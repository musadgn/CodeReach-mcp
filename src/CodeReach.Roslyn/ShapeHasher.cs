using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeReach.Roslyn;

/// <summary>
/// Hashes the declaration shape of a document: every token outside method bodies, accessor bodies and
/// top-level statements. Two versions with the same shape cannot change what any other document binds to.
/// </summary>
internal static class ShapeHasher
{
    public static string Hash(SyntaxNode root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> separator = [0];
        foreach (var token in root.DescendantTokens(node => !IsBody(node)))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(token.Text));
            hash.AppendData(separator);
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..24];
    }

    private static bool IsBody(SyntaxNode node) => node switch
    {
        GlobalStatementSyntax => true,
        BlockSyntax => node.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax,
        ArrowExpressionClauseSyntax => node.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
            or PropertyDeclarationSyntax or IndexerDeclarationSyntax,
        _ => false,
    };
}
