using System.Security.Cryptography;
using System.Text.Json;

namespace CodeReach.Core.Facts;

/// <summary>A canonical hash of what was indexed, independent of where the solution lives and when it was read.</summary>
public static class FactsHash
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    public static string Compute(IndexFacts facts)
    {
        var canonical = new
        {
            Projects = facts.Projects.OrderBy(p => p.Name, StringComparer.Ordinal),
            Documents = facts.Documents.OrderBy(d => d.Path, StringComparer.Ordinal),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, Json)));
    }
}
