using CodeReach.Core.Facts;

namespace CodeReach.Core.Graph;

/// <summary>
/// The one model every tool reads. Immutable after construction; built only from <see cref="IndexFacts"/>.
/// </summary>
public sealed partial class CodeGraph
{
    private static readonly IReadOnlyList<RefRow> NoRows = [];
    private static readonly IReadOnlyList<string> NoIds = [];

    private readonly Dictionary<string, SymbolNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolNode>> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RefRow>> _rowsTo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RefRow>> _rowsFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeNode> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _overrideUp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _overrideDown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(TypeNode Type, string Impl)>> _implsOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _implUp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolNode>> _membersOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EntryPoint>> _entryPoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectFact> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SymbolNode>> _symbolsByFile = new(StringComparer.Ordinal);
    private volatile Dictionary<string, List<SymbolNode>>? _byName;

    public CodeGraph(IndexFacts facts, long generation, DateTimeOffset builtAt)
    {
        Facts = facts;
        Generation = generation;
        BuiltAt = builtAt;
        foreach (var project in facts.Projects)
        {
            _projects[project.Name] = project;
        }

        var rows = new List<RefRow>();
        foreach (var doc in facts.Documents.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var isTest = _projects.TryGetValue(doc.Project, out var owner) && owner.IsTest;
            AddDeclarations(doc, isTest);
            foreach (var reference in doc.References)
            {
                rows.Add(new RefRow(rows.Count, reference, doc.Path, doc.Project, isTest));
            }
        }

        Refs = rows;
        foreach (var row in rows)
        {
            Bucket(_rowsTo, row.Fact.To).Add(row);
            Bucket(_rowsFrom, row.Fact.From).Add(row);
            if (!_nodes.ContainsKey(row.Fact.To))
            {
                _nodes[row.Fact.To] = External(row.Fact.To);
            }
        }

        Endpoints = EndpointResolver.Resolve(facts, _projects, ids => ids.SelectMany(UpOf).ToList());
        AddEntryPoints(facts);
        Completion = BuildCompletion(facts);
    }

    public IndexFacts Facts { get; }

    public long Generation { get; }

    public DateTimeOffset BuiltAt { get; }

    public IReadOnlyList<RefRow> Refs { get; }

    public IReadOnlyList<Endpoint> Endpoints { get; }

    public Completion Completion { get; }

    public IReadOnlyDictionary<string, ProjectFact> Projects => _projects;

    public IReadOnlyDictionary<string, TypeNode> Types => _types;

    /// <summary>Every known symbol: declared ones and the targets of references that have no declaration.</summary>
    public IReadOnlyCollection<SymbolNode> Nodes => _nodes.Values;

    public IReadOnlyDictionary<string, List<EntryPoint>> EntryPoints => _entryPoints;

    public SymbolNode Node(string id) => _nodes.TryGetValue(id, out var node) ? node : External(id);

    public bool IsDeclared(string id) => _declarations.ContainsKey(id);

    public IReadOnlyList<SymbolNode> DeclarationsOf(string id) =>
        _declarations.TryGetValue(id, out var list) ? list : [];

    public IReadOnlyList<SymbolNode> SymbolsInFile(string path) =>
        _symbolsByFile.TryGetValue(path, out var list) ? list : [];

    public IEnumerable<string> Files => _symbolsByFile.Keys;

    public IReadOnlyList<SymbolNode> MembersOf(string typeId) =>
        _membersOf.TryGetValue(typeId, out var list) ? list : [];

    public IReadOnlyList<RefRow> RowsTo(string id) => _rowsTo.TryGetValue(id, out var list) ? list : NoRows;

    public IReadOnlyList<RefRow> RowsFrom(string id) => _rowsFrom.TryGetValue(id, out var list) ? list : NoRows;

    public bool IsEntryPoint(string id) => _entryPoints.ContainsKey(id);

    /// <summary>Symbols by simple name, declared and undeclared alike.</summary>
    public IReadOnlyList<SymbolNode> ByName(string name)
    {
        if (_byName is null)
        {
            var map = new Dictionary<string, List<SymbolNode>>(StringComparer.Ordinal);
            foreach (var node in _nodes.Values)
            {
                Bucket(map, node.Fact.Name).Add(node);
            }

            _byName = map;
        }

        return _byName.TryGetValue(name, out var list) ? list : [];
    }

