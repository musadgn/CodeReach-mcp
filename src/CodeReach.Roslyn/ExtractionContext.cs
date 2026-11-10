using System.Collections.Concurrent;
using CodeReach.Core;
using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

/// <summary>Per-project state shared by every document extraction of that project.</summary>
internal sealed class ExtractionContext
{
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _controllers = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _hosted = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, int[]> _builderParameters = new(SymbolEqualityComparer.Default);

    public ExtractionContext(Compilation compilation, CodeReachConfig config, SymbolIds ids, string root)
    {
        Compilation = compilation;
        Config = config;
        Ids = ids;
        Root = root;
        EntryPoint = compilation.GetEntryPoint(CancellationToken.None);
        Templates = new RegistrationTemplates(compilation);
    }

    public RegistrationTemplates Templates { get; }

    public Compilation Compilation { get; }

    public CodeReachConfig Config { get; }

    public SymbolIds Ids { get; }

    public string Root { get; }

    public IMethodSymbol? EntryPoint { get; }

    public bool IsController(INamedTypeSymbol type) => _controllers.GetOrAdd(type, ControllerReader.IsController);

    public bool IsHostedService(INamedTypeSymbol type) =>
        _hosted.GetOrAdd(type, t => t.TypeKind == TypeKind.Class && Names.Implements(t, Names.HostedService));

    /// <summary>Ordinals of the parameters of a source method that receive a route builder.</summary>
    public int[] BuilderParameters(IMethodSymbol method) => _builderParameters.GetOrAdd(method, m =>
        m.Locations.Any(l => l.IsInSource)
            ? m.Parameters.Where(p => Names.Implements(p.Type, Names.EndpointRouteBuilder)).Select(p => p.Ordinal).ToArray()
            : []);
}
