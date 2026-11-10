using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>callers, callees, find_usages, find_symbol and trace_to_entrypoints.</summary>
internal static class ReachTools
{
    public static async Task<ToolResult> FindSymbol(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var query = args.Required("query");
        var kind = args.Text("kind");
        var includeTests = args.Flag("include_tests");
        var matches = graph.Nodes
            .Where(n => !n.IsExternal && (includeTests || !n.IsTest) && (kind is null || Render.Kind(n.Fact.Kind) == kind))
            .Where(n => n.Fact.QName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Fact.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : n.Fact.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(n => n.Fact.QName, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        var report = new Report().Line($"{matches.Count} declared symbols match '{query}'.");
        foreach (var node in matches.Take(args.Number("limit")))
        {
            report.Line($"  {Render.Kind(node.Fact.Kind),-11} {node.Fact.Display}  {node.Location}\n              id: {node.Id}");
        }

        Truncation(report, matches.Count, args.Number("limit"));
        return report.Finish(lease);
    }

    public static async Task<ToolResult> Callers(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var selection = SymbolResolver.Resolve(graph, args.Required("symbol"));
        var includeTests = args.Flag("include_tests");
        var (edges, hidden) = Reach.Callers(graph, selection, includeTests);
        var byCaller = edges.GroupBy(e => e.Row.Fact.From).ToList();
        var types = byCaller.Select(g => graph.Node(g.Key).Fact.TypeId ?? g.Key).Distinct().Count();
        var report = new Report().Line($"callers of {selection.Label}");
        Note(report, selection);
        report.Line($"{byCaller.Count} callers in {types} types, {edges.Count} call sites.{Render.Hidden(hidden)}");
        var budget = args.Number("limit");
        var seen = new HashSet<string>(selection.Symbols.Select(s => s.Id), StringComparer.Ordinal);
        WriteCallers(report, graph, byCaller, 1, args.Number("depth"), includeTests, seen, ref budget);
        if (budget <= 0)
        {
            report.Line($"  ... output stopped at limit={args.Number("limit")}. Raise limit, or lower depth.");
        }

        return report.Finish(lease);
    }

    public static async Task<ToolResult> Callees(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var selection = SymbolResolver.Resolve(graph, args.Required("symbol"));
        var all = selection.Symbols.SelectMany(s => graph.Callees(s.Id)).ToList();
        all = all.Where(e => graph.Node(e.Target).Fact.Kind != SymKind.Field).ToList();
        var external = args.Flag("include_external") ? all : all.Where(e => !graph.Node(e.Target).IsExternal).ToList();
        var targets = external.GroupBy(e => e.Target).OrderBy(g => g.Min(e => e.Row.Fact.Line)).ToList();
        var report = new Report().Line($"callees of {selection.Label}");
        Note(report, selection);
        report.Line($"{targets.Count} distinct targets from {external.Count} call sites."
            + (all.Count == external.Count ? string.Empty : $" Hidden: {all.Count - external.Count} sites that reach symbols with no source in the index (include_external=true shows them)."));
        foreach (var symbol in selection.Symbols.Where(s => graph.IsInterfaceMember(s.Id) || (s.Fact.Flags & SymFlags.Abstract) != 0))
        {
            var implementations = graph.ImplementationsOf(symbol.Id).Select(i => i.Impl).Concat(graph.OverridersOf(symbol.Id)).Distinct().ToList();
            report.Line($"  {symbol.Fact.Display} has no body; {implementations.Count} implementations: {string.Join(", ", implementations.Select(i => Render.Symbol(graph, i)))}");
        }

        foreach (var target in targets.Take(args.Number("limit")))
        {
            var node = graph.Node(target.Key);
            var sites = string.Join(", ", target.Select(e => e.Row.Fact.Line).Distinct().Order().Select(l => ":" + l));
            var first = target.First();
            report.Line($"  {Render.Kind(first.Row.Fact.Kind),-12} {node.Fact.Display}  {node.Location}  at {first.Row.File}{sites}{Render.Via(graph, first)}");
        }

        Truncation(report, targets.Count, args.Number("limit"));
        return report.Finish(lease);
    }

    public static async Task<ToolResult> FindUsages(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var selection = SymbolResolver.Resolve(graph, args.Required("symbol"));
        var includeTests = args.Flag("include_tests");
        var all = selection.Symbols.SelectMany(s => graph.RowsTo(s.Id)).OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Fact.Line).ToList();
        var rows = includeTests ? all : all.Where(r => !r.IsTest).ToList();
        var kinds = string.Join(", ", rows.GroupBy(r => r.Fact.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {Render.Kind(g.Key)}"));
        var report = new Report().Line($"usages of {selection.Label}");
        Note(report, selection);
        report.Line($"{rows.Count} usages in {rows.Select(r => r.File).Distinct().Count()} files{(rows.Count > 0 ? ": " + kinds : string.Empty)}."
            + (all.Count == rows.Count ? string.Empty : $" Hidden: {all.Count - rows.Count} usages in test projects (include_tests=true shows them)."));
        foreach (var file in rows.Take(args.Number("limit")).GroupBy(r => r.File))
        {
            report.Line($"  {file.Key}");
            foreach (var row in file)
            {
                report.Line($"    :{row.Fact.Line,-5} {Render.Kind(row.Fact.Kind),-12} in {Render.Symbol(graph, row.Fact.From)}");
            }
        }

        Truncation(report, rows.Count, args.Number("limit"));
        foreach (var symbol in selection.Symbols)
        {
            foreach (var up in graph.UpClosure(symbol.Id))
            {
                report.Line($"Related: calls bound to {Render.Symbol(graph, up)} can also reach {symbol.Fact.Display}; `callers` counts them, this list does not.");
            }
        }

        return report.Finish(lease);
    }

    public static async Task<ToolResult> Trace(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var selection = SymbolResolver.Resolve(graph, args.Required("symbol"));
        var trace = Reach.Trace(graph, selection, args.Flag("include_tests"), args.Number("max_depth"));
        var report = new Report().Line($"entry points that reach {selection.Label}");
        Note(report, selection);
        WriteTrace(report, graph, trace, args.Number("limit"));
        return report.Finish(lease);
    }

    public static void WriteTrace(Report report, CodeGraph graph, TraceResult trace, int limit)
    {
        var rooted = trace.Rooted
            .Select(path => (Path: path, Entry: graph.EntryPoints[path[0].Symbol].OrderBy(e => e.Kind, StringComparer.Ordinal).First()))
            .OrderBy(p => p.Entry.Kind, StringComparer.Ordinal).ThenBy(p => p.Entry.Label, StringComparer.Ordinal).ToList();
        var kinds = string.Join(", ", rooted.GroupBy(p => p.Entry.Kind).Select(g => $"{g.Count()} {g.Key}"));
        report.Line($"{rooted.Count} entry points{(rooted.Count > 0 ? " (" + kinds + ")" : string.Empty)}, {trace.Unrooted.Count} unrooted tops; {trace.Visited} symbols visited."
            + Render.Hidden(trace.HiddenTestSites));
        if (trace.Truncated)
        {
            report.Line("TRUNCATED: the depth or size cap stopped the walk, so entry points may be missing. Raise max_depth.");
        }

        if (rooted.Count == 0 && trace.Unrooted.Count == 0 && trace.Visited > 0 && !trace.Truncated)
        {
            report.Line("Every caller chain loops back on itself: these symbols only call each other and nothing outside reaches them.");
        }

        foreach (var (path, entry) in rooted.Take(limit))
        {
            var endpoints = graph.Endpoints.Where(e => e.Handler == entry.Symbol).ToList();
            var label = endpoints.Count > 0
                ? string.Join(" | ", endpoints.Select(e => $"{e.Verb} {e.Template}")) + "   auth: " + Render.Auth(endpoints[0])
                : entry.Label;
            report.Line().Line($"[{entry.Kind}] {label}");
            WritePath(report, graph, path);
        }

        Truncation(report, rooted.Count, limit);
        foreach (var path in trace.Unrooted.Take(limit))
        {
            report.Line().Line("[unrooted] nothing in the index calls the top of this chain (dependency injection, reflection, or unused code)");
            WritePath(report, graph, path);
        }
    }

    public static void Truncation(Report report, int total, int limit)
    {
        if (total > limit)
        {
            report.Line($"  ... {total - limit} more not shown (limit={limit}; raise it up to {Limits.MaxRows}).");
        }
    }

    public static void Note(Report report, Selection selection)
    {
        if (selection.Note is not null)
        {
            report.Line("note: " + selection.Note);
        }
    }

    private static void WritePath(Report report, CodeGraph graph, IReadOnlyList<TraceHop> path)
    {
        var top = graph.Node(path[0].Symbol);
        report.Line($"  {top.Fact.Display}  {top.Location}");
        for (var i = 0; i < path.Count - 1; i++)
        {
            var edge = path[i].Calls!.Value;
            report.Line($"  -> {Render.Symbol(graph, path[i + 1].Symbol)}  called at {Render.Site(edge.Row)}{Render.Via(graph, edge)}");
        }
    }

    private static void WriteCallers(
        Report report, CodeGraph graph, IEnumerable<IGrouping<string, CallEdge>> byCaller, int level, int depth, bool includeTests,
        HashSet<string> seen, ref int budget)
    {
        foreach (var caller in byCaller.OrderBy(g => graph.Node(g.Key).Fact.Display, StringComparer.Ordinal))
        {
            if (budget-- <= 0)
            {
                return;
            }

            var first = caller.First();
            var lines = string.Join(",", caller.Select(e => e.Row.Fact.Line).Distinct().Order());
            var entry = graph.IsEntryPoint(caller.Key) ? $"  [{graph.EntryPoints[caller.Key][0].Kind}: {graph.EntryPoints[caller.Key][0].Label}]" : string.Empty;
            var test = first.Row.IsTest ? "  [test]" : string.Empty;
            report.Line($"{new string(' ', level * 2)}{Render.Symbol(graph, caller.Key)}  {first.Row.File}:{lines}{Render.Via(graph, first)}{entry}{test}");
            if (level < depth && seen.Add(caller.Key))
            {
                var next = graph.Callers(caller.Key).Where(e => includeTests || !e.Row.IsTest).GroupBy(e => e.Row.Fact.From);
                WriteCallers(report, graph, next, level + 1, depth, includeTests, seen, ref budget);
            }
        }
    }
}
