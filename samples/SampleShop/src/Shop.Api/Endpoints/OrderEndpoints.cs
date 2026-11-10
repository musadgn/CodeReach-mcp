using Shop.Application.Orders;

namespace Shop.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/cancel", (Guid id, IOrderService orders) => orders.CancelAsync(id));
    }
}

public static class PaymentWebhook
{
    public static async Task<IResult> Handle(Guid orderId, IOrderService orders) =>
        await orders.GetAsync(orderId) is null ? Results.NotFound() : Results.Ok();
}
