using System.Collections.Immutable;
using CodeReach.Core;
using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>Reads authorization from attribute data. Values come from the compiler, text from the source.</summary>
internal static class AuthReader
{
    public static IEnumerable<AuthFact> Read(ImmutableArray<AttributeData> attributes, string scope, CodeReachConfig config)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass is not { } type || KindOf(type, config) is not { } kind)
            {
                continue;
            }

            var name = type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name[..^9] : type.Name;
            yield return new AuthFact(kind, name, Arguments(attribute), scope);
        }
    }

    public static string Arguments(AttributeData attribute)
    {
        var values = attribute.ConstructorArguments;
        if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax { ArgumentList: { } list })
        {
            var positional = values.Select(v => v.ToCSharpString());
            var named = attribute.NamedArguments.Select(n => $"{n.Key} = {n.Value.ToCSharpString()}");
            return string.Join(", ", positional.Concat(named));
        }

        var parts = new List<string>();
        var position = 0;
        foreach (var argument in list.Arguments)
        {
            var text = string.Join(' ', argument.Expression.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (argument.NameEquals is { } name)
            {
                var value = attribute.NamedArguments.FirstOrDefault(n => n.Key == name.Name.Identifier.ValueText).Value;
                parts.Add($"{name.Name.Identifier.ValueText} = {WithValue(text, value)}");
                continue;
            }

            var hasValue = argument.NameColon is null && position < values.Length && values[position].Kind != TypedConstantKind.Array;
            parts.Add(hasValue ? WithValue(text, values[position]) : text);
            position++;
        }

        return string.Join(", ", parts);
    }

    private static string WithValue(string text, TypedConstant value)
    {
        if (value.IsNull || value.Kind is not (TypedConstantKind.Primitive or TypedConstantKind.Enum))
        {
            return text;
        }

        var evaluated = value.ToCSharpString();
        return evaluated == text ? text : $"{text} = {evaluated}";
    }

    private static AuthKind? KindOf(INamedTypeSymbol type, CodeReachConfig config)
    {
        if (Names.Implements(type, Names.AllowAnonymous))
        {
            return AuthKind.AllowAnonymous;
        }

        if (Names.Implements(type, Names.AuthorizeData))
        {
            return AuthKind.Authorize;
        }

        var custom = config.AuthAttributes.Any(wanted => Names.SelfAndBases(type).Any(t => Names.Matches(t, wanted)))
            || Names.Implements(type, Names.AuthorizationFilter) || Names.Implements(type, Names.AsyncAuthorizationFilter)
            || Names.Implements(type, Names.RequirementData);
        return custom ? AuthKind.Custom : null;
    }
}
