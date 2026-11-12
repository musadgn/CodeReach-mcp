using System.Text.Json;
using CodeReach.Core;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;
using CodeReach.Core.Tools;
using CodeReach.Roslyn;
using Xunit;

namespace CodeReach.Tests;

[Collection(SampleCollection.Name)]
public sealed class ToolTests(SampleFixture sample)
{
    private static readonly Dictionary<string, string> SaneArguments = new()
    {
        ["status"] = "{}",
        ["reindex"] = "{}",
        ["find_symbol"] = """{"query":"Cancel"}""",
        ["callers"] = """{"symbol":"OrderPurger.PurgeAsync","depth":3}""",
        ["callees"] = """{"symbol":"OrderService.CancelAsync(Guid, string)","include_external":true}""",
        ["find_usages"] = """{"symbol":"PermissionKeys.Orders.Delete"}""",
        ["trace_to_entrypoints"] = """{"symbol":"src/Shop.Infrastructure/Persistence/Repositories.cs:10"}""",
        ["routes"] = """{"auth":"none"}""",
        ["entrypoints"] = "{}",
        ["impact"] = "{}",
        ["architecture"] = "{}",
        ["type_hierarchy"] = """{"symbol":"INotifier"}""",
        ["di_registrations"] = """{"symbol":"IOrderService"}""",
    };

    private CodeGraph Graph => sample.Graph;

