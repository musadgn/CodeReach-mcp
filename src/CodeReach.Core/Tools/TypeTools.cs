using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>type_hierarchy and di_registrations.</summary>
internal static class TypeTools
{
    private const string ScanCaveat =
        "Not seen: registrations made by assembly scanning (Scrutor, MediatR and similar), inside a library's own Add* method, or outside this solution.";

    public static async Task<ToolResult> TypeHierarchy(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var (type, note) = ResolveType(graph, args.Required("symbol"));
        var includeTests = args.Flag("include_tests");
        var limit = args.Number("limit");
        var node = graph.Types.GetValueOrDefault(type.Id);
        var report = new Report().Line($"type hierarchy of {type.Fact.Display} ({KindOf(node)})  {type.Location}");
        if (note is not null)
        {
            report.Line("note: " + note);
        }

        var bases = node?.Fact.Bases.Where(b => Ids.DefOf(b) != "T:System.Object").Select(Ids.DisplayOfKey).ToList() ?? [];
        report.Line(node is null
            ? "Bases: unknown (the type has no source in the index)."
            : $"Inherits and implements, nearest base class first: {(bases.Count == 0 ? "nothing" : string.Join(", ", bases))}");

        var subtypes = graph.Types.Values.Where(t => t.BaseDefs.Contains(type.Id)).ToList();
        var shown = subtypes.Where(t => includeTests || !t.IsTest)
            .OrderBy(t => graph.Node(t.Id).Fact.Display, StringComparer.Ordinal).ToList();
        report.Line().Line($"Derived and implementing types: {shown.Count}{Hidden(subtypes.Count - shown.Count, "types")}");
        foreach (var sub in shown.Take(limit))
        {
            var direct = !subtypes.Any(other => other.Id != sub.Id && sub.BaseDefs.Contains(other.Id));
            report.Line($"  {KindOf(sub),-15} {graph.Node(sub.Id).Fact.Display}  {graph.Node(sub.Id).Location}{(direct ? string.Empty : "  (indirect)")}");
        }

        ReachTools.Truncation(report, shown.Count, limit);
        var members = graph.MembersOf(type.Id).Where(m => m.Fact.Kind != SymKind.Type)
            .OrderBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Fact.Line).ToList();
        report.Line().Line($"Members declared here: {members.Count}");
        foreach (var member in members.Take(limit))
        {
            report.Line($"  {Render.Kind(member.Fact.Kind),-11} {member.Fact.Display}  :{member.Fact.Line}{Relation(graph, member)}");
        }

        ReachTools.Truncation(report, members.Count, limit);
        var registrations = graph.Registrations.Where(r => Ids.DefOf(r.Fact.Service) == type.Id || (r.Fact.Implementation is { } i && Ids.DefOf(i) == type.Id)).ToList();
        if (registrations.Count > 0)
        {
            report.Line().Line("Registered in dependency injection:");
            foreach (var registration in registrations)
            {
                report.Line("  " + Describe(graph, registration));
            }
        }

