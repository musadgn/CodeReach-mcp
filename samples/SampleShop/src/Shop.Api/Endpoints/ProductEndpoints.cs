using Shop.Application.Products;

namespace Shop.Api.Endpoints;

public static class ProductEndpoints
{
    /// <summary>The prefix of these routes arrives through the <paramref name="routes"/> parameter.</summary>
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/products");
        group.MapGet("/{id:guid}", async (Guid id, IProductService products) => await products.GetAsync(id));
        group.MapDelete("/{id:guid}", async (Guid id, IProductService products) => await products.DeleteAsync(id))
            .RequireAuthorization("Admin");
        return routes;
    }
}
