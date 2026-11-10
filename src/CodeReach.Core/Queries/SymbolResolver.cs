using System.Text.RegularExpressions;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;

namespace CodeReach.Core.Queries;

/// <summary>What a symbol argument resolved to. <see cref="Through"/> narrows an inherited member to one concrete type.</summary>
public sealed record Selection(IReadOnlyList<SymbolNode> Symbols, TypeNode? Through, string Label, string? Note);

/// <summary>Turns the text an agent typed into graph symbols, or refuses with the candidates.</summary>
public static partial class SymbolResolver
{
    public static Selection Resolve(CodeGraph graph, string query)
    {
        query = query.Trim();
        if (query.Length is 0 or > Limits.MaxSymbolLength)
        {
            throw new ToolRefusal(Codes.ValidationFailed, $"symbol must be 1 to {Limits.MaxSymbolLength} characters.");
        }

        if (IdPattern().IsMatch(query))
        {
            var node = graph.Nodes.FirstOrDefault(n => n.Id == query)
                ?? throw new ToolRefusal(Codes.SymbolNotFound, $"no symbol has the id '{query}'. Call find_symbol to search by name.");
            return new Selection([node], null, node.Fact.Display, null);
        }

        var location = LocationPattern().Match(query);
        if (location.Success)
        {
            return AtLocation(graph, location.Groups[1].Value.Replace('\\', '/'), int.Parse(location.Groups[2].Value));
        }

        return ByName(graph, query);
    }