    [Fact]
    public async Task Every_registered_tool_answers_and_states_its_completion()
    {
        Assert.Equal(SaneArguments.Keys.Order(), ToolRegistry.All.Select(t => t.Name).Order());
        foreach (var tool in ToolRegistry.All)
        {
            var result = await Call(tool.Name, SaneArguments[tool.Name]);

            Assert.False(result.IsError, $"{tool.Name}: {result.Text}");
            Assert.Contains("analysis: complete", result.Text, StringComparison.Ordinal);
            using var schema = JsonDocument.Parse(tool.InputSchema());
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Theory]
    [InlineData("nope", "{}", "UNKNOWN_TOOL: 'nope' is not a tool. Tools: status,")]
    [InlineData("callers", "{}", "VALIDATION_FAILED: callers: 'symbol' is required")]
    [InlineData("callers", """{"symbol":"X","deep":2}""", "VALIDATION_FAILED: callers: unknown argument 'deep'. Allowed: symbol, depth, include_tests, limit.")]
    [InlineData("callers", """{"symbol":"X","depth":99}""", "VALIDATION_FAILED: callers: 'depth' must be an integer from 1 to 8.")]
    [InlineData("routes", """{"auth":"maybe"}""", "VALIDATION_FAILED: routes: 'auth' must be one of: any, none, anonymous, declared.")]
    [InlineData("callers", """{"symbol":"NoSuchThingAnywhere"}""", "SYMBOL_NOT_FOUND: nothing named 'NoSuchThingAnywhere'")]
    [InlineData("callers", """{"symbol":"HardDelete"}""", "SYMBOL_NOT_FOUND: nothing named 'HardDelete' is declared or referenced in the index. Nearest: ")]
    [InlineData("callers", """{"symbol":"Delete"}""", "AMBIGUOUS_SYMBOL: 'Delete' matches ")]
    [InlineData("callers", """{"symbol":"src/Nowhere.cs:3"}""", "SYMBOL_NOT_FOUND: no indexed file matches 'src/Nowhere.cs'")]
    public async Task Refusals_carry_a_code_and_a_remedy(string tool, string arguments, string expectedStart)
    {
        var result = await Call(tool, arguments);

        Assert.True(result.IsError);
        Assert.StartsWith(expectedStart, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ambiguous_name_lists_ids_that_then_resolve()
    {
        var refusal = await Call("callers", """{"symbol":"Delete"}""");
        var ids = refusal.Text.Split('\n').Where(l => l.Contains("id: ", StringComparison.Ordinal)).Select(l => l[(l.IndexOf("id: ", StringComparison.Ordinal) + 4)..]).ToList();

        Assert.True(ids.Count >= 4, refusal.Text);
        foreach (var id in ids)
        {
            Assert.Single(SymbolResolver.Resolve(Graph, id).Symbols);
        }
    }

    [Fact]
    public async Task Hidden_test_rows_are_counted_in_the_answer()
    {
        var hidden = await Call("callers", """{"symbol":"OrderRepository.HardDeleteAsync"}""");
        var shown = await Call("callers", """{"symbol":"OrderRepository.HardDeleteAsync","include_tests":true}""");

        Assert.Contains("1 callers in 1 types, 1 call sites. Hidden: 1 call sites in test projects", hidden.Text, StringComparison.Ordinal);
        Assert.Contains("2 callers in 2 types, 2 call sites.", shown.Text, StringComparison.Ordinal);
        Assert.Contains("[test]", shown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impact_of_a_repository_edit_is_the_endpoints_and_job_that_reach_it()
    {
        const string diff = """
            diff --git a/src/Shop.Infrastructure/Persistence/Repositories.cs b/src/Shop.Infrastructure/Persistence/Repositories.cs
            --- a/src/Shop.Infrastructure/Persistence/Repositories.cs
            +++ b/src/Shop.Infrastructure/Persistence/Repositories.cs
            @@ -10,0 +11 @@ public class OrderRepository
            +        // changed
            """;
        var result = await Call("impact", JsonSerializer.Serialize(new { diff }));

        Assert.False(result.IsError, result.Text);
        Assert.Contains("1 files, 1 changed symbols -> 3 endpoints and 1 other entry points affected", result.Text, StringComparison.Ordinal);
        Assert.Contains("DELETE  /api/Orders/{id:guid}", result.Text, StringComparison.Ordinal);
        Assert.Contains("POST    /api/v2/orders/{id:guid}/cancel", result.Text, StringComparison.Ordinal);
        Assert.Contains("[hosted-service] OrderPurgeService", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/products", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impact_of_a_planned_change_to_a_symbol_equals_its_trace()
    {
        var planned = await Call("impact", """{"symbol":"OrderRepository.HardDeleteAsync"}""");
        var refused = await Call("impact", """{"symbol":"OrderRepository.HardDeleteAsync","base":"main"}""");

        Assert.Contains("impact of a change to OrderRepository.HardDeleteAsync(Guid): 1 changed symbols -> 3 endpoints and 1 other entry points affected", planned.Text, StringComparison.Ordinal);
        Assert.Contains("[hosted-service] OrderPurgeService", planned.Text, StringComparison.Ordinal);
        Assert.StartsWith("VALIDATION_FAILED: impact: pass symbol, or a diff or base, not both.", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_inside_a_method_does_not_change_the_primary_constructor()
    {
        // OrdersController has a primary constructor; line 19 is inside Delete's body.
        const string diff = """
            --- a/src/Shop.Api/Controllers/OrdersController.cs
            +++ b/src/Shop.Api/Controllers/OrdersController.cs
            @@ -19 +19 @@
            -x
            +y
            """;
        var result = await Call("impact", JsonSerializer.Serialize(new { diff }));

        Assert.Contains("1 files, 1 changed symbols", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OrdersController.OrdersController(", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callees_include_constants_and_status_says_why_files_were_not_indexed()
    {
        var callees = await Call("callees", """{"symbol":"OrdersController.Delete"}""");
        var status = await Call("status", "{}");

        Assert.Contains("PermissionKeys.Orders.Delete", callees.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OrdersController.orders ", callees.Text, StringComparison.Ordinal);
        Assert.Contains("not indexed (3 generated (obj/bin), 1 excluded by **/Migrations/**)", status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impact_of_an_edit_outside_any_declaration_is_nothing()
    {
        const string diff = """
            --- a/src/Shop.Infrastructure/Persistence/Repositories.cs
            +++ b/src/Shop.Infrastructure/Persistence/Repositories.cs
            @@ -1 +1 @@
            -using Shop.Application.Abstractions;
            +using  Shop.Application.Abstractions;
            --- a/README.md
            +++ b/README.md
            @@ -1 +1 @@
            -a
            +b
            """;
        var result = await Call("impact", JsonSerializer.Serialize(new { diff }));

        Assert.Contains("2 files, 0 changed symbols -> 0 endpoints and 0 other entry points affected", result.Text, StringComparison.Ordinal);
        Assert.Contains("Not analysed (1 files", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_with_binding_errors_makes_every_answer_partial()
    {
        var broken = sample.Facts.Documents[0] with { Errors = 3 };
        var facts = sample.Facts with { Documents = [broken, .. sample.Facts.Documents.Skip(1)] };
        var host = new FixedHost(new CodeGraph(facts, 2, DateTimeOffset.UnixEpoch), SampleFixture.Config);

        var result = await ToolRegistry.CallAsync(host, "routes", default, TestContext.Current.CancellationToken);

        Assert.Contains("analysis: PARTIAL (results may be missing rows): 1 documents have binding errors (3 total): " + broken.Path, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Directly_bound_callers_are_usages_and_filtered_counts_add_up()
    {
        var examined = 0;
        foreach (var node in Graph.Nodes.Where(n => !n.IsExternal && n.Fact.Kind is SymKind.Method or SymKind.Property))
        {
            var usages = Graph.RowsTo(node.Id).Select(r => r.Index).ToHashSet();
            var edges = Graph.Callers(node.Id);
            foreach (var edge in edges.Where(e => e.Row.Fact.To == node.Id))
            {
                examined++;
                Assert.Contains(edge.Row.Index, usages);
            }

            var selection = new Selection([node], null, node.Fact.Display, null);
            var (shown, hidden) = Reach.Callers(Graph, selection, includeTests: false);
            Assert.Equal(edges.Count, shown.Count + hidden);
        }

        Assert.True(examined > 50, $"only {examined} directly bound calls were examined");
    }

    [Fact]
    public async Task Indexing_twice_gives_the_same_facts_and_they_survive_the_snapshot_format()
    {
        using var second = await RoslynIndexer.OpenAsync(SampleFixture.SolutionPath, SampleFixture.Config, null, TestContext.Current.CancellationToken);
        var again = await second.IndexAllAsync(null, TestContext.Current.CancellationToken);
        using var stream = new MemoryStream();
        FactsSerializer.Write(stream, sample.Facts);
        stream.Position = 0;
        var restored = FactsSerializer.Read(stream);

        Assert.Equal(FactsHash.Compute(sample.Facts), FactsHash.Compute(again));
        Assert.NotNull(restored);
        Assert.Equal(FactsHash.Compute(sample.Facts), FactsHash.Compute(restored));
        Assert.Equal(sample.Facts.Root, restored.Root);
        Assert.Null(FactsSerializer.Read(new MemoryStream(new byte[16])));
    }

    [Fact]
    public void Instructions_are_generated_from_configuration_and_capped()
    {
        var text = Instructions.Build(SampleFixture.Config, SampleFixture.SolutionPath);
        var huge = Instructions.Build(SampleFixture.Config with { Exclude = [.. Enumerable.Repeat("**/a-long-glob/**", 500)] }, SampleFixture.SolutionPath);

        Assert.Contains("SampleShop.slnx", text, StringComparison.Ordinal);
        Assert.Contains("Authorization attributes configured here: HasPermission.", text, StringComparison.Ordinal);
        Assert.Contains("Not indexed: **/Migrations/**.", text, StringComparison.Ordinal);
        Assert.Equal(Limits.InstructionsMaxChars, huge.Length);
    }

    private Task<ToolResult> Call(string tool, string arguments) =>
        ToolRegistry.CallAsync(new FixedHost(Graph, SampleFixture.Config), tool, JsonDocument.Parse(arguments).RootElement, TestContext.Current.CancellationToken);
}
