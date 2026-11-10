using CodeReach.Core.Facts;
using CodeReach.Core.Graph;

namespace CodeReach.Core.Tools;

/// <summary>Shared wording, so every tool describes a symbol, a call and an endpoint the same way.</summary>
internal static class Render
{
    public static string Symbol(CodeGraph graph, string id) => graph.Node(id).Fact.Display;

    public static string Via(CodeGraph graph, CallEdge edge)
    {
        if (edge.Via is null)
        {
            return string.Empty;
        }

        var bound = edge.Row.Fact.To;
        if (bound == edge.Target)
        {
            return string.Empty;
        }

        var candidates = graph.Reachable(edge.Row).Count;
        var count = candidates > 1 ? $", 1 of {candidates} possible targets" : string.Empty;
        return $"  (bound to {Symbol(graph, bound)}{count})";
    }

    public static string Site(RefRow row) => $"{row.File}:{row.Fact.Line}";

    public static string Auth(Endpoint endpoint)
    {
        if (endpoint.Auth.Count == 0)
        {
            return "none declared";
        }

        var parts = endpoint.Auth.Select(a =>
        {
            var arguments = a.Args.Length > 0 ? $"({a.Args})" : string.Empty;
            return $"{a.Name}{arguments} [{a.Scope}]";
        });
        var prefix = endpoint.AllowsAnonymous ? "anonymous allowed: " : string.Empty;
        return prefix + string.Join("; ", parts);
    }

    public static string Endpoint(CodeGraph graph, Endpoint endpoint) =>
        $"{endpoint.Verb,-7} {endpoint.Template}  ->  {Symbol(graph, endpoint.Handler)}  {endpoint.File}:{endpoint.Line}\n          auth: {Auth(endpoint)}";

    public static string Hidden(int testSites) =>
        testSites == 0 ? string.Empty : $" Hidden: {testSites} call sites in test projects (include_tests=true shows them).";

    public static string Kind(SymKind kind) => kind.ToString().ToLowerInvariant();

    public static string Kind(RefKind kind) => kind switch
    {
        RefKind.Invoke => "call",
        RefKind.Construct => "new",
        RefKind.MethodGroup => "method-group",
        RefKind.Read => "read",
        RefKind.Write => "write",
        RefKind.NameOf => "nameof",
        RefKind.TypeRef => "type",
        RefKind.Attribute => "attribute",
        _ => "base-call",
    };
}
