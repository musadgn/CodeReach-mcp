using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

internal static class TypeFacts
{
    public static TypeFact Build(INamedTypeSymbol type, string id, SymbolIds ids)
    {
        var bases = new List<string>();
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            AddKey(current);
        }

        foreach (var contract in type.AllInterfaces)
        {
            AddKey(contract);
        }

        var impls = new List<ImplFact>();
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers()))
            {
                if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary } || member.IsStatic && !member.IsAbstract)
                {
                    continue;
                }

                if (ids.IdOf(member) is { } contractId && ids.IdOf(type.FindImplementationForInterfaceMember(member)) is { } implId
                    && !impls.Exists(i => i.InterfaceMember == contractId && i.Impl == implId))
                {
                    impls.Add(new ImplFact(contractId, implId));
                }
            }
        }

        return new TypeFact(
            id, type.TypeKind == TypeKind.Interface, type.IsAbstract || type.IsStatic, type.IsGenericType,
            [.. bases], [.. impls.OrderBy(i => i.InterfaceMember, StringComparer.Ordinal).ThenBy(i => i.Impl, StringComparer.Ordinal)]);

        void AddKey(INamedTypeSymbol target)
        {
            if (ids.TypeKey(target) is { } key && !bases.Contains(key))
            {
                bases.Add(key);
            }
        }
    }
}
