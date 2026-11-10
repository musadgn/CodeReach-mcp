using System.Text.Json.Nodes;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>routes, entrypoints, architecture, status, reindex and impact.</summary>
internal static class SurveyTools
{
    private const string FallbackPolicy = "P:Microsoft.AspNetCore.Authorization.AuthorizationOptions.FallbackPolicy";
    private const string AuthorizeFilter = "M:Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter.#ctor";

    public static async Task<ToolResult> Routes(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var filter = args.Text("filter");
        var verb = args.Text("verb")?.ToUpperInvariant();
        var auth = args.Text("auth") ?? "any";
        var all = graph.Endpoints.Where(e => args.Flag("include_tests") || !e.IsTest).ToList();
        var shown = all
            .Where(e => filter is null || e.Template.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || Render.Symbol(graph, e.Handler).Contains(filter, StringComparison.OrdinalIgnoreCase)
                || Render.Auth(e).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(e => verb is null || e.Verb.Split(',').Contains(verb) || e.Verb == "ANY")
            .Where(e => auth switch
            {
                "none" => e.Auth.Count == 0,
                "anonymous" => e.AllowsAnonymous,
                "declared" => e.HasDeclaredAuth,
                _ => true,
            })
            .ToList();
        var open = all.Count(e => e.Auth.Count == 0);
        var report = new Report()
            .Line($"{shown.Count} of {all.Count} endpoints ({all.Count(e => !e.Minimal)} controller actions, {all.Count(e => e.Minimal)} minimal API).")
            .Line($"Authorization: {all.Count(e => e.HasDeclaredAuth)} declared, {all.Count(e => e.AllowsAnonymous)} anonymous allowed, {open} none declared.");
        foreach (var note in GlobalAuthNotes(graph))
        {
            report.Line(note);
        }

        report.Line();
        foreach (var endpoint in shown.Take(args.Number("limit")))
        {
            report.Line(Render.Endpoint(graph, endpoint));
        }

        ReachTools.Truncation(report, shown.Count, args.Number("limit"));
        report.Data(new JsonObject
        {
            ["total"] = all.Count,
            ["shown"] = shown.Count,
            ["declared"] = all.Count(e => e.HasDeclaredAuth),
            ["anonymous"] = all.Count(e => e.AllowsAnonymous),
            ["none"] = open,
            ["global"] = new JsonArray([.. GlobalAuthNotes(graph).Select(n => (JsonNode)n)]),
            ["endpoints"] = Data.Rows(shown.Take(args.Number("limit")), e => Data.Endpoint(graph, e)),
        });
        return report.Finish(lease);
    }

    public static async Task<ToolResult> EntryPoints(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var kind = args.Text("kind");
        var entries = graph.EntryPoints.Values.SelectMany(list => list)
            .Where(e => args.Flag("include_tests") || !graph.Node(e.Symbol).IsTest)
            .Where(e => kind is null || e.Kind == kind)
            .OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Label, StringComparer.Ordinal).ThenBy(e => e.Symbol, StringComparer.Ordinal)
            .ToList();
        var summary = string.Join(", ", entries.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}"));
        var report = new Report().Line($"{entries.Count} entry points{(entries.Count > 0 ? ": " + summary : string.Empty)}.");
        foreach (var entry in entries.Take(args.Number("limit")))
        {
            var node = graph.Node(entry.Symbol);
            report.Line($"  [{entry.Kind}] {entry.Label}  ->  {node.Fact.Display}  {node.Location}");
        }

        ReachTools.Truncation(report, entries.Count, args.Number("limit"));
        return report.Finish(lease);
    }

