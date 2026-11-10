using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;
using Xunit;

namespace CodeReach.Tests;

/// <summary>One named test per trap the sample solution carries.</summary>
[Collection(SampleCollection.Name)]
public sealed class CallGraphTests(SampleFixture sample)
{
    private CodeGraph Graph => sample.Graph;

    [Fact]
    public void Call_through_two_interfaces_reaches_the_repository_implementation()
    {
        Assert.Equal(["OrderPurger.PurgeAsync(Guid)"], Callers("OrderRepository.HardDeleteAsync"));
        Assert.Equal(
            ["OrderPurgeService.PurgeExpiredAsync()", "OrderService.CancelAsync(Guid, string)", "OrdersController.Delete(Guid)"],
            Callers("OrderPurger.PurgeAsync"));
    }

    [Fact]
    public void Test_project_callers_are_hidden_by_default_and_counted()
    {
        var selection = SymbolResolver.Resolve(Graph, "OrderRepository.HardDeleteAsync");
        var (shown, hidden) = Reach.Callers(Graph, selection, includeTests: false);
        var (all, noneHidden) = Reach.Callers(Graph, selection, includeTests: true);

        Assert.Single(shown);
        Assert.Equal(1, hidden);
        Assert.Equal(0, noneHidden);
        Assert.Equal(shown.Count + hidden, all.Count);
        Assert.Contains(all, e => Graph.Node(e.Row.Fact.From).Fact.Display == "OrderServiceTests.Hard_delete_removes_the_order()");
    }

    [Fact]
    public void A_production_class_named_like_a_test_is_not_a_test()
    {
        var node = SymbolResolver.Resolve(Graph, "SelfTests.RunAsync").Symbols.Single();

        Assert.False(node.IsTest);
        Assert.True(Graph.Projects["Shop.Tests"].IsTest);
        Assert.StartsWith("references xunit", Graph.Projects["Shop.Tests"].TestEvidence, StringComparison.Ordinal);
        Assert.Contains("SelfTests.RunAsync()", Callers("OrderService.CancelAsync(Guid)"));
    }

    [Fact]
    public void Excluded_folder_contributes_nothing()
    {
        Assert.DoesNotContain(Graph.Facts.Documents, d => d.Path.Contains("Migrations", StringComparison.Ordinal));
        Assert.DoesNotContain(Graph.Nodes, n => n.Fact.Display.Contains("M0001", StringComparison.Ordinal));
        Assert.Equal(1, Graph.Projects["Shop.Infrastructure"].Skipped - SkippedGenerated("Shop.Infrastructure"));
    }

    [Fact]
    public void Identically_named_constants_bind_to_their_own_class()
    {
        Assert.Equal(["OrdersController.Delete(Guid)"], Users("PermissionKeys.Orders.Delete"));
        Assert.Equal(["ProductsController.Delete(Guid)"], Users("PermissionKeys.Products.Delete"));
    }

