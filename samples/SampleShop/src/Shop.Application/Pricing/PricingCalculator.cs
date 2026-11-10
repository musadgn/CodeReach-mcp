using Shop.Domain;

namespace Shop.Application.Pricing;

public class PricingCalculator
{
    public virtual Money PriceOf(Product product) => product.Price;
}

public class DiscountedPricingCalculator : PricingCalculator
{
    public decimal Factor { get; set; } = 0.9m;

    public override Money PriceOf(Product product) => base.PriceOf(product).Scale(Factor);
}
