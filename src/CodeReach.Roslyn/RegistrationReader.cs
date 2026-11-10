using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>A registration call as written: types may still be type parameters of an enclosing generic helper.</summary>
internal sealed record RegistrationCall(string Lifetime, string Method, ITypeSymbol Service, ITypeSymbol? Implementation, IReadOnlyList<string> Details, int Line, string FilePath);

/// <summary>
/// Reads IServiceCollection registration calls (AddScoped, TryAddSingleton, AddKeyedTransient, AddHostedService,
/// AddHttpClient, AddDbContext and their typeof, factory and instance forms) from syntax and a semantic model.
/// </summary>
internal static class RegistrationReader
{
    private static readonly Regex LifetimeMethod = new(@"^(Try)?Add(Keyed)?(Scoped|Transient|Singleton)$", RegexOptions.CultureInvariant);

    public static RegistrationCall? Read(InvocationExpressionSyntax invocation, IMethodSymbol bound, SemanticModel model, CancellationToken cancel)
    {
        var definition = bound.ReducedFrom ?? bound;
        if (definition.Parameters.Length == 0
            || !Names.Implements(definition.Parameters[0].Type, Names.ServiceCollection)
            || Lifetime(definition.Name) is not { } lifetime)
        {
            return null;
        }

        var arguments = bound.TypeArguments;
        var service = arguments.Length > 0 ? arguments[0] : TypeOfArgument(invocation, bound, "serviceType", model, cancel);
        var implementation = arguments.Length > 1 ? arguments[1] : TypeOfArgument(invocation, bound, "implementationType", model, cancel);
        var details = new List<string>();
        if (definition.Name.StartsWith("Try", StringComparison.Ordinal))
        {
            details.Add("only if not already registered");
        }

        if (Arguments.Of(invocation, bound, "serviceKey") is { } key)
        {
            details.Add("key " + key);
        }

        if (implementation is null)
        {
            if (Arguments.Of(invocation, bound, "implementationFactory") is { } factory)
            {
                implementation = FactoryResult(factory, model, cancel);
                details.Add(implementation is null ? "factory" : "factory creating " + implementation.Name);
            }
            else if (Arguments.Of(invocation, bound, "implementationInstance") is { } instance)
            {
                implementation = model.GetTypeInfo(instance, cancel).Type;
                details.Add("instance");
            }
            else
            {
                implementation = service;
            }
        }

        if (service is INamedTypeSymbol { IsUnboundGenericType: true } || implementation is INamedTypeSymbol { IsUnboundGenericType: true })
        {
            details.Add("open generic");
        }

        return service is null or IErrorTypeSymbol
            ? null
            : new RegistrationCall(lifetime, definition.Name, service, implementation, details, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1, invocation.SyntaxTree.FilePath);
    }

    private static string? Lifetime(string method)
    {
        var match = LifetimeMethod.Match(method);
        if (match.Success)
        {
            return match.Groups[3].Value.ToLowerInvariant();
        }

        return method switch
        {
            "AddHostedService" => "singleton",
            "AddHttpClient" => "transient",
            "AddDbContext" or "AddDbContextPool" => "scoped",
            "AddDbContextFactory" or "AddPooledDbContextFactory" => "singleton",
            _ => null,
        };
    }

    private static ITypeSymbol? TypeOfArgument(InvocationExpressionSyntax invocation, IMethodSymbol bound, string parameter, SemanticModel model, CancellationToken cancel) =>
        Arguments.Unwrap(Arguments.Of(invocation, bound, parameter)) is TypeOfExpressionSyntax typeOf
            ? model.GetTypeInfo(typeOf.Type, cancel).Type
            : null;

    /// <summary>The type a factory lambda creates, when its body is a single object creation.</summary>
    private static ITypeSymbol? FactoryResult(ExpressionSyntax factory, SemanticModel model, CancellationToken cancel)
    {
        var body = Arguments.Unwrap(factory) switch
        {
            LambdaExpressionSyntax { ExpressionBody: { } expression } => expression,
            LambdaExpressionSyntax { Block.Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
            _ => null,
        };
        return Arguments.Unwrap(body) is BaseObjectCreationExpressionSyntax creation ? model.GetTypeInfo(creation, cancel).Type : null;
    }
}

/// <summary>
/// Registration calls inside source generic helpers whose types are the helper's own type parameters
/// (<c>void Add&lt;T&gt;() =&gt; services.AddHostedService&lt;T&gt;()</c>). Each call of the helper is one registration.
/// </summary>
internal sealed class RegistrationTemplates(Compilation compilation)
{
    private readonly ConcurrentDictionary<IMethodSymbol, RegistrationCall[]> _cache = new(SymbolEqualityComparer.Default);

    public RegistrationCall[] Of(IMethodSymbol helper, CancellationToken cancel) =>
        !helper.IsGenericMethod || helper.DeclaringSyntaxReferences.Length == 0
            ? []
            : _cache.GetOrAdd(helper, h => Scan(h, cancel));

    private RegistrationCall[] Scan(IMethodSymbol helper, CancellationToken cancel)
    {
        var found = new List<RegistrationCall>();
        foreach (var reference in helper.DeclaringSyntaxReferences)
        {
            if (!compilation.SyntaxTrees.Contains(reference.SyntaxTree))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(reference.SyntaxTree);
            foreach (var invocation in reference.GetSyntax(cancel).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation, cancel).Symbol is IMethodSymbol bound
                    && RegistrationReader.Read(invocation, bound, model, cancel) is { } call
                    && (OwnParameter(call.Service, helper) || OwnParameter(call.Implementation, helper)))
                {
                    found.Add(call);
                }
            }
        }

        return [.. found];
    }

    private static bool OwnParameter(ITypeSymbol? type, IMethodSymbol helper) =>
        type is ITypeParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.DeclaringMethod, helper);
}