    [GeneratedRegex(@"^[TMPFEL]:\S")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(.+\.cs):(\d{1,7})$", RegexOptions.IgnoreCase)]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex GenericArguments();

    private static Selection AtLocation(CodeGraph graph, string path, int line)
    {
        var files = graph.Files.Where(f => f == path || f.EndsWith("/" + path.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            throw new ToolRefusal(Codes.SymbolNotFound, $"no indexed file matches '{path}'. Paths are relative to the solution directory; excluded files are not indexed.");
        }

        if (files.Count > 1)
        {
            throw new ToolRefusal(Codes.AmbiguousSymbol, $"'{path}' matches {files.Count} files: {string.Join(", ", files.Take(Limits.MaxCandidatesListed))}. Give a longer path.");
        }

        var best = graph.SymbolsInFile(files[0])
            .Where(s => s.Fact.StartLine <= line && line <= s.Fact.EndLine)
            .OrderBy(s => s.Fact.EndLine - s.Fact.StartLine)
            .ThenBy(s => s.Fact.Kind switch { SymKind.Type => 2, SymKind.Constructor => 1, _ => 0 })
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new ToolRefusal(Codes.SymbolNotFound, $"no declaration spans {files[0]}:{line}.");
        return new Selection([best], null, best.Fact.Display, $"resolved {files[0]}:{line} to {best.Fact.Display}");
    }

    private static Selection ByName(CodeGraph graph, string query)
    {
        var parameters = default(string);
        var open = query.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            parameters = Compact(query[(open + 1)..].TrimEnd(')'));
            query = query[..open];
        }

        string previous;
        do
        {
            previous = query;
            query = GenericArguments().Replace(query, string.Empty);
        }
        while (query != previous);

        query = query.Trim('.', ' ').Replace("..ctor", ".#ctor", StringComparison.Ordinal);
        if (query.EndsWith(".ctor", StringComparison.Ordinal) && !query.EndsWith("#ctor", StringComparison.Ordinal))
        {
            query = query[..^4] + "#ctor";
        }

        var last = query[(query.LastIndexOf('.') + 1)..];
        var matches = graph.ByName(last).Where(n => n.Fact.QName == query || n.Fact.QName.EndsWith("." + query, StringComparison.Ordinal)).ToList();
        if (parameters is not null)
        {
            matches = matches.Where(n => n.IsExternal || ParametersOf(n.Fact.Display).Equals(parameters, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        matches = Prefer(Prefer(matches, n => !n.IsExternal), n => !n.IsTest);
        if (matches.Count == 0)
        {
            return Inherited(graph, query, last) ?? throw NotFound(graph, query, last);
        }

        var families = Families(graph, matches);
        if (families.Count > 1)
        {
            var listed = families.Take(Limits.MaxCandidatesListed).Select(f => $"\n  {f[0].Fact.Display}  [{f[0].Location}]  id: {f[0].Id}");
            throw new ToolRefusal(
                Codes.AmbiguousSymbol,
                $"'{query}' matches {families.Count} unrelated symbols. Qualify the name or pass one of these ids:{string.Concat(listed)}");
        }

        var ordered = matches.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var note = ordered.Count == 1 ? null
            : $"'{query}' covers {ordered.Count} related symbols (overloads or one interface/implementation family): {string.Join("; ", ordered.Select(n => n.Fact.Display))}";
        return new Selection(ordered, null, ordered.Count == 1 ? ordered[0].Fact.Display : query, note);
    }

    private static List<SymbolNode> Prefer(List<SymbolNode> nodes, Func<SymbolNode, bool> better)
    {
        var preferred = nodes.Where(better).ToList();
        return preferred.Count > 0 ? preferred : nodes;
    }

    /// <summary>A member named through a type that only inherits it (<c>OrderRepository.GetByIdAsync</c>).</summary>
    private static Selection? Inherited(CodeGraph graph, string query, string last)
    {
        var dot = query.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var typeQuery = query[..dot];
        var typeName = typeQuery[(typeQuery.LastIndexOf('.') + 1)..];
        var types = graph.ByName(typeName)
            .Where(n => n.Fact.Kind == SymKind.Type && !n.IsExternal && (n.Fact.QName == typeQuery || n.Fact.QName.EndsWith("." + typeQuery, StringComparison.Ordinal)))
            .ToList();
        if (types.Count != 1 || !graph.Types.TryGetValue(types[0].Id, out var type))
        {
            return null;
        }

        var members = type.BaseDefs.SelectMany(graph.MembersOf).Where(m => m.Fact.Name == last).ToList();
        var concrete = Prefer(members, m => (m.Fact.Flags & SymFlags.Abstract) == 0 && !graph.IsInterfaceMember(m.Id));
        if (concrete.Count == 0)
        {
            return null;
        }

        var label = $"{types[0].Fact.Display}.{last}";
        return new Selection(concrete, type, label, $"{label} is inherited: {string.Join("; ", concrete.Select(m => m.Fact.Display))}. Only calls a {types[0].Fact.Display} can receive are counted.");
    }

    private static ToolRefusal NotFound(CodeGraph graph, string query, string last)
    {
        var near = graph.Nodes
            .Where(n => !n.IsExternal && n.Fact.Name.Contains(last, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Fact.Name.Length).ThenBy(n => n.Fact.Display, StringComparer.Ordinal)
            .Take(8).Select(n => n.Fact.Display).Distinct().ToList();
        var hint = near.Count > 0 ? $" Nearest: {string.Join(", ", near)}." : string.Empty;
        return new ToolRefusal(Codes.SymbolNotFound, $"nothing named '{query}' is declared or referenced in the index.{hint} Call find_symbol to search.");
    }

    private static List<List<SymbolNode>> Families(CodeGraph graph, List<SymbolNode> nodes)
    {
        var families = new List<List<SymbolNode>>();
        foreach (var node in nodes)
        {
            var joined = families.Where(f => f.Any(other => Related(graph, node, other))).ToList();
            var merged = new List<SymbolNode> { node };
            foreach (var family in joined)
            {
                merged.AddRange(family);
                families.Remove(family);
            }

            families.Add(merged);
        }

        return families;
    }

    private static bool Related(CodeGraph graph, SymbolNode a, SymbolNode b) =>
        (a.Fact.TypeId == b.Fact.TypeId && a.Fact.Name == b.Fact.Name && a.Fact.TypeId is not null)
        || graph.UpClosure(a.Id).Contains(b.Id) || graph.UpClosure(b.Id).Contains(a.Id);

    private static string ParametersOf(string display)
    {
        var open = display.IndexOf('(', StringComparison.Ordinal);
        return open < 0 ? string.Empty : Compact(display[(open + 1)..].TrimEnd(')'));
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
