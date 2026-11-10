using Shop.Application.Abstractions;

namespace Shop.Infrastructure.Persistence;

/// <summary>A generic base shared by every repository: its methods must not look like one repository's hotspot.</summary>
public abstract class Repository<T> : IRepository<T>
    where T : class
{
    protected Dictionary<Guid, T> Store { get; } = [];

    public Task<T?> GetByIdAsync(Guid id) => Task.FromResult(Store.GetValueOrDefault(id));

    public virtual Task SaveAsync(T entity)
    {
        Store[KeyOf(entity)] = entity;
        return Task.CompletedTask;
    }

    protected abstract Guid KeyOf(T entity);
}
