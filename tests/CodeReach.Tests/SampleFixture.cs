using System.Diagnostics;
using CodeReach.Core;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Tools;
using CodeReach.Roslyn;
using Xunit;

namespace CodeReach.Tests;

/// <summary>Indexes the fictional SampleShop solution once for the whole test run.</summary>
public sealed class SampleFixture : IAsyncLifetime
{
    public static string SampleDirectory { get; } = Locate();

    public static string SolutionPath => Path.Combine(SampleDirectory, "SampleShop.slnx");

    public static CodeReachConfig Config => CodeReachConfig.Parse(File.ReadAllText(Path.Combine(SampleDirectory, CodeReachConfig.FileName)));

    public RoslynIndexer Indexer { get; private set; } = null!;

    public IndexFacts Facts { get; private set; } = null!;

    public CodeGraph Graph { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Restore(SampleDirectory);
        Indexer = await RoslynIndexer.OpenAsync(SolutionPath, Config, null, CancellationToken.None);
        Facts = await Indexer.IndexAllAsync(null, CancellationToken.None);
        Graph = new CodeGraph(Facts, 1, DateTimeOffset.UnixEpoch);
    }

    public ValueTask DisposeAsync()
    {
        Indexer.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>MSBuild can only resolve references of a restored solution; a fresh checkout has none.</summary>
    public static void Restore(string directory)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", ["restore", "--verbosity", "quiet"])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "dotnet restore of the sample failed:\n" + output);
    }

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "SampleShop");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("samples/SampleShop was not found above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition(Name)]
public sealed class SampleCollection : ICollectionFixture<SampleFixture>
{
    public const string Name = "sample";
}

/// <summary>A host that serves one fixed graph, so tools can be called without a process or a workspace.</summary>
internal sealed class FixedHost(CodeGraph graph, CodeReachConfig config, string? diff = null) : IIndexHost
{
    public CodeReachConfig Config => config;

    public Task<GraphLease> AcquireAsync(CancellationToken cancel) => Task.FromResult(new GraphLease(graph, null));

    public Task<string> ReindexAsync(bool full, CancellationToken cancel) => Task.FromResult("reindexed");

    public string DescribeState() => "state: fixed";

    public Task<string> GitDiffAsync(string? baseRef, CancellationToken cancel) => Task.FromResult(diff ?? string.Empty);
}
