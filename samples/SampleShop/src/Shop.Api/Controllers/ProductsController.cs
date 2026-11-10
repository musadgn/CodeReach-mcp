using Microsoft.AspNetCore.Mvc;
using Shop.Api.Security;
using Shop.Application.Products;
using Shop.Domain;

namespace Shop.Api.Controllers;

/// <summary>Two routes, and no authorization on the controller: the read action is open.</summary>
[ApiController]
[Route("api/products")]
[Route("api/catalog")]
public class ProductsController(IProductService products) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Product>> Get(Guid id) => await products.GetAsync(id) is { } product ? product : NotFound();

    [HttpDelete("{id:guid}")]
    [HasPermission(PermissionKeys.Products.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await products.DeleteAsync(id);
        return NoContent();
    }

    [AcceptVerbs("GET", "HEAD", Route = "{id:guid}/price")]
    public async Task<Money> Price(Guid id) => await products.PriceAsync(id);

    [NonAction]
    public string Describe() => nameof(ProductsController);
}
