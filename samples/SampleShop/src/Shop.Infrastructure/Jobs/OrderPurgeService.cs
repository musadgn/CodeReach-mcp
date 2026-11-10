using Microsoft.Extensions.Hosting;
using Shop.Application.Abstractions;
using Shop.Application.Orders;

namespace Shop.Infrastructure.Jobs;

public class OrderPurgeService(IOrderRepository orders, IOrderPurger purger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeExpiredAsync();
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task PurgeExpiredAsync()
    {
        foreach (var order in await orders.ListExpiredAsync())
        {
            await purger.PurgeAsync(order.Id);
        }
    }
}