    public static async Task<ToolResult> Architecture(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var declared = graph.Nodes.Where(n => !n.IsExternal).ToList();
        var report = new Report().Line($"solution {Path.GetFileName(graph.Facts.SolutionPath)}: {graph.Projects.Count} projects, {graph.Facts.Documents.Length} documents, {declared.Count} symbols, {graph.Refs.Count} references.");
        report.Line().Line("Projects (references are project references; -> counts are bound references into that project):");
        var cross = graph.Refs
            .Select(r => (From: r.Project, To: graph.Node(r.Fact.To)))
            .Where(p => !p.To.IsExternal && p.To.Project != p.From)
            .GroupBy(p => (p.From, p.To.Project)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var project in graph.Projects.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var uses = cross.Where(c => c.Key.From == project.Name).OrderByDescending(c => c.Value).Select(c => $"{c.Key.Project} ({c.Value})");
            var test = project.IsTest ? $"  [test: {project.TestEvidence}]" : string.Empty;
            var skipped = project.Skipped > 0 ? $", {project.Skipped} not indexed" : string.Empty;
            report.Line($"  {project.Name}: {project.Documents} documents{skipped}{test}");
            report.Line($"      -> {(cross.Keys.Any(k => k.From == project.Name) ? string.Join(", ", uses) : "nothing in the solution")}");
        }

        var entries = graph.EntryPoints.Values.SelectMany(l => l).Where(e => !graph.Node(e.Symbol).IsTest).GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}");
        report.Line().Line($"Entry points: {string.Join(", ", entries)}.");
        var limit = args.Number("limit");
        report.Line().Line("Most called (non-test call sites, dispatch resolved):");
        var hot = declared
            .Where(n => !n.IsTest && n.Fact.Kind is SymKind.Method && !graph.IsInterfaceMember(n.Id))
            .Select(n => (Node: n, Sites: graph.Callers(n.Id).Count(e => !e.Row.IsTest)))
            .Where(h => h.Sites > 0).OrderByDescending(h => h.Sites).ThenBy(h => h.Node.Id, StringComparer.Ordinal).Take(limit);
        var hotList = hot.ToList();
        foreach (var (node, sites) in hotList)
        {
            report.Line($"  {sites,5}  {node.Fact.Display}  {node.Location}");
        }

        report.Line().Line("Interfaces by number of implementing types:");
        var contracts = graph.Types.Values.Where(t => t.Fact.IsInterface && !t.IsTest)
            .Select(t => (Type: t, Count: graph.Types.Values.Count(c => c.IsConcrete && !c.IsTest && c.BaseDefs.Contains(t.Id))))
            .Where(c => c.Count > 0).OrderByDescending(c => c.Count).ThenBy(c => c.Type.Id, StringComparer.Ordinal).Take(limit);
        var contractList = contracts.ToList();
        foreach (var (type, count) in contractList)
        {
            report.Line($"  {count,5}  {Render.Symbol(graph, type.Id)}");
        }

