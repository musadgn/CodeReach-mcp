using System.Text.Json.Nodes;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>
/// Data for the explorer screens: the file tree, one file with its declarations and impact, the graph around
/// a symbol, and the details of one node. Every relation comes from the same graph methods the tools use
/// (<see cref="CodeGraph.Callers"/>, <see cref="CodeGraph.Callees"/>, dispatch), so a picture cannot disagree
/// with an answer.
/// </summary>
public static class Explorer
{
    public const int MaxGraphNodes = 400;

    public static readonly IReadOnlyList<string> EdgeKinds = ["calls", "new", "reads", "writes", "uses-type", "implements", "overrides", "inherits", "member"];

    public static JsonObject Files(CodeGraph graph)
    {
        var errors = graph.Facts.Documents.ToDictionary(d => d.Path, d => d.Errors, StringComparer.Ordinal);
        return new JsonObject
        {
            ["solution"] = Path.GetFileName(graph.Facts.SolutionPath),
            ["projects"] = Data.Rows(graph.Projects.Values.OrderBy(p => p.IsTest).ThenBy(p => p.Name, StringComparer.Ordinal), project => new JsonObject
            {
                ["name"] = project.Name,
                ["test"] = project.IsTest,
                ["failed"] = project.LoadFailure,
                ["files"] = Data.Rows(
                    graph.Facts.Documents.Where(d => d.Project == project.Name).OrderBy(d => d.Path, StringComparer.Ordinal),
                    d => new JsonObject
                    {
                        ["path"] = d.Path,
                        ["symbols"] = graph.SymbolsInFile(d.Path).Count(s => s.Fact.Kind != SymKind.Type),
                        ["errors"] = errors[d.Path],
                    }),
            }),
        };
    }

    /// <summary>One indexed file: its text, its declarations, and who outside it depends on them. Null when the path is not indexed.</summary>
    public static JsonObject? File(CodeGraph graph, string path)
    {
        var document = graph.Facts.Documents.FirstOrDefault(d => d.Path == path);
        if (document is null)
        {
            return null;
        }

        var full = System.IO.Path.Combine(graph.Facts.Root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string? text;
        try
        {
            text = System.IO.File.ReadAllText(full);
        }
        catch (IOException)
        {
            text = null;
        }
        catch (UnauthorizedAccessException)
        {
            text = null;
        }

        var declared = graph.SymbolsInFile(path);
        var ids = declared.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var incoming = declared.SelectMany(s => graph.Callers(s.Id)).Where(e => e.Row.File != path && !e.Row.IsTest).ToList();
        var typeUses = declared.Where(s => s.Fact.Kind == SymKind.Type).SelectMany(s => graph.RowsTo(s.Id)).Where(r => r.File != path && !r.IsTest).ToList();
        var testSites = declared.SelectMany(s => graph.Callers(s.Id)).Count(e => e.Row.IsTest);
        var dependentFiles = incoming.Select(e => e.Row.File).Concat(typeUses.Select(r => r.File)).Distinct(StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["path"] = path,
            ["project"] = document.Project,
            ["errors"] = document.Errors,
            ["text"] = text,
            ["symbols"] = Data.Rows(declared.OrderBy(s => s.Fact.StartLine), s =>
            {
                var item = Data.Symbol(graph, s.Id);
                item["start"] = s.Fact.StartLine;
                item["end"] = s.Fact.EndLine;
                item["entry"] = graph.IsEntryPoint(s.Id);
                return item;
            }),
            ["impact"] = new JsonObject
            {
                ["dependentFiles"] = dependentFiles.Count,
                ["callSites"] = incoming.Count,
                ["typeUses"] = typeUses.Count,
                ["testSites"] = testSites,
                ["files"] = new JsonArray([.. dependentFiles.Order(StringComparer.Ordinal).Take(50).Select(f => (JsonNode)f)]),
            },
        };
    }

    /// <summary>
    /// The neighbourhood of a symbol, <paramref name="depth"/> layers out. <paramref name="direction"/> is
    /// out (what it uses), in (what uses it) or both. Framework members are left out unless asked for.
    /// </summary>
    public static JsonObject Neighborhood(
        CodeGraph graph, string id, string direction, int depth, IReadOnlySet<string> kinds, bool includeExternal, bool includeTests)
    {
        var nodes = new Dictionary<string, int>(StringComparer.Ordinal) { [id] = 0 };
        var edges = new Dictionary<(string From, string To, string Kind), int>();
        var frontier = new List<string> { id };
        var truncated = false;
        var outgoing = direction is "out" or "both";
        var incoming = direction is "in" or "both";
        for (var layer = 1; layer <= depth && frontier.Count > 0 && !truncated; layer++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var (from, to, kind) in Relations(graph, current, outgoing, incoming, includeTests))
                {
                    if (!kinds.Contains(kind))
                    {
                        continue;
                    }

                    var other = from == current ? to : from;
                    if (!includeExternal && graph.Node(other).IsExternal)
                    {
                        continue;
                    }

                    if (!nodes.ContainsKey(other))
                    {
                        if (nodes.Count >= MaxGraphNodes)
                        {
                            truncated = true;
                            continue;
                        }

                        nodes[other] = layer;
                        next.Add(other);
                    }

                    edges[(from, to, kind)] = edges.GetValueOrDefault((from, to, kind)) + 1;
                }
            }

            frontier = next;
        }

