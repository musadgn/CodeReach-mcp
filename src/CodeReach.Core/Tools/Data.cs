using System.Text.Json.Nodes;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>
/// The structured form of an answer, built from the same objects as its text, so a screen and a text answer
/// can never disagree. Shapes are plain JSON: names, locations and ids, never internal types.
/// </summary>
internal static class Data
{
    public static JsonObject Symbol(CodeGraph graph, string id)
    {
        var node = graph.Node(id);
        return new JsonObject
        {
            ["id"] = id,
            ["display"] = node.Fact.Display,
            ["kind"] = Render.Kind(node.Fact.Kind),
            ["file"] = node.IsExternal ? null : node.File,
            ["line"] = node.IsExternal ? null : node.Fact.Line,
            ["test"] = node.IsTest,
        };
    }

    public static JsonObject Endpoint(CodeGraph graph, Endpoint endpoint) => new()
    {
        ["verb"] = endpoint.Verb,
        ["route"] = endpoint.Template,
        ["minimal"] = endpoint.Minimal,
        ["handler"] = Symbol(graph, endpoint.Handler),
        ["file"] = endpoint.File,
        ["line"] = endpoint.Line,
        ["project"] = endpoint.Project,
        ["authState"] = endpoint.AllowsAnonymous ? "anonymous" : endpoint.HasDeclaredAuth ? "declared" : "none",
        ["auth"] = new JsonArray([.. endpoint.Auth.Select(a => (JsonNode)new JsonObject
        {
            ["kind"] = a.Kind.ToString().ToLowerInvariant(),
            ["name"] = a.Name,
            ["args"] = a.Args,
            ["scope"] = a.Scope,
        })]),
        ["authText"] = Render.Auth(endpoint),
    };

    public static JsonObject Path(CodeGraph graph, IReadOnlyList<TraceHop> path)
    {
        var top = path[0].Symbol;
        var entry = graph.EntryPoints.TryGetValue(top, out var entries)
            ? entries.OrderBy(e => e.Kind, StringComparer.Ordinal).First()
            : null;
        return new JsonObject
        {
            ["entryKind"] = entry?.Kind,
            ["entryLabel"] = entry?.Label,
            ["endpoints"] = new JsonArray([.. graph.Endpoints.Where(e => e.Handler == top).Select(e => (JsonNode)Endpoint(graph, e))]),
            ["hops"] = new JsonArray([.. path.Select(hop =>
            {
                var item = Symbol(graph, hop.Symbol);
                if (hop.Calls is { } call)
                {
                    item["callFile"] = call.Row.File;
                    item["callLine"] = call.Row.Fact.Line;
                    item["boundTo"] = call.Row.Fact.To == call.Target ? null : Render.Symbol(graph, call.Row.Fact.To);
                    item["candidates"] = call.Via is null ? 1 : graph.Reachable(call.Row).Count;
                }

                return (JsonNode)item;
            })]),
        };
    }

    public static JsonObject Trace(CodeGraph graph, TraceResult trace, int limit) => new()
    {
        ["rooted"] = new JsonArray([.. trace.Rooted.Take(limit).Select(p => (JsonNode)Path(graph, p))]),
        ["rootedTotal"] = trace.Rooted.Count,
        ["unrooted"] = new JsonArray([.. trace.Unrooted.Take(limit).Select(p => (JsonNode)Path(graph, p))]),
        ["unrootedTotal"] = trace.Unrooted.Count,
        ["visited"] = trace.Visited,
        ["hiddenTestSites"] = trace.HiddenTestSites,
        ["truncated"] = trace.Truncated,
    };

    public static JsonObject Registration(string service, string implementation, string lifetime, Registration registration) => new()
    {
        ["service"] = service,
        ["implementation"] = implementation,
        ["lifetime"] = lifetime,
        ["method"] = registration.Fact.Method,
        ["detail"] = registration.Fact.Detail,
        ["file"] = registration.File,
        ["line"] = registration.Fact.Line,
    };

    public static JsonArray Rows<T>(IEnumerable<T> items, Func<T, JsonNode> map) => new([.. items.Select(map)]);
}
