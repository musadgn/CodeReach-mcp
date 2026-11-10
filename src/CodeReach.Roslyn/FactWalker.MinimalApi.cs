using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>Minimal API endpoints, the route groups they hang from, and authorization applied in fluent chains.</summary>
internal sealed partial class FactWalker
{
    private const int MaxBuilderDepth = 12;

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
        ["MapMethods"] = "*",
        ["Map"] = "ANY",
        ["MapFallback"] = "ANY",
    };

    private sealed record Builder(string Prefix, List<AuthFact> Auth, PrefixParam? Via);

    private static ExpressionSyntax? Unwrap(ExpressionSyntax? expression) => Arguments.Unwrap(expression);

    private static ExpressionSyntax? Argument(InvocationExpressionSyntax invocation, IMethodSymbol bound, int ordinal) =>
        Arguments.Of(invocation, bound, ordinal);

    private static ExpressionSyntax? Argument(InvocationExpressionSyntax invocation, IMethodSymbol bound, string parameter) =>
        Arguments.Of(invocation, bound, parameter);

    private void InspectInvocation(InvocationExpressionSyntax invocation, IMethodSymbol bound)
    {
        var definition = (bound.ReducedFrom ?? bound).OriginalDefinition;
        if (ReadRegistration(invocation, bound))
        {
            return;
        }

        if (Names.Is(definition.ContainingType, Names.MapExtensions) && MapVerbs.TryGetValue(definition.Name, out var verb))
        {
            ReadEndpoint(invocation, bound, verb);
            return;
        }

        if (AuthCall(invocation, definition, "global") is { } global && ChainContains(invocation, Names.ControllerMapExtensions))
        {
            GroupArgs.Add(new GroupArgFact(EndpointResolver.AllControllers, -1, string.Empty, [global], null));
            return;
        }

        foreach (var ordinal in _context.BuilderParameters(definition))
        {
            if (Argument(invocation, bound, ordinal) is { } expression && Ids.IdOf(definition) is { } callee)
            {
                var builder = Resolve(expression, 0);
                GroupArgs.Add(new GroupArgFact(callee, ordinal, builder.Prefix, [.. builder.Auth], builder.Via));
            }
        }
    }

    private void ReadEndpoint(InvocationExpressionSyntax invocation, IMethodSymbol bound, string verb)
    {
        var definition = bound.ReducedFrom ?? bound;
        var pattern = Argument(invocation, bound, "pattern") is { } patternExpression
            ? Constant(patternExpression) ?? "{dynamic}"
            : definition.Name == "MapFallback" ? "{*path:nonfile}" : string.Empty;
        if (verb == "*")
        {
            var methods = Argument(invocation, bound, "httpMethods")?.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
                .Select(l => Constant(l)?.ToUpperInvariant()).OfType<string>().ToList() ?? [];
            verb = methods.Count > 0 ? string.Join(',', methods) : "?";
        }

        var handler = Unwrap(invocation.ArgumentList.Arguments.LastOrDefault()?.Expression);
        var builder = Resolve(Argument(invocation, bound, 0), 0);
        var auth = new List<AuthFact>(builder.Auth);
        var handlerSymbol = handler is null ? null : _model.GetSymbolInfo(handler, _cancel) is var info
            ? info.Symbol ?? info.CandidateSymbols.FirstOrDefault() : null;
        string? handlerId;
        if (handler is AnonymousFunctionExpressionSyntax or null || handlerSymbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary })
        {
            handlerId = DeclareHandler(handler ?? invocation, verb, pattern);
        }
        else
        {
            handlerId = Ids.IdOf(handlerSymbol) ?? DeclareHandler(handler, verb, pattern);
        }

        if (handlerSymbol is IMethodSymbol method)
        {
            auth.AddRange(AuthReader.Read(method.GetAttributes(), "endpoint", _context.Config));
        }

        for (SyntaxNode current = invocation; current.Parent is MemberAccessExpressionSyntax access && access.Expression == current
            && access.Parent is InvocationExpressionSyntax outer; current = outer)
        {
            if (_model.GetSymbolInfo(outer, _cancel).Symbol is IMethodSymbol fluent && AuthCall(outer, fluent, "endpoint") is { } fact)
            {
                auth.Add(fact);
            }
        }

        Endpoints.Add(new EndpointFact(
            handlerId, verb, EndpointResolver.Join(builder.Prefix, pattern), true, LineOf(invocation), [.. auth], builder.Via));
    }

    private string DeclareHandler(SyntaxNode node, string verb, string pattern)
    {
        var position = node.GetLocation().GetLineSpan();
        var line = position.StartLinePosition.Line + 1;
        var id = $"{CodeReach.Core.Facts.Ids.HandlerPrefix}[{_context.Compilation.AssemblyName}]{_path}:{line}:{position.StartLinePosition.Character + 1}";
        if (node is AnonymousFunctionExpressionSyntax)
        {
            _handlerLambdas.Add(node);
            _handlerIds[node] = id;
        }

        Symbols.Add(new SymbolFact(
            id, SymKind.Handler, "handler", $"{_path}:{line}", $"{verb} {pattern} handler", null, line, line,
            position.EndLinePosition.Line + 1, SymFlags.None));
        return id;
    }

    private string? Constant(ExpressionSyntax expression) => _model.GetConstantValue(expression, _cancel) is { HasValue: true, Value: string text } ? text : null;

    private bool ChainContains(InvocationExpressionSyntax invocation, string extensionType)
    {
        for (var current = Unwrap(invocation); current is InvocationExpressionSyntax call; current = Unwrap((call.Expression as MemberAccessExpressionSyntax)?.Expression))
        {
            if (_model.GetSymbolInfo(call, _cancel).Symbol is IMethodSymbol method
                && Names.Is((method.ReducedFrom ?? method).ContainingType, extensionType))
            {
                return true;
            }
        }

        return false;
    }

    private AuthFact? AuthCall(InvocationExpressionSyntax invocation, IMethodSymbol method, string scope)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        var arguments = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Constant(a.Expression) is { } text ? $"\"{text}\"" : a.Expression.ToString()));
        if (Names.Is(definition.ContainingType, Names.AuthorizationExtensions))
        {
            return definition.Name switch
            {
                "RequireAuthorization" => new AuthFact(AuthKind.Authorize, "RequireAuthorization", arguments, scope),
                "AllowAnonymous" => new AuthFact(AuthKind.AllowAnonymous, "AllowAnonymous", string.Empty, scope),
                _ => null,
            };
        }

        return _context.Config.AuthEndpointMethods.Contains(definition.Name, StringComparer.Ordinal)
            ? new AuthFact(AuthKind.Custom, definition.Name, arguments, scope)
            : null;
    }

    /// <summary>Follows a route builder expression back to where its prefix and authorization come from.</summary>
    private Builder Resolve(ExpressionSyntax? expression, int depth)
    {
        expression = Unwrap(expression);
        if (expression is null || depth > MaxBuilderDepth)
        {
            return new Builder(string.Empty, [], null);
        }

        if (expression is InvocationExpressionSyntax invocation && _model.GetSymbolInfo(invocation, _cancel).Symbol is IMethodSymbol method)
        {
            var definition = (method.ReducedFrom ?? method).OriginalDefinition;
            var receiver = method.ReducedFrom is not null ? Argument(invocation, method, 0)
                : (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
            if (receiver is null)
            {
                return new Builder(string.Empty, [], null);
            }

            var inner = Resolve(receiver, depth + 1);
            if (Names.Is(definition.ContainingType, Names.MapExtensions) && definition.Name == "MapGroup")
            {
                var prefix = Argument(invocation, method, "prefix") is { } prefixExpression ? Constant(prefixExpression) ?? "{dynamic}" : string.Empty;
                return inner with { Prefix = EndpointResolver.Join(inner.Prefix, prefix) };
            }

            if (AuthCall(invocation, method, "group") is { } fact)
            {
                inner.Auth.Add(fact);
            }

            return inner;
        }

        if (expression is not IdentifierNameSyntax identifier)
        {
            return new Builder(string.Empty, [], null);
        }

        switch (_model.GetSymbolInfo(identifier, _cancel).Symbol)
        {
            case ILocalSymbol local when local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_cancel) is VariableDeclaratorSyntax declarator:
                var builder = Resolve(declarator.Initializer?.Value, depth + 1);
                builder.Auth.AddRange(StatementAuth(local, declarator));
                return builder;
            case IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Ordinary } owner } parameter when Ids.IdOf(owner) is { } ownerId:
                return new Builder(string.Empty, [], new PrefixParam(ownerId, parameter.Ordinal));
            case IParameterSymbol:
                return new Builder(EndpointResolver.UnresolvedPrefix, [], null);
            default:
                return new Builder(string.Empty, [], null);
        }
    }

    /// <summary>
    /// True for a fluent convention call, which hands back the builder it was called on. A mapping call returns
    /// a new builder for the endpoint or group it created, so what follows it no longer applies to the receiver.
    /// </summary>
    private bool ReturnsItsReceiver(InvocationExpressionSyntax call, MemberAccessExpressionSyntax access) =>
        SymbolEqualityComparer.Default.Equals(_model.GetTypeInfo(call, _cancel).Type, _model.GetTypeInfo(access.Expression, _cancel).Type);

    /// <summary>Authorization applied to a builder variable in its own statement (<c>group.RequireAuthorization();</c>).</summary>
    private IEnumerable<AuthFact> StatementAuth(ILocalSymbol local, VariableDeclaratorSyntax declarator)
    {
        var scope = declarator.FirstAncestorOrSelf<BlockSyntax>() ?? declarator.SyntaxTree.GetRoot(_cancel);
        foreach (var statement in scope.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            var found = new List<AuthFact>();
            var current = Unwrap(statement.Expression);
            while (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call && ReturnsItsReceiver(call, access))
            {
                if (_model.GetSymbolInfo(call, _cancel).Symbol is IMethodSymbol method && AuthCall(call, method, "group") is { } fact)
                {
                    found.Add(fact);
                }

                current = Unwrap(access.Expression);
            }

            if (found.Count > 0 && current is IdentifierNameSyntax root
                && SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(root, _cancel).Symbol, local))
            {
                foreach (var fact in found)
                {
                    yield return fact;
                }
            }
        }
    }
}
