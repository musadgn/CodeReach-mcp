using System.Reflection;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;
using CodeReach.Mcp;
using CodeReach.Roslyn;
using Xunit;

namespace CodeReach.Tests;

/// <summary>The layering of ARCHITECTURE.md, checked on the built assemblies.</summary>
public sealed class WallTests
{
    private static readonly Assembly Core = typeof(CodeGraph).Assembly;
    private static readonly Assembly Roslyn = typeof(RoslynIndexer).Assembly;
    private static readonly Assembly Host = typeof(IndexHost).Assembly;

    [Fact]
    public void Core_references_no_compiler_no_protocol_and_no_other_layer()
    {
        Assert.Empty(Forbidden(Core, "Microsoft.CodeAnalysis", "Microsoft.Build", "ModelContextProtocol", "CodeReach.Roslyn", "codereach"));
    }

    [Fact]
    public void Roslyn_layer_references_no_protocol_and_not_the_host()
    {
        Assert.Empty(Forbidden(Roslyn, "ModelContextProtocol", "Microsoft.Extensions.Hosting", "codereach"));
        Assert.Contains(Roslyn.GetReferencedAssemblies(), a => a.Name == "CodeReach.Core");
    }

    [Fact]
    public void The_wall_check_itself_bites()
    {
        // The host legitimately references all of these, so the same check must report them there.
        Assert.NotEmpty(Forbidden(Host, "ModelContextProtocol"));
        Assert.NotEmpty(Forbidden(Host, "CodeReach.Roslyn"));
        Assert.NotEmpty(Forbidden(Roslyn, "Microsoft.CodeAnalysis"));
    }

    [Fact]
    public void Diff_parser_reads_new_side_ranges_deletions_and_removed_files()
    {
        const string diff = """
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -3,2 +3,4 @@ class A
            @@ -20 +22,0 @@ class A
            @@ -30 +31 @@ class A
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,5 +0,0 @@
            """;
        var changes = DiffParser.Parse(diff);

        Assert.Equal(["src/A.cs", "src/Gone.cs"], changes.Select(c => c.Path));
        Assert.Equal([(3, 6), (22, 23), (31, 31)], changes[0].Ranges);
        Assert.False(changes[0].Deleted);
        Assert.True(changes[1].Deleted);
    }

    private static IEnumerable<string> Forbidden(Assembly assembly, params string[] prefixes) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(name => prefixes.Any(p => name == p || name.StartsWith(p + ".", StringComparison.Ordinal)));
}
