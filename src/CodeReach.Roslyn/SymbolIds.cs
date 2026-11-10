using System.Collections.Concurrent;
using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

/// <summary>Turns Roslyn symbols into the string identities the facts model uses.</summary>
internal sealed class SymbolIds
{
    public static readonly SymbolDisplayFormat QualifiedName = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeExplicitInterface);

    public static readonly SymbolDisplayFormat Short = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly ConcurrentDictionary<ISymbol, string?> _ids = new(SymbolEqualityComparer.Default);
    private readonly Func<string, string> _relativePath;

    public SymbolIds(Func<string, string> relativePath) => _relativePath = relativePath;

    /// <summary>The member a reference is attributed to: accessors become their property, reduced and constructed forms their definition.</summary>
    public static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            if (method.AssociatedSymbol is { } associated && method.MethodKind is MethodKind.PropertyGet
                or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
            {
                return associated.OriginalDefinition;
            }

            return method.OriginalDefinition;
        }

        return symbol.OriginalDefinition;
    }

    /// <summary>The id of a symbol, or null for anything the model does not track.</summary>
    public string? IdOf(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        symbol = Normalize(symbol);
        return Tracked(symbol) ? _ids.GetOrAdd(symbol, Build) : null;
    }

    /// <summary>The key of a possibly constructed type, or null when it cannot take part in dispatch.</summary>
    public string? TypeKey(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var definition = IdOf(named);
        if (definition is null || !named.IsGenericType)
        {
            return definition;
        }

        var arguments = named.TypeArguments.Select(a => a switch
        {
            ITypeParameterSymbol parameter => "!" + parameter.Name,
            INamedTypeSymbol argument => TypeKey(argument) ?? "?",
            _ => "?" + a.ToDisplayString(Short),
        });
        var variant = named.OriginalDefinition.TypeParameters.Any(p => p.Variance != VarianceKind.None);
        return $"{definition}<{string.Join(',', arguments)}>{(variant ? Ids.VariantMark : string.Empty)}";
    }

    private static bool Tracked(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => !type.IsAnonymousType && type.TypeKind != TypeKind.Error && !type.IsTupleType,
        IMethodSymbol method => method.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction
            or MethodKind.BuiltinOperator or MethodKind.FunctionPointerSignature or MethodKind.DelegateInvoke),
        IPropertySymbol property => !property.ContainingType.IsAnonymousType,
        IFieldSymbol field => !field.ContainingType.IsTupleType,
        IEventSymbol => true,
        _ => false,
    };

    private string? Build(ISymbol symbol)
    {
        var id = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(id) || id.Length < 3 || id[1] != ':')
        {
            return null;
        }

        var source = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (source is null || symbol.ContainingAssembly is null)
        {
            return id;
        }

        var tag = symbol.ContainingAssembly.Name;
        if (FileLocalOwner(symbol) is { } fileLocal)
        {
            var declared = fileLocal.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
            tag += "|" + (declared is null ? "file" : _relativePath(declared));
        }

        return $"{id[..2]}[{tag}]{id[2..]}";
    }

    private static INamedTypeSymbol? FileLocalOwner(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsFileLocal)
            {
                return type;
            }
        }

        return null;
    }
}
