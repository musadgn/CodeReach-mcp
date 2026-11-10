using CodeReach.Core.Facts;

namespace CodeReach.Core.Graph;

/// <summary>Joins endpoint facts with the route prefixes and group authorization that reach them through parameters.</summary>
public static class EndpointResolver
{
    public const string UnresolvedPrefix = "{unresolved-prefix}";

    /// <summary>Callee used for authorization applied to every controller (<c>MapControllers().RequireAuthorization()</c>).</summary>
    public const string AllControllers = "*controllers";

    internal static IReadOnlyList<Endpoint> Resolve(
        IndexFacts facts,
        IReadOnlyDictionary<string, ProjectFact> projects,
        Func<IEnumerable<string>, IReadOnlyList<string>> upOf)
    {
        var groupArgs = facts.Documents.SelectMany(d => d.GroupArgs).ToLookup(g => (g.Callee, g.Ordinal));
        var global = groupArgs[(AllControllers, -1)].SelectMany(g => g.Auth).ToArray();
        var result = new List<Endpoint>();
        foreach (var doc in facts.Documents)
        {
            var isTest = projects.TryGetValue(doc.Project, out var project) && project.IsTest;
            foreach (var endpoint in doc.Endpoints)
            {
                var prefixes = endpoint.Prefix is null
                    ? [(Prefix: string.Empty, Auth: endpoint.Minimal ? [] : global)]
                    : Prefixes(endpoint.Prefix, groupArgs, upOf, 0);
                foreach (var (prefix, auth) in prefixes)
                {
                    result.Add(new Endpoint(
                        endpoint.Handler, endpoint.Verb, Join(prefix, endpoint.Template), endpoint.Minimal, doc.Path,
                        endpoint.Line, doc.Project, isTest, [.. auth, .. endpoint.Auth]));
                }
            }
        }

        return result
            .DistinctBy(e => (e.Handler, e.Verb, e.Template))
            .OrderBy(e => e.Template, StringComparer.Ordinal).ThenBy(e => e.Verb, StringComparer.Ordinal)
            .ThenBy(e => e.Handler, StringComparer.Ordinal)
            .ToList();
    }

    public static string Join(string prefix, string template)
    {
        var parts = new[] { prefix, template }
            .Select(p => p.Trim().TrimStart('~').Trim('/'))
            .Where(p => p.Length > 0);
        return "/" + string.Join('/', parts);
    }

    private static List<(string Prefix, AuthFact[] Auth)> Prefixes(
        PrefixParam parameter,
        ILookup<(string, int), GroupArgFact> groupArgs,
        Func<IEnumerable<string>, IReadOnlyList<string>> upOf,
        int hops)
    {
        var found = new List<(string, AuthFact[])>();
        if (hops < Limits.MaxPrefixHops)
        {
            string[] self = [parameter.Method];
            foreach (var callee in self.Concat(upOf(self)))
            {
                foreach (var argument in groupArgs[(callee, parameter.Ordinal)])
                {
                    if (argument.Via is null)
                    {
                        found.Add((argument.Prefix, argument.Auth));
                        continue;
                    }

                    foreach (var (outer, outerAuth) in Prefixes(argument.Via, groupArgs, upOf, hops + 1))
                    {
                        found.Add((Join(outer, argument.Prefix), [.. outerAuth, .. argument.Auth]));
                    }
                }
            }
        }

        if (found.Count == 0)
        {
            found.Add((UnresolvedPrefix, []));
        }

        return found;
    }
}