        report.Data(new JsonObject
        {
            ["solution"] = Path.GetFileName(graph.Facts.SolutionPath),
            ["documents"] = graph.Facts.Documents.Length,
            ["symbols"] = declared.Count,
            ["references"] = graph.Refs.Count,
            ["endpoints"] = graph.Endpoints.Count(e => !e.IsTest),
            ["projects"] = Data.Rows(graph.Projects.Values.OrderBy(p => p.Name, StringComparer.Ordinal), p => new JsonObject
            {
                ["name"] = p.Name,
                ["documents"] = p.Documents,
                ["test"] = p.IsTest,
                ["failed"] = p.LoadFailure,
                ["uses"] = Data.Rows(cross.Where(c => c.Key.From == p.Name).OrderByDescending(c => c.Value), c => new JsonObject { ["project"] = c.Key.Project, ["references"] = c.Value }),
            }),
            ["entryPoints"] = Data.Rows(graph.EntryPoints.Values.SelectMany(l => l).Where(e => !graph.Node(e.Symbol).IsTest).GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal),
                g => new JsonObject { ["kind"] = g.Key, ["count"] = g.Count() }),
            ["mostCalled"] = Data.Rows(hotList, h => { var item = Data.Symbol(graph, h.Node.Id); item["sites"] = h.Sites; return item; }),
            ["mostImplemented"] = Data.Rows(contractList, c => { var item = Data.Symbol(graph, c.Type.Id); item["implementations"] = c.Count; return item; }),
        });
        return report.Finish(lease);
    }

    public static async Task<ToolResult> Status(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var config = host.Config;
        var report = new Report().Line(host.DescribeState())
            .Line($"solution: {graph.Facts.SolutionPath}")
            .Line($"indexed: {graph.Facts.Documents.Length} documents, {graph.Nodes.Count(n => !n.IsExternal)} symbols, {graph.Refs.Count} references, {graph.Endpoints.Count} endpoints, {graph.EntryPoints.Count} entry points.");
        foreach (var project in graph.Projects.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var state = project.LoadFailure is null ? "ok" : "FAILED: " + project.LoadFailure;
            var test = project.IsTest ? $", test project ({project.TestEvidence})" : string.Empty;
            report.Line($"  {project.Name}: {project.Documents} documents, {project.Skipped} not indexed{test}; {state}");
        }

        report.Line($"config: exclude=[{string.Join(", ", config.Exclude)}] authAttributes=[{string.Join(", ", config.AuthAttributes)}] "
            + $"authEndpointMethods=[{string.Join(", ", config.AuthEndpointMethods)}] testProjects=[{string.Join(", ", config.TestProjects)}] "
            + $"entryPoints={config.EntryPoints.Length} custom + {(config.BuiltInEntryPoints ? CodeReachConfig.BuiltInRoots.Count : 0)} built-in rules");
        foreach (var warning in graph.Facts.LoadWarnings.Take(5))
        {
            report.Line("warning: " + warning);
        }

        return report.Finish(lease);
    }

    public static async Task<ToolResult> Reindex(IIndexHost host, Args args, CancellationToken cancel)
    {
        var summary = await host.ReindexAsync(args.Flag("full"), cancel).ConfigureAwait(false);
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        return new Report().Line(summary).Finish(lease);
    }

    public static async Task<ToolResult> Impact(IIndexHost host, Args args, CancellationToken cancel)
    {
        var diff = args.Text("diff");
        var source = diff is not null ? "the diff passed in" : $"working tree vs {args.Text("base") ?? "HEAD"}";
        diff ??= await host.GitDiffAsync(args.Text("base"), cancel).ConfigureAwait(false);
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var changes = DiffParser.Parse(diff);
        var changed = new Dictionary<string, SymbolNode>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        foreach (var change in changes)
        {
            var file = graph.Files.FirstOrDefault(f => f == change.Path || change.Path.EndsWith("/" + f, StringComparison.Ordinal) || f.EndsWith("/" + change.Path, StringComparison.Ordinal));
            if (file is null || change.Deleted)
            {
                unmapped.Add(change.Path + (change.Deleted ? " (deleted)" : string.Empty));
                continue;
            }

            foreach (var symbol in change.Ranges.SelectMany(range => Touched(graph, file, range.Start, range.End)))
            {
                changed.TryAdd(symbol.Id, symbol);
            }
        }

        var affected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dead = new List<SymbolNode>();
        foreach (var symbol in changed.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var trace = Reach.Trace(graph, new Selection([symbol], null, symbol.Fact.Display, null), args.Flag("include_tests"), Limits.DefaultTraceDepth);
            foreach (var path in trace.Rooted)
            {
                if (!affected.TryGetValue(path[0].Symbol, out var list))
                {
                    affected[path[0].Symbol] = list = [];
                }

                list.Add(symbol.Fact.Display);
            }

            if (trace.Rooted.Count == 0)
            {
                dead.Add(symbol);
            }
        }

        var entries = affected.Keys.Select(id => graph.EntryPoints[id][0]).ToList();
        var endpoints = graph.Endpoints.Where(e => affected.ContainsKey(e.Handler)).ToList();
        var report = new Report()
            .Line($"impact of {source}: {changes.Count} files, {changed.Count} changed symbols -> {endpoints.Count} endpoints and {entries.Count(e => e.Kind != "endpoint")} other entry points affected.");
        if (unmapped.Count > 0)
        {
            report.Line($"Not analysed ({unmapped.Count} files that are not indexed C# sources, or were deleted): {string.Join(", ", unmapped.Take(10))}{(unmapped.Count > 10 ? ", ..." : string.Empty)}");
        }

        var limit = args.Number("limit");
        report.Line().Line("Affected endpoints:");
        foreach (var endpoint in endpoints.Take(limit))
        {
            report.Line(Render.Endpoint(graph, endpoint)).Line($"          changed: {string.Join(", ", affected[endpoint.Handler].Distinct())}");
        }

        ReachTools.Truncation(report, endpoints.Count, limit);
        report.Line().Line("Affected jobs and other entry points:");
        foreach (var entry in entries.Where(e => e.Kind != "endpoint").OrderBy(e => e.Label, StringComparer.Ordinal).Take(limit))
        {
            report.Line($"  [{entry.Kind}] {entry.Label}  ->  {Render.Symbol(graph, entry.Symbol)}  {graph.Node(entry.Symbol).Location}")
                .Line($"          changed: {string.Join(", ", affected[entry.Symbol].Distinct())}");
        }

        report.Line().Line($"Changed symbols that reach no entry point ({dead.Count}):");
        foreach (var symbol in dead.Take(limit))
        {
            report.Line($"  {symbol.Fact.Display}  {symbol.Location}");
        }

        ReachTools.Truncation(report, dead.Count, limit);
        report.Data(new JsonObject
        {
            ["source"] = source,
            ["files"] = changes.Count,
            ["unmapped"] = new JsonArray([.. unmapped.Select(u => (JsonNode)u)]),
            ["changed"] = Data.Rows(changed.Values.OrderBy(c => c.Id, StringComparer.Ordinal), c => Data.Symbol(graph, c.Id)),
            ["endpoints"] = Data.Rows(endpoints, e =>
            {
                var item = Data.Endpoint(graph, e);
                item["because"] = new JsonArray([.. affected[e.Handler].Distinct().Select(c => (JsonNode)c)]);
                return item;
            }),
            ["entryPoints"] = Data.Rows(entries.Where(e => e.Kind != "endpoint").OrderBy(e => e.Label, StringComparer.Ordinal), e =>
            {
                var item = Data.Symbol(graph, e.Symbol);
                item["entryKind"] = e.Kind;
                item["entryLabel"] = e.Label;
                item["because"] = new JsonArray([.. affected[e.Symbol].Distinct().Select(c => (JsonNode)c)]);
                return item;
            }),
            ["unreached"] = Data.Rows(dead, d => Data.Symbol(graph, d.Id)),
        });
        return report.Finish(lease);
    }

    /// <summary>The innermost declarations a line range touches. A range that touches only a type's own lines changes all its members.</summary>
    private static IEnumerable<SymbolNode> Touched(CodeGraph graph, string file, int start, int end)
    {
        var hits = graph.SymbolsInFile(file).Where(s => s.Fact.StartLine <= end && start <= s.Fact.EndLine).ToList();
        var members = hits.Where(s => s.Fact.Kind != SymKind.Type).ToList();
        foreach (var member in members)
        {
            yield return member;
        }

        foreach (var type in hits.Where(s => s.Fact.Kind == SymKind.Type))
        {
            var own = graph.MembersOf(type.Id).Where(m => m.File == file).ToList();
            var covered = Enumerable.Range(start, end - start + 1)
                .All(line => line < type.Fact.StartLine || line > type.Fact.EndLine || own.Any(m => m.Fact.StartLine <= line && line <= m.Fact.EndLine));
            if (!covered)
            {
                foreach (var member in graph.MembersOf(type.Id))
                {
                    yield return member;
                }
            }
        }
    }

    private static IEnumerable<string> GlobalAuthNotes(CodeGraph graph)
    {
        foreach (var row in graph.RowsTo(FallbackPolicy).Where(r => r.Fact.Kind == RefKind.Write && !r.IsTest))
        {
            yield return $"Global: a fallback authorization policy is set at {Render.Site(row)}; endpoints with none declared may still require it.";
        }

        var filters = graph.Nodes.Where(n => n.Id.StartsWith(AuthorizeFilter, StringComparison.Ordinal))
            .SelectMany(n => graph.RowsTo(n.Id)).Where(r => !r.IsTest);
        foreach (var row in filters)
        {
            yield return $"Global: an AuthorizeFilter is constructed at {Render.Site(row)}; if it is added to MVC options it protects every controller action.";
        }
    }
}
