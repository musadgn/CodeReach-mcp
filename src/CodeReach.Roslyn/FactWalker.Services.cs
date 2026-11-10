using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>Dependency injection: registrations made on an IServiceCollection, and what constructors ask for.</summary>
internal sealed partial class FactWalker
{
    private void RecordDependencies(IMethodSymbol constructor, string id)
    {
        if (constructor.ContainingType is not { TypeKind: TypeKind.Class or TypeKind.Struct, IsStatic: false })
        {
            return;
        }

        foreach (var parameter in constructor.Parameters)
        {
            var type = parameter.Type as INamedTypeSymbol;
            var many = false;
            if (type is { IsGenericType: true, TypeArguments: [INamedTypeSymbol element] }
                && Names.FullName(type) == "System.Collections.Generic.IEnumerable`1")
            {
                type = element;
                many = true;
            }

            if (ServiceKey(type) is { } key)
            {
                Dependencies.Add(new DependencyFact(id, parameter.Ordinal, key, many));
            }
        }
    }

    /// <summary>Records a registration call, or the registrations a generic helper makes for these type arguments.</summary>
    private bool ReadRegistration(InvocationExpressionSyntax invocation, IMethodSymbol bound)
    {
        if (RegistrationReader.Read(invocation, bound, _model, _cancel) is { } call)
        {
            // A call whose types are a helper's type parameters is recorded at each call of the helper instead.
            if (ServiceKey(call.Service) is { } service)
            {
                AddService(service, ServiceKey(call.Implementation), call, call.Line, call.Details);
            }

            return true;
        }

        var helper = bound.OriginalDefinition;
        var templates = _context.Templates.Of(helper, _cancel);
        foreach (var template in templates)
        {
            var service = ServiceKey(Substitute(template.Service, helper, bound));
            if (service is null)
            {
                continue;
            }

            var through = $"through {helper.Name}<{string.Join(", ", bound.TypeArguments.Select(t => t.Name))}>, {Relative(template)}";
            AddService(service, ServiceKey(Substitute(template.Implementation, helper, bound)), template, LineOf(invocation), [.. template.Details, through]);
        }

        return templates.Length > 0;
    }

    private void AddService(string service, string? implementation, RegistrationCall call, int line, IEnumerable<string> details) =>
        Services.Add(new ServiceFact(
            service, implementation, call.Lifetime, call.Method, line, _owners.Count > 0 ? _owners.Peek() : string.Empty, string.Join(", ", details)));

    private static ITypeSymbol? Substitute(ITypeSymbol? type, IMethodSymbol helper, IMethodSymbol bound) =>
        type is ITypeParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.DeclaringMethod, helper)
            ? bound.TypeArguments[parameter.Ordinal]
            : type;

    private string Relative(RegistrationCall template) =>
        $"{Path.GetRelativePath(_context.Root, template.FilePath).Replace('\\', '/')}:{template.Line}";

    /// <summary>The key of a service type: constructed (<c>ChannelReader&lt;Event&gt;</c>), or the open definition for <c>typeof(IRepo&lt;&gt;)</c>.</summary>
    private string? ServiceKey(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Error } => null,
        INamedTypeSymbol { IsUnboundGenericType: true } unbound => Ids.TypeKey(unbound.OriginalDefinition),
        INamedTypeSymbol named => Ids.TypeKey(named),
        _ => null,
    };
}