        return report.Finish(lease);
    }

    public static async Task<ToolResult> Registrations(IIndexHost host, Args args, CancellationToken cancel)
    {
        var lease = await host.AcquireAsync(cancel).ConfigureAwait(false);
        var graph = lease.Graph;
        var model = new ContainerModel(graph, args.Flag("include_tests"));
        var symbol = args.Text("symbol");
        return symbol is null
            ? Overview(model, args, lease)
            : ForType(model, ResolveType(graph, symbol), args, lease);
    }

    private static ToolResult Overview(ContainerModel model, Args args, GraphLease lease)
    {
        var graph = model.Graph;
        var lifetime = args.Text("lifetime");
        var limit = args.Number("limit");
        var all = model.Registrations;
        var report = new Report().Line(
            $"{all.Count} registrations ({string.Join(", ", all.GroupBy(r => r.Fact.Lifetime).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"))}) "
            + $"in {all.Select(r => r.File).Distinct().Count()} files; {model.Dependencies.Count} constructor parameters in {model.Dependencies.Select(d => d.Consumer).Distinct().Count()} types.");

        var captive = model.CaptiveDependencies().ToList();
        var missing = model.Unregistered().ToList();
        var duplicates = all.Where(r => !r.IsConditional && !r.IsKeyed).GroupBy(r => r.Fact.Service).Where(g => g.Count() > 1).ToList();

        report.Line().Line($"Captive dependencies (a singleton holding a scoped service for the app's lifetime): {captive.Count}");
        foreach (var (registration, dependency, scoped) in captive.Take(limit))
        {
            report.Line($"  {TypeName(graph, registration.Fact.Implementation!)} ({Lifetime(registration)}, {registration.Location}) takes {TypeName(graph, dependency.Fact.Type)} (scoped, {scoped.Location})")
                .Line($"          in {graph.Node(dependency.Fact.Constructor).Fact.Display}  {graph.Node(dependency.Fact.Constructor).Location}");
        }

        if (captive.Count > 0)
        {
            report.Line("  Fix: inject IServiceScopeFactory and resolve the scoped service inside a scope per unit of work, or change a lifetime.");
        }

        report.Line().Line($"Injected but no registration found (interfaces and abstract types asked for by container-built types): {missing.Count}");
        foreach (var group in missing.Take(limit))
        {
            var consumers = string.Join(", ", group.Select(d => $"{Render.Symbol(graph, d.Consumer)} ({graph.Node(d.Fact.Constructor).Location})").Distinct().Take(5));
            report.Line($"  {TypeName(graph, group.Key)}  {graph.Node(Ids.DefOf(group.Key)).Location}")
                .Line($"          asked for by {consumers}{(group.Count() > 5 ? ", ..." : string.Empty)}");
        }

        if (missing.Count > 0)
        {
            report.Line("  " + ScanCaveat);
        }

        report.Line().Line($"Registered more than once (one injection gets the last; IEnumerable<T> gets all): {duplicates.Count}");
        foreach (var group in duplicates.Take(limit))
        {
            report.Line($"  {TypeName(graph, group.Key)}: {string.Join("; ", group.Select(r => $"{Implementation(graph, r)} {r.Location}"))}");
        }

        var listed = all.Where(r => lifetime is null || r.Fact.Lifetime == lifetime).ToList();
        report.Line().Line($"Registrations{(lifetime is null ? string.Empty : $" ({lifetime})")}: {listed.Count}");
        foreach (var registration in listed.Take(limit))
        {
            report.Line("  " + Describe(graph, registration));
        }

        ReachTools.Truncation(report, listed.Count, limit);
        return report.Finish(lease, finding: captive.Count > 0 || missing.Count > 0);
    }

    private static ToolResult ForType(ContainerModel model, (SymbolNode Type, string? Note) resolved, Args args, GraphLease lease)
    {
        var graph = model.Graph;
        var (type, note) = resolved;
        var limit = args.Number("limit");
        var report = new Report().Line($"dependency injection for {type.Fact.Display}  {type.Location}");
        if (note is not null)
        {
            report.Line("note: " + note);
        }

        var asService = model.Registrations.Where(r => Ids.DefOf(r.Fact.Service) == type.Id).ToList();
        var asImplementation = model.Registrations.Where(r => r.Fact.Implementation is { } i && Ids.DefOf(i) == type.Id && Ids.DefOf(r.Fact.Service) != type.Id).ToList();
        report.Line().Line($"Registered as a service: {asService.Count}");
        foreach (var registration in asService)
        {
            report.Line("  " + Describe(graph, registration));
        }

        if (asImplementation.Count > 0)
        {
            report.Line($"Registered as the implementation of: {asImplementation.Count}");
            foreach (var registration in asImplementation)
            {
                report.Line("  " + Describe(graph, registration));
            }
        }

        var node = graph.Types.GetValueOrDefault(type.Id);
        if (node is not null && !node.IsConcrete)
        {
            var implementations = graph.Types.Values.Where(t => t.IsConcrete && t.BaseDefs.Contains(type.Id) && (model.IncludeTests || !t.IsTest)).ToList();
            report.Line($"Implementations in source: {implementations.Count}");
            foreach (var implementation in implementations.Take(limit))
            {
                var used = model.Registrations.Any(r => Ids.DefOf(r.Fact.Service) == type.Id && r.Fact.Implementation is { } i && Ids.DefOf(i) == implementation.Id);
                report.Line($"  {graph.Node(implementation.Id).Fact.Display}  {graph.Node(implementation.Id).Location}{(used ? "  (registered for it)" : string.Empty)}");
            }
        }

        var consumers = model.Dependencies.Where(d => Ids.DefOf(d.Fact.Type) == type.Id).ToList();
        report.Line().Line($"Asked for by {consumers.Select(d => d.Consumer).Distinct().Count()} types (constructor parameters):");
        foreach (var dependency in consumers.Take(limit))
        {
            var built = model.IsContainerBuilt(dependency.Consumer) ? string.Empty : "  (this type is not known to be built by the container)";
            report.Line($"  {graph.Node(dependency.Fact.Constructor).Fact.Display}{(dependency.Fact.Many ? " as IEnumerable" : string.Empty)}  {graph.Node(dependency.Fact.Constructor).Location}{built}");
        }

        ReachTools.Truncation(report, consumers.Count, limit);
        var own = model.Dependencies.Where(d => d.Consumer == type.Id).GroupBy(d => d.Fact.Constructor).ToList();
        if (own.Count > 0)
        {
            report.Line().Line("Its constructor asks for:");
            foreach (var constructor in own)
            {
                report.Line($"  {graph.Node(constructor.Key).Fact.Display}  {graph.Node(constructor.Key).Location}");
                foreach (var dependency in constructor.OrderBy(d => d.Fact.Ordinal))
                {
                    report.Line($"    {TypeName(graph, dependency.Fact.Type)}{(dependency.Fact.Many ? " (all registrations)" : string.Empty)}: {model.Availability(dependency)}");
                }
            }
        }

        var captive = model.CaptiveDependencies().Where(c => c.Registration.Fact.Implementation is { } i && Ids.DefOf(i) == type.Id).ToList();
        foreach (var (registration, dependency, scoped) in captive)
        {
            report.Line().Line($"CAPTIVE DEPENDENCY: {type.Fact.Display} is {Lifetime(registration)} ({registration.Location}) but takes {TypeName(graph, dependency.Fact.Type)}, registered scoped at {scoped.Location}. Resolve it from a scope instead.");
        }

        if (asService.Count == 0 && asImplementation.Count == 0)
        {
            report.Line().Line(ScanCaveat);
        }

        return report.Finish(lease, finding: captive.Count > 0);
    }

    private static (SymbolNode Type, string? Note) ResolveType(CodeGraph graph, string text)
    {
        // These tools take a type, so a name that matches exactly one declared type wins over members of the same name.
        var name = text.Trim();
        var types = graph.Types.Keys.Select(graph.Node)
            .Where(n => n.Fact.QName == name || n.Fact.QName.EndsWith("." + name, StringComparison.Ordinal))
            .ToList();
        if (types.Count == 1)
        {
            return (types[0], null);
        }

        var selection = SymbolResolver.Resolve(graph, text);
        var symbol = selection.Symbols[0];
        if (symbol.Fact.Kind == SymKind.Type)
        {
            return (symbol, selection.Note);
        }

        var typeId = symbol.Fact.TypeId ?? Ids.ContainingTypeOf(symbol.Id)
            ?? throw new ToolRefusal(Codes.ValidationFailed, $"'{text}' is not a type and has no containing type. Name a class or an interface.");
        var type = graph.Node(typeId);
        return (type, $"{symbol.Fact.Display} is a member; showing its type {type.Fact.Display}.");
    }

    /// <summary>A service or dependency key: a type id, or a constructed key such as <c>T:Ns.Reader`1&lt;T:Ns.Event&gt;</c>.</summary>
    private static string TypeName(CodeGraph graph, string key) =>
        key.Contains('<', StringComparison.Ordinal) ? Ids.DisplayOfKey(key) : Render.Symbol(graph, key);

    private static string KindOf(TypeNode? node) => node switch
    {
        null => "type",
        { Fact.IsInterface: true } => "interface",
        { Fact.IsAbstract: true } => "abstract class",
        _ => "class",
    };

    private static string Hidden(int count, string what) =>
        count == 0 ? string.Empty : $" (hidden: {count} {what} in test projects; include_tests=true shows them)";

    private static string Relation(CodeGraph graph, SymbolNode member)
    {
        var up = graph.UpOf(member.Id).ToList();
        var parts = up.Select(id => (graph.IsInterfaceMember(id) ? "implements " : "overrides ") + Render.Symbol(graph, id)).ToList();
        var below = graph.ImplementationsOf(member.Id).Select(i => i.Impl).Concat(graph.OverridersOf(member.Id)).Distinct().ToList();
        if (below.Count > 0)
        {
            var interfaceMember = graph.IsInterfaceMember(member.Id);
            parts.Add(below.Count == 1
                ? (interfaceMember ? "implemented by " : "overridden by ") + Render.Symbol(graph, below[0])
                : $"{below.Count} {(interfaceMember ? "implementations" : "overrides")}");
        }

        return parts.Count == 0 ? string.Empty : "  " + string.Join("; ", parts);
    }

    private static string Lifetime(Registration registration) => registration.Fact.Method switch
    {
        "AddHostedService" => "singleton, hosted service",
        "AddHttpClient" => "transient, typed HttpClient",
        "AddDbContext" or "AddDbContextPool" => "scoped, DbContext",
        _ => registration.Fact.Lifetime,
    };

    private static string Implementation(CodeGraph graph, Registration registration) =>
        registration.Fact.Implementation is { } implementation ? TypeName(graph, implementation) : "(factory)";

    private static string Describe(CodeGraph graph, Registration registration)
    {
        var service = TypeName(graph, registration.Fact.Service);
        var implementation = Implementation(graph, registration);
        var mapping = implementation == service ? service : $"{service} -> {implementation}";
        var detail = registration.Fact.Detail.Length > 0 ? $" [{registration.Fact.Detail}]" : string.Empty;
        return $"{Lifetime(registration),-28} {mapping}{detail}  {registration.Location}";
    }

    /// <summary>What the container knows, computed once per call from the graph's registration and constructor facts.</summary>
    private sealed class ContainerModel
    {
        private readonly Dictionary<string, List<Registration>> _byService;
        private readonly Dictionary<string, List<Registration>> _openByDefinition;
        private readonly HashSet<string> _built;

        public ContainerModel(CodeGraph graph, bool includeTests)
        {
            Graph = graph;
            IncludeTests = includeTests;
            Registrations = graph.Registrations.Where(r => includeTests || !r.IsTest).ToList();
            Dependencies = graph.Dependencies.Where(d => includeTests || !d.IsTest).ToList();
            _byService = Registrations.Where(r => !r.IsKeyed).GroupBy(r => r.Fact.Service).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _openByDefinition = Registrations.Where(r => !r.IsKeyed && Ids.IsOpen(r.Fact.Service))
                .GroupBy(r => Ids.DefOf(r.Fact.Service)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // Types the container constructs: registered implementations, controllers, and entry-point types such as hosted services.
            _built = new HashSet<string>(Registrations.Select(r => r.Fact.Implementation).OfType<string>().Select(Ids.DefOf), StringComparer.Ordinal);
            foreach (var symbol in graph.EntryPoints.Keys)
            {
                if (graph.Node(symbol).Fact.TypeId is { } owner && graph.EntryPoints[symbol].Any(e => e.Kind != "main"))
                {
                    _built.Add(owner);
                }
            }
        }

        public CodeGraph Graph { get; }

        public bool IncludeTests { get; }

        public List<Registration> Registrations { get; }

        public List<Dependency> Dependencies { get; }

        public bool IsContainerBuilt(string typeId) => _built.Contains(typeId);

        /// <summary>The plain registrations that can satisfy a request for a service key, open generics included.</summary>
        public List<Registration>? For(string key)
        {
            if (_byService.TryGetValue(key, out var exact))
            {
                return exact;
            }

            return _openByDefinition.TryGetValue(Ids.DefOf(key), out var open) ? open : null;
        }

        public string Availability(Dependency dependency)
        {
            if (For(dependency.Fact.Type) is { } registrations)
            {
                return string.Join(", ", registrations.Select(r => $"{Lifetime(r)} at {r.Location}"));
            }

            if (dependency.Fact.Many)
            {
                return "no registration found (the IEnumerable will be empty)";
            }

            var definition = Ids.DefOf(dependency.Fact.Type);
            return Graph.Types.TryGetValue(definition, out var type) && !type.IsConcrete
                ? "NO REGISTRATION FOUND"
                : Graph.Types.ContainsKey(definition) ? "no registration found" : "framework or library type";
        }

        /// <summary>Singletons whose constructor takes a service that is registered only as scoped.</summary>
        public IEnumerable<(Registration Registration, Dependency Dependency, Registration Scoped)> CaptiveDependencies()
        {
            foreach (var registration in Registrations.Where(r => r.Fact.Lifetime == "singleton" && r.Fact.Implementation is not null))
            {
                foreach (var dependency in Dependencies.Where(d => d.Consumer == Ids.DefOf(registration.Fact.Implementation!)))
                {
                    if (For(dependency.Fact.Type) is { } targets && targets.All(t => t.Fact.Lifetime == "scoped"))
                    {
                        yield return (registration, dependency, targets[^1]);
                    }
                }
            }
        }

        /// <summary>Interfaces and abstract source types that container-built types ask for and nothing registers.</summary>
        public IEnumerable<IGrouping<string, Dependency>> Unregistered() => Dependencies
            .Where(d => !d.Fact.Many && _built.Contains(d.Consumer) && For(d.Fact.Type) is null)
            .Where(d => Graph.Types.TryGetValue(Ids.DefOf(d.Fact.Type), out var type) && !type.IsConcrete && !type.IsTest)
            .GroupBy(d => d.Fact.Type)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
    }
}
