using Shop.Domain;

namespace Shop.Application.Abstractions;

public interface IRepository<T>
    where T : class
{
    Task<T?> GetByIdAsync(Guid id);

    Task SaveAsync(T entity);
}

public interface IOrderRepository : IRepository<Order>
{
    Task HardDeleteAsync(Guid id);

    Task<IReadOnlyList<Order>> ListExpiredAsync();
}

public interface IProductRepository : IRepository<Product>
{
}
