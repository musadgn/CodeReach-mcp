using Shop.Application.Notifications;
using Shop.Application.Orders;
using Shop.Domain;
using Shop.Infrastructure.Persistence;
using Xunit;

namespace Shop.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task Cancel_marks_the_order()
    {
        var repository = new OrderRepository();
        var order = new Order { Id = Guid.NewGuid() };
        await repository.SaveAsync(order);
        var service = new OrderService(repository, new OrderPurger(repository), new SmsNotifier());

        await service.CancelAsync(order.Id);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task Hard_delete_removes_the_order()
    {
        var repository = new OrderRepository();
        var order = new Order { Id = Guid.NewGuid() };
        await repository.SaveAsync(order);

        await repository.HardDeleteAsync(order.Id);

        Assert.Null(await repository.GetByIdAsync(order.Id));
    }
}
