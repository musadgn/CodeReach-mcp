using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RefKind = CodeReach.Core.Facts.RefKind;

namespace CodeReach.Roslyn;

internal sealed partial class FactWalker
{
    public override void VisitIdentifierName(IdentifierNameSyntax node) => HandleName(node);

    public override void VisitGenericName(GenericNameSyntax node)
    {
        HandleName(node);
        base.VisitGenericName(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        if (_owners.Count > 0 && _model.GetSymbolInfo(node, _cancel).Symbol is IMethodSymbol constructor)
        {
            Add(constructor, RefKind.Attribute, node);
            Add(constructor.ContainingType, RefKind.TypeRef, node);
        }

        if (node.ArgumentList is not null)
        {
            Visit(node.ArgumentList);
        }
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        AddBound(node, RefKind.Construct);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        AddBound(node, RefKind.Construct);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        AddBound(node, RefKind.BaseCall);
        base.VisitConstructorInitializer(node);
    }

    public override void VisitPrimaryConstructorBaseType(PrimaryConstructorBaseTypeSyntax node)
    {
        AddBound(node, RefKind.BaseCall);
        base.VisitPrimaryConstructorBaseType(node);
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (_owners.Count > 0 && _model.GetSymbolInfo(node, _cancel).Symbol is IPropertySymbol indexer)
        {
            Add(indexer, IsWrite(node) ? RefKind.Write : RefKind.Read, node, _model.GetTypeInfo(node.Expression, _cancel).Type);
        }

        base.VisitElementAccessExpression(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        AddOperator(node);
        base.VisitBinaryExpression(node);
    }

    public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        AddOperator(node);
        base.VisitPrefixUnaryExpression(node);
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        AddOperator(node);
        base.VisitPostfixUnaryExpression(node);
    }

    public override void VisitCastExpression(CastExpressionSyntax node)
    {
        AddOperator(node);
        base.VisitCastExpression(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            AddOperator(node);
        }

        base.VisitAssignmentExpression(node);
    }

    private static SyntaxNode TopOf(SyntaxNode name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == name => access,
        MemberBindingExpressionSyntax binding when binding.Name == name => binding,
        QualifiedNameSyntax qualified when qualified.Right == name => qualified,
        AliasQualifiedNameSyntax alias when alias.Name == name => alias,
        _ => name,
    };

    private static bool IsWrite(SyntaxNode top) => top.Parent switch
    {
        AssignmentExpressionSyntax assignment => assignment.Left == top,
        PrefixUnaryExpressionSyntax unary => unary.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression,
        PostfixUnaryExpressionSyntax unary => unary.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression,
        ArgumentSyntax argument => argument.RefKindKeyword.Kind() is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword,
        NameEqualsSyntax { Parent: AttributeArgumentSyntax } => true,
        _ => false,
    };

    private void HandleName(SimpleNameSyntax node)
    {
        if (_owners.Count == 0 || node.IsVar)
        {
            return;
        }

        var info = _model.GetSymbolInfo(node, _cancel);
        var symbol = info.Symbol;
        var inNameOf = IsInNameOf(node);
        if (symbol is null)
        {
            // A method group handed to a parameter typed as Delegate has no single bound symbol, only its candidate.
            var uninvoked = TopOf(node) is var group && !(group.Parent is InvocationExpressionSyntax call && call.Expression == group);
            var soleMethodGroup = uninvoked && info.CandidateSymbols is [IMethodSymbol]
                && info.CandidateReason is CandidateReason.MemberGroup or CandidateReason.OverloadResolutionFailure;
            if (info.CandidateSymbols.IsEmpty || !(inNameOf || soleMethodGroup))
            {
                return;
            }

            symbol = info.CandidateSymbols[0];
        }

        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        var top = TopOf(node);
        switch (symbol)
        {
            case INamedTypeSymbol type:
                Add(type, inNameOf ? RefKind.NameOf : RefKind.TypeRef, node);
                break;
            case IMethodSymbol method:
                var (receiver, viaBase) = ReceiverOf(top);
                var invocation = top.Parent is InvocationExpressionSyntax call && call.Expression == top ? call : null;
                var kind = inNameOf ? RefKind.NameOf
                    : invocation is null ? RefKind.MethodGroup
                    : viaBase ? RefKind.BaseCall : RefKind.Invoke;
                Add(method, kind, node, receiver, viaBase);
                if (invocation is not null && !inNameOf)
                {
                    InspectInvocation(invocation, method);
                }

                break;
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                var (memberReceiver, memberViaBase) = ReceiverOf(top);
                Add(symbol, inNameOf ? RefKind.NameOf : IsWrite(top) ? RefKind.Write : RefKind.Read, node, memberReceiver, memberViaBase);
                break;
        }
    }

    private bool IsInNameOf(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null and not StatementSyntax and not MemberDeclarationSyntax; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } candidate
                && _model.GetSymbolInfo(candidate, _cancel).Symbol is null)
            {
                return true;
            }
        }

        return false;
    }

    private (ITypeSymbol? Type, bool ViaBase) ReceiverOf(SyntaxNode top)
    {
        switch (top)
        {
            case MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }:
                return (null, true);
            case MemberAccessExpressionSyntax access:
                return (_model.GetTypeInfo(access.Expression, _cancel).Type, false);
            case MemberBindingExpressionSyntax:
                var conditional = top.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                    .FirstOrDefault(c => c.WhenNotNull.Span.Contains(top.Span));
                var type = conditional is null ? null : _model.GetTypeInfo(conditional.Expression, _cancel).Type;
                return (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                    ? nullable.TypeArguments[0] : type, false);
            default:
                return (_types.Count > 0 ? _types.Peek() : null, false);
        }
    }

    private void AddBound(SyntaxNode node, RefKind kind)
    {
        if (_owners.Count > 0 && _model.GetSymbolInfo(node, _cancel).Symbol is IMethodSymbol constructor)
        {
            Add(constructor, kind, node);
        }
    }

    private void AddOperator(SyntaxNode node)
    {
        if (_owners.Count > 0 && _model.GetSymbolInfo(node, _cancel).Symbol is IMethodSymbol
            { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } method)
        {
            Add(method, RefKind.Invoke, node);
        }
    }

    private void Add(ISymbol target, RefKind kind, SyntaxNode at, ITypeSymbol? receiverType = null, bool viaBase = false)
    {
        if (_owners.Count == 0 || Ids.IdOf(target) is not { } id)
        {
            return;
        }

        var member = SymbolIds.Normalize(target);
        var dispatch = Dispatch.None;
        string? receiver = null;
        var dispatchable = kind is RefKind.Invoke or RefKind.MethodGroup or RefKind.Read or RefKind.Write
            && member is IMethodSymbol or IPropertySymbol or IEventSymbol && !member.IsStatic && !viaBase;
        if (dispatchable && member.ContainingType is { TypeKind: TypeKind.Class or TypeKind.Interface } owner)
        {
            dispatch = owner.TypeKind == TypeKind.Interface ? Dispatch.Interface
                : member.IsVirtual || member.IsAbstract || member.IsOverride ? Dispatch.Virtual : Dispatch.None;
            receiver = ReceiverKey(receiverType, owner);
        }

        References.Add(new ReferenceFact(_owners.Peek(), id, kind, LineOf(at), dispatch, dispatchable && member.IsAbstract, receiver));
    }

    /// <summary>Null means "the member's own containing type", which is all a non-generic exact match can say.</summary>
    private string? ReceiverKey(ITypeSymbol? receiverType, INamedTypeSymbol memberOwner)
    {
        if (receiverType is not INamedTypeSymbol named)
        {
            return null;
        }

        var sameType = SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, memberOwner.OriginalDefinition);
        return sameType && !named.IsGenericType ? null : Ids.TypeKey(named);
    }
}
