using CodeReach.Core;
using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

/// <summary>Decides which methods are entry points besides HTTP endpoints.</summary>
internal static class RootRules
{
    public static IEnumerable<EntryPointFact> Match(IMethodSymbol method, string id, ExtractionContext context)
    {
        var type = method.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(method, context.EntryPoint))
        {
            yield return new EntryPointFact(id, "main", type.ContainingAssembly.Name);
        }

        if (context.IsHostedService(type) && method.Name is "ExecuteAsync" or "StartAsync" or "StopAsync"
            && (method.IsOverride || ImplementsMemberOf(method, i => Names.Is(i, Names.HostedService))))
        {
            yield return new EntryPointFact(id, "hosted-service", type.Name);
        }

        foreach (var rule in context.Config.Roots)
        {
            if (Applies(rule, method))
            {
                yield return new EntryPointFact(id, rule.Kind, type.Name);
            }
        }
    }

    private static bool Applies(RootRule rule, IMethodSymbol method)
    {
        var nameFits = rule.Method is null or "*" || rule.Method == method.Name;
        if (rule.Attribute is { } attribute)
        {
            return method.GetAttributes().Any(a => Names.SelfAndBases(a.AttributeClass).Any(t => Names.Matches(t, attribute)));
        }

        if (rule.Implements is { } contract)
        {
            return nameFits && ImplementsMemberOf(method, i => SameType(i, contract));
        }

        if (rule.BaseType is { } baseType)
        {
            return nameFits && method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic
                && Names.SelfAndBases(method.ContainingType.BaseType).Any(t => SameType(t, baseType));
        }

        return false;
    }

    private static bool SameType(INamedTypeSymbol type, string wanted)
    {
        var full = Names.FullName(type);
        return full == wanted || (!wanted.Contains('`', StringComparison.Ordinal) && Names.StripArity(full) == wanted);
    }

    private static bool ImplementsMemberOf(IMethodSymbol method, Func<INamedTypeSymbol, bool> contract)
    {
        var type = method.ContainingType;
        return type.AllInterfaces.Where(contract).SelectMany(i => i.GetMembers())
            .Any(member => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method));
    }
}