        return new JsonObject
        {
            ["center"] = id,
            ["direction"] = direction,
            ["depth"] = depth,
            ["truncated"] = truncated,
            ["nodes"] = Data.Rows(nodes, n => Node(graph, n.Key, n.Value)),
            ["edges"] = Data.Rows(edges, e => new JsonObject
            {
                ["from"] = e.Key.From,
                ["to"] = e.Key.To,
                ["kind"] = e.Key.Kind,
                ["count"] = e.Value,
            }),
        };
    }

    /// <summary>The projects and how many bound references each makes into the others.</summary>
    public static JsonObject ProjectGraph(CodeGraph graph)
    {
        var cross = graph.Refs
            .Select(r => (From: r.Project, To: graph.Node(r.Fact.To)))
            .Where(p => !p.To.IsExternal && p.To.Project != p.From)
            .GroupBy(p => (p.From, p.To.Project)).ToDictionary(g => g.Key, g => g.Count());
        return new JsonObject
        {
            ["nodes"] = Data.Rows(graph.Projects.Values.OrderBy(p => p.Name, StringComparer.Ordinal), p => new JsonObject
            {
                ["id"] = "project:" + p.Name,
                ["display"] = p.Name,
                ["kind"] = p.IsTest ? "test project" : "project",
                ["documents"] = p.Documents,
                ["test"] = p.IsTest,
                ["layer"] = 0,
            }),
            ["edges"] = Data.Rows(cross, c => new JsonObject
            {
                ["from"] = "project:" + c.Key.From,
                ["to"] = "project:" + c.Key.Project,
                ["kind"] = "references",
                ["count"] = c.Value,
            }),
        };
    }

    /// <summary>Everything the selection panel shows about one node.</summary>
    public static JsonObject Details(CodeGraph graph, string id)
    {
        var node = graph.Node(id);
        var item = Data.Symbol(graph, id);
        var callers = graph.Callers(id);
        item["callSites"] = callers.Count(e => !e.Row.IsTest);
        item["testCallSites"] = callers.Count(e => e.Row.IsTest);
        item["callers"] = callers.Where(e => !e.Row.IsTest).Select(e => e.Row.Fact.From).Distinct(StringComparer.Ordinal).Count();
        item["callees"] = graph.Callees(id).Select(e => e.Target).Distinct(StringComparer.Ordinal).Count();
        item["usages"] = graph.RowsTo(id).Count(r => !r.IsTest);
        item["entryPoints"] = Data.Rows(graph.EntryPoints.GetValueOrDefault(id) ?? [], e => new JsonObject { ["kind"] = e.Kind, ["label"] = e.Label });
        item["endpoints"] = Data.Rows(graph.Endpoints.Where(e => e.Handler == id), e => Data.Endpoint(graph, e));
        item["implements"] = new JsonArray([.. graph.UpOf(id).Select(u => (JsonNode)Data.Symbol(graph, u))]);
        item["implementations"] = new JsonArray([.. graph.ImplementationsOf(id).Select(i => i.Impl).Concat(graph.OverridersOf(id)).Distinct(StringComparer.Ordinal).Select(i => (JsonNode)Data.Symbol(graph, i))]);
        item["project"] = node.IsExternal ? null : node.Project;
        if (node.Fact.Kind == SymKind.Type)
        {
            item["members"] = graph.MembersOf(id).Count;
            item["derived"] = graph.Types.Values.Count(t => t.BaseDefs.Contains(id) && !t.IsTest);
            item["registrations"] = Data.Rows(graph.Registrations.Where(r => Ids.DefOf(r.Fact.Service) == id || (r.Fact.Implementation is { } i && Ids.DefOf(i) == id)), r => new JsonObject
            {
                ["lifetime"] = r.Fact.Lifetime,
                ["method"] = r.Fact.Method,
                ["file"] = r.File,
                ["line"] = r.Fact.Line,
            });
        }

        return item;
    }

    private static JsonObject Node(CodeGraph graph, string id, int layer)
    {
        var item = Data.Symbol(graph, id);
        item["layer"] = layer;
        item["external"] = graph.Node(id).IsExternal;
        item["entry"] = graph.IsEntryPoint(id);
        if (graph.EntryPoints.TryGetValue(id, out var entries))
        {
            item["entryKind"] = entries[0].Kind;
            item["entryLabel"] = entries[0].Label;
        }

        var endpoint = graph.Endpoints.FirstOrDefault(e => e.Handler == id);
        if (endpoint is not null)
        {
            item["authState"] = endpoint.AllowsAnonymous ? "anonymous" : endpoint.HasDeclaredAuth ? "declared" : "none";
        }

        return item;
    }

    private static IEnumerable<(string From, string To, string Kind)> Relations(CodeGraph graph, string id, bool outgoing, bool incoming, bool includeTests)
    {
        var node = graph.Node(id);
        if (outgoing)
        {
            foreach (var edge in graph.Callees(id))
            {
                if (includeTests || !edge.Row.IsTest)
                {
                    yield return (id, edge.Target, KindOf(edge.Row.Fact.Kind));
                }
            }

            foreach (var row in graph.RowsFrom(id).Where(r => r.Fact.Kind is RefKind.TypeRef && (includeTests || !r.IsTest)))
            {
                yield return (id, row.Fact.To, "uses-type");
            }

            foreach (var up in graph.UpOf(id))
            {
                yield return (id, up, graph.IsInterfaceMember(up) ? "implements" : "overrides");
            }

            if (node.Fact.Kind == SymKind.Type)
            {
                foreach (var member in graph.MembersOf(id).Where(m => m.Fact.Kind != SymKind.Type))
                {
                    yield return (id, member.Id, "member");
                }

                if (graph.Types.TryGetValue(id, out var type))
                {
                    foreach (var baseDef in type.BaseDefs.Where(b => b != "T:System.Object"))
                    {
                        yield return (id, baseDef, "inherits");
                    }
                }
            }
        }

        if (incoming)
        {
            foreach (var edge in graph.Callers(id))
            {
                if (includeTests || !edge.Row.IsTest)
                {
                    yield return (edge.Row.Fact.From, id, KindOf(edge.Row.Fact.Kind));
                }
            }

            foreach (var row in graph.RowsTo(id).Where(r => r.Fact.Kind is RefKind.TypeRef && (includeTests || !r.IsTest)))
            {
                yield return (row.Fact.From, id, "uses-type");
            }

            foreach (var implementation in graph.ImplementationsOf(id).Select(i => i.Impl).Concat(graph.OverridersOf(id)).Distinct(StringComparer.Ordinal))
            {
                yield return (implementation, id, graph.IsInterfaceMember(id) ? "implements" : "overrides");
            }

            if (node.Fact.Kind == SymKind.Type)
            {
                foreach (var sub in graph.Types.Values.Where(t => t.BaseDefs.Contains(id) && (includeTests || !t.IsTest)))
                {
                    yield return (sub.Id, id, "inherits");
                }
            }
            else if (node.Fact.TypeId is { } owner)
            {
                yield return (owner, id, "member");
            }
        }
    }

    private static string KindOf(RefKind kind) => kind switch
    {
        RefKind.Construct => "new",
        RefKind.Read => "reads",
        RefKind.Write => "writes",
        _ => "calls",
    };

    /// <summary>Resolves the text an explorer search typed, refusing ambiguity the way the tools do.</summary>
    public static string Resolve(CodeGraph graph, string text) => SymbolResolver.Resolve(graph, text).Symbols[0].Id;
}
