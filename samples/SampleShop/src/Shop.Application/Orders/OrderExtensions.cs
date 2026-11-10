using Shop.Domain;

namespace Shop.Application.Orders;

public static class OrderExtensions
{
    public static bool IsLarge(this Order order) => order.Total.Amount > 1000m;

    public static Money TotalOf(this IEnumerable<Order> orders)
    {
        var sum = new Money(0);
        foreach (var order in orders)
        {
            sum = sum + order.Total;
        }

        return sum;
    }
}
