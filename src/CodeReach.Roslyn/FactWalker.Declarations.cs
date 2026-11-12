using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

internal sealed partial class FactWalker
{
    private static SymKind KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => SymKind.Type,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => SymKind.Constructor,
        IMethodSymbol => SymKind.Method,
        IPropertySymbol => SymKind.Property,
        IEventSymbol => SymKind.Event,
        _ => SymKind.Field,
    };

    private static SymFlags FlagsOf(ISymbol symbol)
    {
        var flags = SymFlags.None;
        flags |= symbol.IsStatic ? SymFlags.Static : 0;
        flags |= symbol.IsAbstract ? SymFlags.Abstract : 0;
        flags |= symbol.IsVirtual ? SymFlags.Virtual : 0;
        flags |= symbol.IsOverride ? SymFlags.Override : 0;
        flags |= symbol.DeclaredAccessibility == Accessibility.Public ? SymFlags.Public : 0;
        return flags;
    }

    private static SyntaxToken NameToken(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        MethodDeclarationSyntax method => method.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        VariableDeclaratorSyntax variable => variable.Identifier,
        EnumMemberDeclarationSyntax member => member.Identifier,
        ParameterSyntax parameter => parameter.Identifier,
        _ => node.GetFirstToken(),
    };

    private string? Declare(ISymbol? symbol, SyntaxNode span, SyntaxNode? nameNode = null)
    {
        if (symbol is null || Ids.IdOf(symbol) is not { } id)
        {
            return null;
        }

        var lines = span.GetLocation().GetLineSpan();
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor
            ? constructor.IsStatic ? "#cctor" : "#ctor"
            : symbol.Name;
        var qname = symbol is INamedTypeSymbol || symbol.ContainingType is null
            ? symbol.ToDisplayString(SymbolIds.QualifiedName)
            : symbol.ContainingType.ToDisplayString(SymbolIds.QualifiedName) + "." + name;

        Symbols.Add(new SymbolFact(
            id, KindOf(symbol), name, qname, symbol.ToDisplayString(SymbolIds.Short),
            Ids.IdOf(symbol.ContainingType),
            LineOf(NameToken(nameNode ?? span)), lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1, FlagsOf(symbol)));
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, IsStatic: false } instanceConstructor)
        {
            RecordDependencies(instanceConstructor, id);
        }

        return id;
    }

    private string? DeclareType(INamedTypeSymbol type, SyntaxNode node)
    {
        var id = Declare(type, node);
        if (id is null)
        {
            return null;
        }

        if (Types.All(t => t.Id != id))
        {
            Types.Add(TypeFacts.Build(type, id, Ids));
        }

        if (_context.IsController(type))
        {
            foreach (var inherited in ControllerReader.InheritedActions(type))
            {
                if (Ids.IdOf(inherited) is { } handler)
                {
                    Endpoints.AddRange(ControllerReader.Endpoints(type, inherited, handler, LineOf(NameToken(node)), _context));
                }
            }
        }

        // Members that have no declaration node of their own: primary constructors and positional record properties.
        foreach (var member in type.GetMembers())
        {
            var reference = member.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == node.SyntaxTree);
            if (reference is null)
            {
                continue;
            }

            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } && reference.Span == node.Span)
            {
                // A primary constructor spans only its parameter list: an edit elsewhere in the type does not change it.
                Declare(member, node is TypeDeclarationSyntax { ParameterList: { } parameters } ? parameters : node, node);
            }
            else if (member is IPropertySymbol && reference.GetSyntax(_cancel) is ParameterSyntax parameter && node.Span.Contains(parameter.Span))
            {
                Declare(member, parameter);
            }
        }

        return id;
    }

    private string? DeclareMember(ISymbol? symbol, SyntaxNode span, SyntaxNode? nameNode = null)
    {
        var id = Declare(symbol, span, nameNode);
        if (id is null || symbol is null)
        {
            return id;
        }

        var overridden = symbol switch
        {
            IMethodSymbol method => (ISymbol?)method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };
        if (Ids.IdOf(overridden) is { } overriddenId)
        {
            Overrides.Add(new OverrideFact(id, overriddenId));
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } action)
        {
            if (_context.IsController(action.ContainingType) && ControllerReader.IsAction(action))
            {
                Endpoints.AddRange(ControllerReader.Endpoints(action.ContainingType, action, id, LineOf(NameToken(span)), _context));
            }

            EntryPoints.AddRange(RootRules.Match(action, id, _context));
        }

        return id;
    }
}
