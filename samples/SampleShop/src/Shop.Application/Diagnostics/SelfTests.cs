using Shop.Application.Orders;

namespace Shop.Application.Diagnostics;

/// <summary>Production code whose name ends in "Tests". It is not a test and must never be filtered as one.</summary>
public class SelfTests(IOrderService orders)
{
    public Task RunAsync() => orders.CancelAsync(Guid.Empty);
}
