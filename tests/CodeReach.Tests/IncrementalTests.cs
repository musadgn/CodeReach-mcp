using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Tools;
using CodeReach.Mcp;
using CodeReach.Roslyn;
using Xunit;

namespace CodeReach.Tests;

public sealed class IncrementalTests
{
    /// <summary>After every kind of edit, updating the index must give exactly what indexing from scratch gives.</summary>
    [Fact]
    public async Task Incremental_update_equals_a_from_scratch_index_after_each_kind_of_edit()
    {
        using var sample = new TempSample();
        var cancel = TestContext.Current.CancellationToken;
        using var indexer = await RoslynIndexer.OpenAsync(sample.SolutionPath, SampleFixture.Config, null, cancel);
        var facts = await indexer.IndexAllAsync(null, cancel);
        var total = facts.Documents.Length;
        var domain = facts.Documents.Count(d => d.Project == "Shop.Domain");

        // 1. A body-only edit re-extracts one document.
        var repositories = "src/Shop.Infrastructure/Persistence/Repositories.cs";
        sample.Edit(repositories, "Store.Remove(id);", "Store.Remove(id);\n        _ = Store.Count;");
        facts = await indexer.UpdateAsync(facts, new SourceChanges([sample.PathOf(repositories)], [], []), null, cancel);
        Assert.Equal(1, indexer.LastExtracted);
        await AssertSameAsFreshAsync(sample, facts);

        // 2. A new member changes the declaration shape: the project and everything that depends on it are re-extracted.
        var selfTests = "src/Shop.Application/Diagnostics/SelfTests.cs";
        sample.Edit(selfTests, "public Task RunAsync()", "public Task AgainAsync() => orders.CancelAsync(Guid.Empty, \"again\");\n\n    public Task RunAsync()");
        facts = await indexer.UpdateAsync(facts, new SourceChanges([sample.PathOf(selfTests)], [], []), null, cancel);
        Assert.Equal(total - domain, indexer.LastExtracted);
        await AssertSameAsFreshAsync(sample, facts);

        // 3. A constant's value is declaration shape: an attribute argument in another project must follow it.
        var keys = "src/Shop.Domain/PermissionKeys.cs";
        sample.Edit(keys, "\"orders.delete\"", "\"orders.remove\"");
        facts = await indexer.UpdateAsync(facts, new SourceChanges([sample.PathOf(keys)], [], []), null, cancel);
        Assert.Equal(total, indexer.LastExtracted);
        var graph = new CodeGraph(facts, 1, DateTimeOffset.UnixEpoch);
        Assert.Contains(graph.Endpoints, e => e.Auth.Any(a => a.Args == "PermissionKeys.Orders.Delete = \"orders.remove\""));
        await AssertSameAsFreshAsync(sample, facts);

        // 4. A file is added, then removed.
        var added = sample.PathOf("src/Shop.Domain/Coupon.cs");
        await File.WriteAllTextAsync(added, "namespace Shop.Domain;\n\npublic record Coupon(string Code, Money Value);\n", cancel);
        facts = await indexer.UpdateAsync(facts, new SourceChanges([], [added], []), null, cancel);
        Assert.Contains(facts.Documents, d => d.Path == "src/Shop.Domain/Coupon.cs");
        await AssertSameAsFreshAsync(sample, facts);

        File.Delete(added);
        facts = await indexer.UpdateAsync(facts, new SourceChanges([], [], [added]), null, cancel);
        Assert.DoesNotContain(facts.Documents, d => d.Path == "src/Shop.Domain/Coupon.cs");
        await AssertSameAsFreshAsync(sample, facts);
    }

    [Fact]
    public async Task A_fresh_snapshot_is_served_without_a_workspace_and_an_edit_is_picked_up()
    {
        using var sample = new TempSample();
        var cancel = TestContext.Current.CancellationToken;
        var options = new HostOptions(sample.SolutionPath, SampleFixture.Config, sample.CacheDirectory);
        long firstGeneration;
        using (var cold = new IndexHost(options, Timeout.InfiniteTimeSpan))
        {
            var lease = await cold.AcquireAsync(cancel);
            await cold.SettleAsync();
            Assert.True(cold.WorkspaceLoaded);
            Assert.Null(lease.Notice);
            firstGeneration = lease.Graph.Generation;
        }

        using var warm = new IndexHost(options, Timeout.InfiniteTimeSpan);
        var served = await warm.AcquireAsync(cancel);
        Assert.False(warm.WorkspaceLoaded);
        Assert.Contains("loaded from snapshot", warm.DescribeState(), StringComparison.Ordinal);
        Assert.Equal(1, Callers(served.Graph));

        sample.Edit(
            "src/Shop.Application/Diagnostics/SelfTests.cs", "public Task RunAsync()",
            "public Task WipeAsync(Shop.Application.Abstractions.IOrderRepository r) => r.HardDeleteAsync(Guid.Empty);\n\n    public Task RunAsync()");
        var summary = await warm.ReindexAsync(full: false, cancel);
        var refreshed = await warm.AcquireAsync(cancel);

        Assert.StartsWith("Index refreshed", summary, StringComparison.Ordinal);
        Assert.True(warm.WorkspaceLoaded);
        Assert.True(refreshed.Graph.Generation > served.Graph.Generation);
        Assert.Equal(2, Callers(refreshed.Graph));
        Assert.True(firstGeneration >= 1);

        static int Callers(CodeGraph graph) =>
            Core.Queries.Reach.Callers(graph, Core.Queries.SymbolResolver.Resolve(graph, "OrderRepository.HardDeleteAsync"), includeTests: false).Shown.Count;
    }

    [Fact]
    public async Task A_snapshot_made_under_another_configuration_is_not_used()
    {
        using var sample = new TempSample();
        var cancel = TestContext.Current.CancellationToken;
        using (var first = new IndexHost(new HostOptions(sample.SolutionPath, SampleFixture.Config, sample.CacheDirectory), Timeout.InfiniteTimeSpan))
        {
            await first.AcquireAsync(cancel);
            await first.SettleAsync();
        }

        var other = SampleFixture.Config with { Exclude = [] };
        using var second = new IndexHost(new HostOptions(sample.SolutionPath, other, sample.CacheDirectory), Timeout.InfiniteTimeSpan);
        var lease = await second.AcquireAsync(cancel);

        Assert.True(second.WorkspaceLoaded);
        Assert.Contains(lease.Graph.Facts.Documents, d => d.Path.Contains("Migrations", StringComparison.Ordinal));
    }

    private static async Task AssertSameAsFreshAsync(TempSample sample, IndexFacts incremental)
    {
        using var fresh = await RoslynIndexer.OpenAsync(sample.SolutionPath, SampleFixture.Config, null, TestContext.Current.CancellationToken);
        var expected = await fresh.IndexAllAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(expected.Documents.Select(d => d.Path), incremental.Documents.Select(d => d.Path));
        foreach (var (wanted, actual) in expected.Documents.Zip(incremental.Documents))
        {
            Assert.True(
                FactsHash.Compute(expected with { Documents = [wanted], Projects = [] }) == FactsHash.Compute(incremental with { Documents = [actual], Projects = [] }),
                $"facts of {wanted.Path} differ between the incremental and the from-scratch index");
        }

        Assert.Equal(FactsHash.Compute(expected), FactsHash.Compute(incremental));
    }
}
