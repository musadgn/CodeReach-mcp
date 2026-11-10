using Shop.Application.Abstractions;
using Shop.Application.Notifications;
using Shop.Domain;

namespace Shop.Application.Orders;

public class OrderService : IOrderService
{
    public const string FraudReason = "fraud";

    private readonly IOrderRepository _orders;
    private readonly IOrderPurger _purger;
    private readonly INotifier _notifier;

    public OrderService(IOrderRepository orders, IOrderPurger purger, INotifier notifier)
    {
        _orders = orders;
        _purger = purger;
        _notifier = notifier;
    }

    public Task<Order?> GetAsync(Guid id) => _orders.GetByIdAsync(id);

    public Task CancelAsync(Guid id) => CancelAsync(id, "customer");

    public async Task CancelAsync(Guid id, string reason)
    {
        var order = await _orders.GetByIdAsync(id) ?? throw new InvalidOperationException("No such order.");
        order.Cancel();
        await _orders.SaveAsync(order);
        if (reason == FraudReason)
        {
            // Fraudulent orders are removed for good. The text HardDeleteAsync here is only a comment.
            await _purger.PurgeAsync(id);
        }

        await _notifier.SendAsync($"Order {id} cancelled ({nameof(IOrderRepository.HardDeleteAsync)} not called here).");
    }
}

public class OrderPurger : IOrderPurger
{
    private readonly IOrderRepository _orders;

    public OrderPurger(IOrderRepository orders) => _orders = orders;

    public Task PurgeAsync(Guid id) => _orders.HardDeleteAsync(id);
}
