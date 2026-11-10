using CodeReach.Core.Graph;
using CodeReach.Core.Queries;
using Xunit;

namespace CodeReach.Tests;

[Collection(SampleCollection.Name)]
public sealed class RouteTests(SampleFixture sample)
{
    /// <summary>The complete route table of the sample, audited by hand against its source. No more, no fewer.</summary>
    private static readonly string[] Expected =
    [
        "DELETE /api/Orders/{id:guid} -> OrdersController.Delete(Guid) | HasPermission(PermissionKeys.Orders.Delete = \"orders.delete\") [action]; Authorize [base ShopControllerBase]",
        "DELETE /api/catalog/{id:guid} -> ProductsController.Delete(Guid) | HasPermission(PermissionKeys.Products.Delete = \"products.delete\") [action]",
        "DELETE /api/products/{id:guid} -> ProductsController.Delete(Guid) | HasPermission(PermissionKeys.Products.Delete = \"products.delete\") [action]",
        "DELETE /api/v2/products/{id:guid} -> DELETE /{id:guid} handler | RequireAuthorization(\"ApiUser\") [group]; RequireAuthorization(\"Admin\") [endpoint]",
        "GET /api/Orders/ping -> ShopControllerBase.Ping() | Authorize [base ShopControllerBase]",
        "GET /api/Orders/{id:guid} -> OrdersController.Get(Guid) | HasPermission(PermissionKeys.Orders.View = \"orders.view\") [action]; Authorize [base ShopControllerBase]",
        "GET /api/catalog/{id:guid} -> ProductsController.Get(Guid) | none declared",
        "GET /api/catalog/{id:guid}/price -> ProductsController.Price(Guid) | none declared",
        "GET /api/products/{id:guid} -> ProductsController.Get(Guid) | none declared",
        "GET /api/products/{id:guid}/price -> ProductsController.Price(Guid) | none declared",
        "GET /api/public/orders/{id:guid}/status -> OrdersController.Status(Guid) | anonymous allowed: AllowAnonymous [action]; Authorize [base ShopControllerBase]",
        "GET /api/v2/products/{id:guid} -> GET /{id:guid} handler | RequireAuthorization(\"ApiUser\") [group]",
        "GET /health -> GET /health handler | anonymous allowed: AllowAnonymous [endpoint]",
        "HEAD /api/catalog/{id:guid}/price -> ProductsController.Price(Guid) | none declared",
        "HEAD /api/products/{id:guid}/price -> ProductsController.Price(Guid) | none declared",
        "POST /api/Orders/{id:guid}/cancel -> OrdersController.CancelAsync(Guid, string) | Authorize [base ShopControllerBase]",
        "POST /api/v2/orders/{id:guid}/cancel -> POST /{id:guid}/cancel handler | RequireAuthorization(\"ApiUser\") [group]",
        "POST /webhooks/payment -> PaymentWebhook.Handle(Guid, IOrderService) | none declared",
    ];

    private CodeGraph Graph => sample.Graph;

    [Fact]
    public void Route_table_is_exactly_the_audited_list()
    {
        var actual = Graph.Endpoints
            .Select(e => $"{e.Verb} {e.Template} -> {Graph.Node(e.Handler).Fact.Display} | {Core.Tools.Render.Auth(e)}")
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(Expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Non_actions_and_framework_members_are_not_endpoints()
    {
        Assert.DoesNotContain(Graph.Endpoints, e => Graph.Node(e.Handler).Fact.Name is "Describe" or "NotFound" or "Accepted");
    }

    [Fact]
    public void Every_endpoint_handler_is_an_entry_point_and_hosted_services_are_too()
    {
        Assert.All(Graph.Endpoints, e => Assert.True(Graph.IsEntryPoint(e.Handler)));
        var kinds = Graph.EntryPoints.Values.SelectMany(l => l).Select(e => $"{e.Kind}:{e.Label}").ToList();
        Assert.Contains("hosted-service:OrderPurgeService", kinds);
        Assert.Contains("main:Shop.Api", kinds);
    }

    [Fact]
    public void Trace_from_the_repository_finds_exactly_the_expected_roots()
    {
        var trace = Reach.Trace(Graph, SymbolResolver.Resolve(Graph, "OrderRepository.HardDeleteAsync"), includeTests: false, maxDepth: 30);
        var roots = trace.Rooted.Select(p => Graph.EntryPoints[p[0].Symbol][0]).Select(e => $"{e.Kind}:{e.Label}").Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "endpoint:DELETE /api/Orders/{id:guid}", "endpoint:POST /api/Orders/{id:guid}/cancel",
                "endpoint:POST /api/v2/orders/{id:guid}/cancel", "hosted-service:OrderPurgeService",
            ],
            roots);
        Assert.Equal(["SelfTests.RunAsync()"], trace.Unrooted.Select(p => Graph.Node(p[0].Symbol).Fact.Display));
        Assert.False(trace.Truncated);
        Assert.All(trace.Rooted, path => Assert.Equal("OrderRepository.HardDeleteAsync(Guid)", Graph.Node(path[^1].Symbol).Fact.Display));
    }

    [Fact]
    public void Trace_paths_are_made_of_real_caller_edges()
    {
        var examined = 0;
        foreach (var method in Graph.Nodes.Where(n => !n.IsExternal && n.Fact.Kind == Core.Facts.SymKind.Method))
        {
            var trace = Reach.Trace(Graph, new Selection([method], null, method.Fact.Display, null), includeTests: true, maxDepth: 30);
            foreach (var path in trace.Rooted.Concat(trace.Unrooted))
            {
                for (var i = 0; i < path.Count - 1; i++)
                {
                    examined++;
                    Assert.Contains(Graph.Callers(path[i + 1].Symbol), e => e.Row.Fact.From == path[i].Symbol && e.Row.Index == path[i].Calls!.Value.Row.Index);
                }
            }
        }

        Assert.True(examined > 100, $"only {examined} trace hops were examined");
    }
}
