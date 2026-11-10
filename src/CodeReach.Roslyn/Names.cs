using Microsoft.CodeAnalysis;

namespace CodeReach.Roslyn;

/// <summary>Matches symbols against full metadata names, so no framework assembly has to be referenced here.</summary>
internal static class Names
{
    public const string ControllerBase = "Microsoft.AspNetCore.Mvc.ControllerBase";
    public const string ApiController = "Microsoft.AspNetCore.Mvc.ApiControllerAttribute";
    public const string Controller = "Microsoft.AspNetCore.Mvc.ControllerAttribute";
    public const string NonController = "Microsoft.AspNetCore.Mvc.NonControllerAttribute";
    public const string NonAction = "Microsoft.AspNetCore.Mvc.NonActionAttribute";
    public const string HttpMethodAttribute = "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";
    public const string AcceptVerbs = "Microsoft.AspNetCore.Mvc.AcceptVerbsAttribute";
    public const string RouteAttribute = "Microsoft.AspNetCore.Mvc.RouteAttribute";
    public const string RouteTemplateProvider = "Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider";
    public const string AreaAttribute = "Microsoft.AspNetCore.Mvc.AreaAttribute";
    public const string ActionName = "Microsoft.AspNetCore.Mvc.ActionNameAttribute";
    public const string AuthorizeData = "Microsoft.AspNetCore.Authorization.IAuthorizeData";
    public const string AllowAnonymous = "Microsoft.AspNetCore.Authorization.IAllowAnonymous";
    public const string AuthorizationFilter = "Microsoft.AspNetCore.Mvc.Filters.IAuthorizationFilter";
    public const string AsyncAuthorizationFilter = "Microsoft.AspNetCore.Mvc.Filters.IAsyncAuthorizationFilter";
    public const string RequirementData = "Microsoft.AspNetCore.Authorization.IAuthorizationRequirementData";
    public const string HostedService = "Microsoft.Extensions.Hosting.IHostedService";
    public const string BackgroundService = "Microsoft.Extensions.Hosting.BackgroundService";
    public const string EndpointRouteBuilder = "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";
    public const string MapExtensions = "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions";
    public const string AuthorizationExtensions = "Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions";
    public const string ControllerMapExtensions = "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions";

    /// <summary>Namespace-qualified metadata name of the original definition, with arity (<c>Ns.IFoo`1</c>).</summary>
    public static string FullName(INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "." + name;
        }

        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
    }

    public static bool Is(INamedTypeSymbol? type, string fullName) => type is not null && FullName(type) == fullName;

    public static IEnumerable<INamedTypeSymbol> SelfAndBases(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    public static bool DerivesFrom(INamedTypeSymbol? type, string fullName) => SelfAndBases(type).Any(t => Is(t, fullName));

    public static bool Implements(ITypeSymbol? type, string fullName) => type is INamedTypeSymbol named
        && (Is(named, fullName) || named.AllInterfaces.Any(i => Is(i, fullName)));

    /// <summary>Matches a user-supplied name that may omit the namespace, the arity or the <c>Attribute</c> suffix.</summary>
    public static bool Matches(INamedTypeSymbol type, string wanted)
    {
        var full = StripArity(FullName(type));
        wanted = StripArity(wanted);
        return Same(full, wanted) || Same(type.Name, wanted);

        static bool Same(string actual, string expected) => actual == expected
            || actual == expected + "Attribute";
    }

    public static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
