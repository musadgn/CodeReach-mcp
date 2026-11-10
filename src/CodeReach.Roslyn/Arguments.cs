using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>Finds the expression passed for a parameter, the way the compiler matched it.</summary>
internal static class Arguments
{
    public static ExpressionSyntax? Unwrap(ExpressionSyntax? expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized: expression = parenthesized.Expression; break;
                case CastExpressionSyntax cast: expression = cast.Expression; break;
                case PostfixUnaryExpressionSyntax suppress when suppress.OperatorToken.ValueText == "!": expression = suppress.Operand; break;
                default: return expression;
            }
        }
    }

    /// <summary>The expression passed for a parameter of the definition, with the receiver of a reduced extension call as ordinal 0.</summary>
    public static ExpressionSyntax? Of(InvocationExpressionSyntax invocation, IMethodSymbol bound, int ordinal)
    {
        var reduced = bound.ReducedFrom is not null;
        if (reduced && ordinal == 0)
        {
            return (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        }

        var definition = bound.ReducedFrom ?? bound;
        if (ordinal >= definition.Parameters.Length)
        {
            return null;
        }

        var arguments = invocation.ArgumentList.Arguments;
        var named = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == definition.Parameters[ordinal].Name);
        if (named is not null)
        {
            return named.Expression;
        }

        var index = ordinal - (reduced ? 1 : 0);
        return index >= 0 && index < arguments.Count && arguments[index].NameColon is null ? arguments[index].Expression : null;
    }

    public static ExpressionSyntax? Of(InvocationExpressionSyntax invocation, IMethodSymbol bound, string parameter)
    {
        var found = (bound.ReducedFrom ?? bound).Parameters.FirstOrDefault(p => p.Name == parameter);
        return found is null ? null : Of(invocation, bound, found.Ordinal);
    }
}
