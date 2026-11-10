using Shop.Application.Abstractions;
using Shop.Application.Pricing;
using Shop.Domain;

namespace Shop.Application.Products;

public interface IProductService
{
    Task<Product?> GetAsync(Guid id);

    Task<Money> PriceAsync(Guid id);

    Task DeleteAsync(Guid id);
}

public class ProductService(IProductRepository products, PricingCalculator pricing) : IProductService
{
    public Task<Product?> GetAsync(Guid id) => products.GetByIdAsync(id);

    public async Task<Money> PriceAsync(Guid id)
    {
        var product = await products.GetByIdAsync(id) ?? throw new InvalidOperationException("No such product.");
        return pricing.PriceOf(product);
    }

    public async Task DeleteAsync(Guid id)
    {
        var product = await products.GetByIdAsync(id);
        if (product is not null)
        {
            product.Name = "(deleted)";
            await products.SaveAsync(product);
        }
    }
}