    [Fact]
    public void Comments_and_strings_are_not_usages_and_nameof_is_labelled()
    {
        var rows = Graph.RowsTo(SymbolResolver.Resolve(Graph, "IOrderRepository.HardDeleteAsync").Symbols.Single().Id).Where(r => !r.IsTest).ToList();

        Assert.Equal([RefKind.Invoke, RefKind.NameOf], rows.Select(r => r.Fact.Kind).Order());
        Assert.DoesNotContain(Callers("IOrderRepository.HardDeleteAsync"), c => c.StartsWith("OrderService.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shared_generic_base_method_is_narrowed_to_the_repository_asked_about()
    {
        Assert.Equal(
            ["ProductService.DeleteAsync(Guid)", "ProductService.GetAsync(Guid)", "ProductService.PriceAsync(Guid)"],
            Callers("ProductRepository.GetByIdAsync"));
        Assert.Equal(["OrderService.CancelAsync(Guid, string)", "OrderService.GetAsync(Guid)"], Callers("OrderRepository.GetByIdAsync"));
        Assert.Equal(5, Callers("Repository.GetByIdAsync").Count);
    }

    [Fact]
    public void Receiver_type_picks_the_override_of_the_right_repository()
    {
        var targets = Callees("OrderService.CancelAsync(Guid, string)");

        Assert.Contains("OrderRepository.SaveAsync(Order)", targets);
        Assert.DoesNotContain("Repository<T>.SaveAsync(T)", targets);
    }

    [Fact]
    public void Virtual_and_abstract_calls_expand_to_overrides_and_base_calls_do_not()
    {
        var send = Callees("NotifierBase.SendAsync");

        Assert.Contains("NotifierBase.Format(string)", send);
        Assert.Contains("EmailNotifier.Format(string)", send);
        Assert.Contains("EmailNotifier.DeliverAsync(string)", send);
        Assert.Contains("SmsNotifier.DeliverAsync(string)", send);
        Assert.DoesNotContain("NotifierBase.DeliverAsync(string)", send);
        Assert.Equal(["DiscountedPricingCalculator.PriceOf(Product)", "ProductService.PriceAsync(Guid)"], Callers("PricingCalculator.PriceOf"));
        Assert.Equal(["ProductService.PriceAsync(Guid)"], Callers("DiscountedPricingCalculator.PriceOf"));
    }

    [Fact]
    public void One_interface_with_two_implementations_reports_both_and_says_so()
    {
        var edges = Graph.Callees(SymbolResolver.Resolve(Graph, "NotifierBase.SendAsync").Symbols.Single().Id);
        var deliver = edges.Where(e => Graph.Node(e.Row.Fact.To).Fact.Name == "DeliverAsync").ToList();

        Assert.Equal(2, deliver.Count);
        Assert.All(deliver, e => Assert.Equal(2, Graph.Reachable(e.Row).Count));
    }

    [Fact]
    public void Operators_method_groups_constructors_and_positional_properties_are_bound()
    {
        Assert.Equal(["OrderExtensions.TotalOf(IEnumerable<Order>)"], Users("Money.op_Addition"));
        Assert.Contains(Graph.RowsTo(Id("DeliveryLog.OnSent")), r => r.Fact.Kind == RefKind.MethodGroup);
        Assert.Contains(Graph.RowsTo(Id("OrderPurger.#ctor")), r => r.Fact.Kind == RefKind.Construct && r.IsTest);
        Assert.Contains(Graph.RowsTo(Id("Money.Amount")), r => r.Fact.Kind == RefKind.Read);
        Assert.Contains(Graph.RowsTo(Id("Money.#ctor")), r => r.Fact.Kind == RefKind.Construct);
        Assert.Contains(Graph.Nodes, n => n.Fact.QName.EndsWith("DeliveryLog.System.IDisposable.Dispose", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_source_symbol_id_names_its_assembly()
    {
        var declared = Graph.Nodes.Where(n => !n.IsExternal).ToList();

        Assert.True(declared.Count > 100, "the sample should declare more than 100 symbols");
        Assert.All(declared, n => Assert.NotNull(Ids.AssemblyOf(n.Id)));
        Assert.Equal("T:[Shop.Application]Shop.Application.Orders", Ids.ContainingTypeOf(Id("OrderService.GetAsync"))![..43]);
    }

    private static int SkippedGenerated(string project) => project == "Shop.Infrastructure" ? 3 : 0;

    private string Id(string query) => SymbolResolver.Resolve(Graph, query).Symbols[0].Id;

    private List<string> Callers(string query)
    {
        var (shown, _) = Reach.Callers(Graph, SymbolResolver.Resolve(Graph, query), includeTests: false);
        return shown.Select(e => Graph.Node(e.Row.Fact.From).Fact.Display).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private List<string> Users(string query) =>
        SymbolResolver.Resolve(Graph, query).Symbols.SelectMany(s => Graph.RowsTo(s.Id)).Where(r => !r.IsTest)
            .Select(r => Graph.Node(r.Fact.From).Fact.Display).Distinct().Order(StringComparer.Ordinal).ToList();

    private List<string> Callees(string query) =>
        SymbolResolver.Resolve(Graph, query).Symbols.SelectMany(s => Graph.Callees(s.Id))
            .Select(e => Graph.Node(e.Target).Fact.Display).Distinct().Order(StringComparer.Ordinal).ToList();
}
