namespace Shop.Domain;

public enum OrderStatus { Open, Cancelled }

public class Order
{
    public Guid Id { get; init; }

    public Money Total { get; set; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public void Cancel() => Status = OrderStatus.Cancelled;
}

public class Product
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public Money Price { get; set; }
}
