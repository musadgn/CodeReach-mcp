using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

/// <summary>Applies the framework's own controller, action and attribute-routing rules to symbols.</summary>
internal static class ControllerReader
{
    private static readonly Dictionary<string, string> VerbAttributes = new(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Mvc.HttpGetAttribute"] = "GET",
        ["Microsoft.AspNetCore.Mvc.HttpPostAttribute"] = "POST",
        ["Microsoft.AspNetCore.Mvc.HttpPutAttribute"] = "PUT",
        ["Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"] = "DELETE",
        ["Microsoft.AspNetCore.Mvc.HttpPatchAttribute"] = "PATCH",
        ["Microsoft.AspNetCore.Mvc.HttpHeadAttribute"] = "HEAD",
        ["Microsoft.AspNetCore.Mvc.HttpOptionsAttribute"] = "OPTIONS",
    };

    public static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType
            || type.DeclaredAccessibility != Accessibility.Public || HasAttribute(type, Names.NonController))
        {
            return false;
        }

        return type.Name.EndsWith("Controller", StringComparison.Ordinal) || HasAttribute(type, Names.Controller);
    }

    public static bool IsAction(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsAbstract || method.IsGenericMethod
            || method.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        var root = method;
        while (root.OverriddenMethod is { } overridden)
        {
            root = overridden;
        }

        var isObjectMember = root.ContainingType.SpecialType == SpecialType.System_Object;
        var isDispose = method is { Name: "Dispose", Parameters.Length: 0 };
        return !isObjectMember && !isDispose && !Chain(method).Any(m => m.GetAttributes().Any(a => Derives(a, Names.NonAction)));
    }

    /// <summary>Actions a concrete controller inherits from base classes without overriding them.</summary>
    public static IEnumerable<IMethodSymbol> InheritedActions(INamedTypeSymbol controller)
    {
        var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var type = controller; type is not null; type = type.BaseType)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!SymbolEqualityComparer.Default.Equals(type, controller) && !overridden.Contains(method.OriginalDefinition)
                    && IsAction(method) && method.Locations.Any(l => l.IsInSource))
                {
                    yield return method;
                }

                if (method.OverriddenMethod is { } hidden)
                {
                    overridden.Add(hidden.OriginalDefinition);
                }
            }
        }
    }

    public static IEnumerable<EndpointFact> Endpoints(INamedTypeSymbol controller, IMethodSymbol action, string handler, int line, ExtractionContext context)
    {
        var controllerRoutes = Names.SelfAndBases(controller)
            .Select(t => t.GetAttributes().Where(IsRoute).Select(FirstString).OfType<string>().ToList())
            .FirstOrDefault(routes => routes.Count > 0) ?? [];
        var selectors = Selectors(action);
        var auth = Auth(controller, action, context);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["controller"] = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^10] : controller.Name,
            ["action"] = ActionName(action),
            ["area"] = Names.SelfAndBases(controller).SelectMany(t => t.GetAttributes())
                .Where(a => Derives(a, Names.AreaAttribute)).Select(FirstString).FirstOrDefault() ?? string.Empty,
        };

        foreach (var (template, verbs) in selectors)
        {
            foreach (var prefix in controllerRoutes.Count == 0 ? [null] : controllerRoutes.Cast<string?>())
            {
                var route = template is null && prefix is null
                    ? $"(conventional) {tokens["controller"]}/{tokens["action"]}"
                    : template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal))
                        ? EndpointResolver.Join(string.Empty, template)
                        : EndpointResolver.Join(prefix ?? string.Empty, template ?? string.Empty);
                foreach (var token in tokens)
                {
                    route = route.Replace("[" + token.Key + "]", token.Value, StringComparison.OrdinalIgnoreCase);
                }

                foreach (var verb in verbs)
                {
                    yield return new EndpointFact(handler, verb, route, false, line, auth, null);
                }
            }
        }
    }

    private static AuthFact[] Auth(INamedTypeSymbol controller, IMethodSymbol action, ExtractionContext context)
    {
        var auth = new List<AuthFact>();
        foreach (var method in Chain(action))
        {
            auth.AddRange(AuthReader.Read(method.GetAttributes(), "action", context.Config));
        }

        foreach (var type in Names.SelfAndBases(controller))
        {
            var scope = SymbolEqualityComparer.Default.Equals(type, controller) ? "controller" : "base " + type.Name;
            auth.AddRange(AuthReader.Read(type.GetAttributes(), scope, context.Config));
        }

        return [.. auth];
    }

    /// <summary>One entry per route the action answers on, as the framework builds its selectors.</summary>
    private static List<(string? Template, string[] Verbs)> Selectors(IMethodSymbol action)
    {
        var attributes = Chain(action).SelectMany(m => m.GetAttributes()).ToList();
        var selectors = new List<(string?, string[])>();
        var bareVerbs = new List<string>();
        foreach (var attribute in attributes)
        {
            var verbs = VerbsOf(attribute);
            if (verbs is null)
            {
                continue;
            }

            var template = Derives(attribute, Names.AcceptVerbs)
                ? attribute.NamedArguments.FirstOrDefault(n => n.Key == "Route").Value.Value as string
                : FirstString(attribute);
            if (template is null)
            {
                bareVerbs.AddRange(verbs);
            }
            else
            {
                selectors.Add((template, verbs));
            }
        }

        var defaultVerbs = bareVerbs.Count > 0 ? bareVerbs.Distinct().ToArray() : ["ANY"];
        foreach (var route in attributes.Where(IsRoute).Select(FirstString).OfType<string>())
        {
            selectors.Add((route, defaultVerbs));
        }

        if (selectors.Count == 0 || (bareVerbs.Count > 0 && !attributes.Any(IsRoute)))
        {
            selectors.Add((null, defaultVerbs));
        }

        return selectors;
    }

    private static string[]? VerbsOf(AttributeData attribute)
    {
        if (Derives(attribute, Names.AcceptVerbs))
        {
            return attribute.ConstructorArguments
                .SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : [a])
                .Select(v => (v.Value as string)?.ToUpperInvariant()).OfType<string>().ToArray();
        }

        if (!Derives(attribute, Names.HttpMethodAttribute))
        {
            return null;
        }

        var known = Names.SelfAndBases(attribute.AttributeClass)
            .Select(t => VerbAttributes.GetValueOrDefault(Names.FullName(t))).FirstOrDefault(v => v is not null);
        return [known ?? "?"];
    }

    private static string ActionName(IMethodSymbol action)
    {
        var named = Chain(action).SelectMany(m => m.GetAttributes()).Where(a => Derives(a, Names.ActionName)).Select(FirstString).FirstOrDefault();
        if (named is not null)
        {
            return named;
        }

        return action.Name.EndsWith("Async", StringComparison.Ordinal) && action.Name.Length > 5 ? action.Name[..^5] : action.Name;
    }

    private static IEnumerable<IMethodSymbol> Chain(IMethodSymbol method)
    {
        for (var current = method; current is not null; current = current.OverriddenMethod)
        {
            yield return current;
        }
    }

    private static bool IsRoute(AttributeData attribute) => attribute.AttributeClass is { } type
        && Names.Implements(type, Names.RouteTemplateProvider) && !Derives(attribute, Names.HttpMethodAttribute);

    private static bool Derives(AttributeData attribute, string fullName) => Names.DerivesFrom(attribute.AttributeClass, fullName);

    private static bool HasAttribute(INamedTypeSymbol type, string fullName) =>
        Names.SelfAndBases(type).Any(t => t.GetAttributes().Any(a => Derives(a, fullName)));

    private static string? FirstString(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Primitive
            ? attribute.ConstructorArguments[0].Value as string
            : null;
}
