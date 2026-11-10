using Shop.Domain;

namespace Shop.Application.Orders;

public interface IOrderService
{
    Task<Order?> GetAsync(Guid id);

    Task CancelAsync(Guid id);

    Task CancelAsync(Guid id, string reason);
}

public interface IOrderPurger
{
    Task PurgeAsync(Guid id);
}
