using CodeReach.Core.Facts;

namespace CodeReach.Core.Graph;

public sealed record SymbolNode(SymbolFact Fact, string File, string Project, bool IsTest)
{
    public string Id => Fact.Id;

    /// <summary>True when the index holds no declaration: a metadata symbol or an implicit member.</summary>
    public bool IsExternal => (Fact.Flags & SymFlags.External) != 0;

    public string Location => IsExternal ? "(no source)" : $"{File}:{Fact.Line}";
}

public readonly record struct RefRow(int Index, ReferenceFact Fact, string File, string Project, bool IsTest)
{
    public bool IsCall => Fact.Kind is RefKind.Invoke or RefKind.Construct or RefKind.MethodGroup
        or RefKind.BaseCall or RefKind.Read or RefKind.Write;
}

public sealed class TypeNode
{
    public TypeNode(TypeFact fact, string project, bool isTest)
    {
        Fact = fact;
        Project = project;
        IsTest = isTest;
        BaseKeys = new HashSet<string>(fact.Bases, StringComparer.Ordinal);
        BaseDefs = new HashSet<string>(fact.Bases.Select(Ids.DefOf), StringComparer.Ordinal);
    }

    public TypeFact Fact { get; }

    public string Project { get; }

    public bool IsTest { get; }

    public HashSet<string> BaseKeys { get; }

    public HashSet<string> BaseDefs { get; }

    public string Id => Fact.Id;

    public bool IsConcrete => !Fact.IsInterface && !Fact.IsAbstract;
}

public sealed record Endpoint(
    string Handler, string Verb, string Template, bool Minimal, string File, int Line, string Project, bool IsTest,
    IReadOnlyList<AuthFact> Auth)
{
    /// <summary>The framework lets <c>[AllowAnonymous]</c> win over every authorize attribute, wherever it sits.</summary>
    public bool AllowsAnonymous => Auth.Any(a => a.Kind == AuthKind.AllowAnonymous);

    public bool HasDeclaredAuth => !AllowsAnonymous && Auth.Count > 0;
}

public sealed record EntryPoint(string Symbol, string Kind, string Label);

/// <summary>A call of a definition: the row, and the member the call was bound to when that differs.</summary>
public readonly record struct CallEdge(RefRow Row, string Target, string? Via);

public sealed record Completion(IReadOnlyList<string> Problems)
{
    public bool IsComplete => Problems.Count == 0;
}

public sealed record Registration(ServiceFact Fact, string File, string Project, bool IsTest)
{
    public string Location => $"{File}:{Fact.Line}";

    /// <summary>A TryAdd call registers nothing when the service already has a registration.</summary>
    public bool IsConditional => Fact.Method.StartsWith("Try", StringComparison.Ordinal);

    /// <summary>A keyed service is resolved only by its key, so it neither satisfies nor overrides a plain injection.</summary>
    public bool IsKeyed => Fact.Method.Contains("Keyed", StringComparison.Ordinal);
}

public sealed record Dependency(DependencyFact Fact, string File, string Project, bool IsTest)
{
    /// <summary>The type whose constructor asks for <see cref="DependencyFact.Type"/>.</summary>
    public string Consumer => Ids.ContainingTypeOf(Fact.Constructor) ?? Fact.Constructor;
}
