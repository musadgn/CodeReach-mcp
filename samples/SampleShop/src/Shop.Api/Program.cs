using Shop.Api.Endpoints;
using Shop.Application.Abstractions;
using Shop.Application.Notifications;
using Shop.Application.Orders;
using Shop.Application.Pricing;
using Shop.Application.Products;
using Shop.Infrastructure.Jobs;
using Shop.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IOrderRepository, OrderRepository>();
builder.Services.AddSingleton<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderPurger, OrderPurger>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<INotifier, EmailNotifier>();
builder.Services.AddSingleton<PricingCalculator, DiscountedPricingCalculator>();
builder.Services.AddHostedService<OrderPurgeService>().AddShopInfrastructure();

var app = builder.Build();
app.MapControllers();
app.MapGet("/health", () => "ok").AllowAnonymous();
app.MapPost("/webhooks/payment", PaymentWebhook.Handle);

var api = app.MapGroup("/api/v2").RequireAuthorization("ApiUser");
api.MapProductEndpoints();
api.MapGroup("/orders").MapOrderEndpoints();

app.Run();
