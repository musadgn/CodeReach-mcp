using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shop.Api.Security;
using Shop.Application.Orders;
using Shop.Domain;

namespace Shop.Api.Controllers;

public class OrdersController(IOrderService orders, IOrderPurger purger) : ShopControllerBase
{
    [HttpGet("{id:guid}")]
    [HasPermission(PermissionKeys.Orders.View)]
    public async Task<ActionResult<Order>> Get(Guid id) => await orders.GetAsync(id) is { } order ? order : NotFound();

    [HttpDelete("{id:guid}")]
    [HasPermission(PermissionKeys.Orders.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await purger.PurgeAsync(id);
        return NoContent();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid id, [FromQuery] string reason = "customer")
    {
        await orders.CancelAsync(id, reason);
        return Accepted();
    }

    [HttpGet("~/api/public/orders/{id:guid}/status")]
    [AllowAnonymous]
    public async Task<ActionResult<OrderStatus>> Status(Guid id) => (await orders.GetAsync(id))?.Status ?? OrderStatus.Open;
}