    /// <summary>Interface members a definition implements and the member it overrides.</summary>
    public IEnumerable<string> UpOf(string id)
    {
        if (_implUp.TryGetValue(id, out var interfaces))
        {
            foreach (var member in interfaces)
            {
                yield return member;
            }
        }

        if (_overrideUp.TryGetValue(id, out var overridden))
        {
            yield return overridden;
        }
    }

    public IReadOnlyList<string> OverridersOf(string id) =>
        _overrideDown.TryGetValue(id, out var list) ? list : NoIds;

    public IReadOnlyList<(TypeNode Type, string Impl)> ImplementationsOf(string interfaceMember) =>
        _implsOf.TryGetValue(interfaceMember, out var list) ? list : [];

    public bool IsInterfaceMember(string id) => _implsOf.ContainsKey(id)
        || (Ids.ContainingTypeOf(id) is { } type && _types.TryGetValue(type, out var node) && node.Fact.IsInterface);

    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        return list;
    }

    private static SymbolNode External(string id) => new(
        new SymbolFact(id, Ids.KindOf(id), Ids.NameOf(id), Ids.QNameOf(id), Ids.DisplayOf(id), Ids.ContainingTypeOf(id), 0, 0, 0, SymFlags.External),
        string.Empty,
        string.Empty,
        false);

    private void AddDeclarations(DocumentFacts doc, bool isTest)
    {
        foreach (var symbol in doc.Symbols)
        {
            var node = new SymbolNode(symbol, doc.Path, doc.Project, isTest);
            Bucket(_declarations, symbol.Id).Add(node);
            Bucket(_symbolsByFile, doc.Path).Add(node);
            if (_nodes.TryAdd(symbol.Id, node) && symbol.TypeId is not null)
            {
                Bucket(_membersOf, symbol.TypeId).Add(node);
            }
        }

        foreach (var type in doc.Types)
        {
            if (_types.TryAdd(type.Id, new TypeNode(type, doc.Project, isTest)))
            {
                foreach (var impl in type.Impls)
                {
                    Bucket(_implsOf, impl.InterfaceMember).Add((_types[type.Id], impl.Impl));
                    var up = Bucket(_implUp, impl.Impl);
                    if (!up.Contains(impl.InterfaceMember))
                    {
                        up.Add(impl.InterfaceMember);
                    }
                }
            }
        }

        foreach (var item in doc.Overrides)
        {
            if (_overrideUp.TryAdd(item.Member, item.Overridden))
            {
                Bucket(_overrideDown, item.Overridden).Add(item.Member);
            }
        }
    }

    private void AddEntryPoints(IndexFacts facts)
    {
        foreach (var endpoint in Endpoints)
        {
            Bucket(_entryPoints, endpoint.Handler).Add(
                new EntryPoint(endpoint.Handler, "endpoint", $"{endpoint.Verb} {endpoint.Template}"));
        }

        foreach (var entry in facts.Documents.SelectMany(d => d.EntryPoints))
        {
            var list = Bucket(_entryPoints, entry.Symbol);
            if (!list.Any(e => e.Kind == entry.Kind && e.Label == entry.Label))
            {
                list.Add(new EntryPoint(entry.Symbol, entry.Kind, entry.Label));
            }
        }
    }

    private static Completion BuildCompletion(IndexFacts facts)
    {
        var problems = new List<string>();
        foreach (var project in facts.Projects.Where(p => p.LoadFailure is not null))
        {
            problems.Add($"project {project.Name} did not load: {project.LoadFailure}");
        }

        var broken = facts.Documents.Where(d => d.Errors > 0).OrderByDescending(d => d.Errors).ToList();
        if (broken.Count > 0)
        {
            var sample = string.Join(", ", broken.Take(3).Select(d => $"{d.Path} ({d.Errors})"));
            problems.Add($"{broken.Count} documents have binding errors ({broken.Sum(d => d.Errors)} total): {sample}");
        }

        if (facts.LoadWarnings.Length > 0)
        {
            problems.Add($"{facts.LoadWarnings.Length} workspace warnings, first: {facts.LoadWarnings[0]}");
        }

        return new Completion(problems);
    }
}
