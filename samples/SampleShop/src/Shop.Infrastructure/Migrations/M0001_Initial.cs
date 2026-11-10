using Shop.Infrastructure.Persistence;

namespace Shop.Infrastructure.Migrations;

/// <summary>Generated-style noise. The sample configuration excludes this folder from the index.</summary>
public static class M0001_Initial
{
    public static async Task UpAsync(OrderRepository orders)
    {
        await orders.HardDeleteAsync(Guid.Empty);
        await orders.HardDeleteAsync(Guid.Empty);
        await orders.HardDeleteAsync(Guid.Empty);
    }
}
