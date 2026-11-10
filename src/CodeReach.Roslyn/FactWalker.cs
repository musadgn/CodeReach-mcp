using CodeReach.Core.Facts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>
/// Walks one syntax tree and records facts. This partial holds the owner stack: which symbol the code
/// being visited belongs to.
/// </summary>
internal sealed partial class FactWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly ExtractionContext _context;
    private readonly string _path;
    private readonly CancellationToken _cancel;
    private readonly Stack<string> _owners = new();
    private readonly Stack<INamedTypeSymbol> _types = new();
    private readonly HashSet<SyntaxNode> _handlerLambdas = [];
    private readonly Dictionary<SyntaxNode, string> _handlerIds = [];

    public FactWalker(SemanticModel model, ExtractionContext context, string path, CancellationToken cancel)
    {
        _model = model;
        _context = context;
        _path = path;
        _cancel = cancel;
    }

    public List<SymbolFact> Symbols { get; } = [];

    public List<ReferenceFact> References { get; } = [];

    public List<TypeFact> Types { get; } = [];

    public List<OverrideFact> Overrides { get; } = [];

    public List<EndpointFact> Endpoints { get; } = [];

    public List<GroupArgFact> GroupArgs { get; } = [];

    public List<EntryPointFact> EntryPoints { get; } = [];

    private SymbolIds Ids => _context.Ids;

    public override void Visit(SyntaxNode? node)
    {
        if (node is null)
        {
            return;
        }

        _cancel.ThrowIfCancellationRequested();
        var (owner, type) = Enter(node);
        if (owner is not null)
        {
            _owners.Push(owner);
        }

        if (type is not null)
        {
            _types.Push(type);
        }

        base.Visit(node);
        if (type is not null)
        {
            _types.Pop();
        }

        if (owner is not null)
        {
            _owners.Pop();
        }
    }

    // Using directives and namespace names bind to nothing the model tracks.
    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
    }

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private (string? Owner, INamedTypeSymbol? Type) Enter(SyntaxNode node)
    {
        switch (node)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                var type = _model.GetDeclaredSymbol(node, _cancel) as INamedTypeSymbol;
                return type is null ? (null, null) : (DeclareType(type, node), type);
            case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax:
                return (DeclareMember(_model.GetDeclaredSymbol(node, _cancel), node), null);
            case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field }:
                return (DeclareMember(_model.GetDeclaredSymbol(node, _cancel), field, node), null);
            case GlobalStatementSyntax when _owners.Count == 0:
                return (DeclareTopLevel(node), null);
            case AnonymousFunctionExpressionSyntax when _handlerLambdas.Contains(node):
                return (_handlerIds[node], null);
            default:
                return (null, null);
        }
    }

    private string? DeclareTopLevel(SyntaxNode statement)
    {
        if (_model.GetEnclosingSymbol(statement.SpanStart, _cancel) is not IMethodSymbol main || Ids.IdOf(main) is not { } id)
        {
            return null;
        }

        if (Symbols.All(s => s.Id != id))
        {
            var unit = statement.Parent!;
            var first = unit.ChildNodes().OfType<GlobalStatementSyntax>().First();
            var last = unit.ChildNodes().OfType<GlobalStatementSyntax>().Last();
            Symbols.Add(new SymbolFact(
                id, SymKind.Method, main.Name, main.ContainingType.ToDisplayString(SymbolIds.QualifiedName) + "." + main.Name,
                main.ContainingType.Name + " (top-level statements)", Ids.IdOf(main.ContainingType), LineOf(first), LineOf(first),
                last.GetLocation().GetLineSpan().EndLinePosition.Line + 1, SymFlags.Static));
            EntryPoints.Add(new EntryPointFact(id, "main", main.ContainingAssembly.Name));
        }

        return id;
    }
}
