using Shop.Application.Abstractions;
using Shop.Domain;

namespace Shop.Infrastructure.Persistence;

public class OrderRepository : Repository<Order>, IOrderRepository
{
    public Task HardDeleteAsync(Guid id)
    {
        Store.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Order>> ListExpiredAsync()
    {
        IReadOnlyList<Order> expired = Store.Values.Where(o => o.ExpiresAt < DateTimeOffset.UtcNow).ToList();
        return Task.FromResult(expired);
    }

    public override Task SaveAsync(Order entity)
    {
        entity.ExpiresAt ??= DateTimeOffset.UtcNow.AddDays(30);
        return base.SaveAsync(entity);
    }

    protected override Guid KeyOf(Order entity) => entity.Id;
}

public class ProductRepository : Repository<Product>, IProductRepository
{
    protected override Guid KeyOf(Product entity) => entity.Id;
}
