namespace Shop.Domain;

/// <summary>Two nested classes with identically named members: a call must bind to the right one.</summary>
public static class PermissionKeys
{
    public static class Orders
    {
        public const string View = "orders.view";
        public const string Delete = "orders.delete";
    }

    public static class Products
    {
        public const string View = "products.view";
        public const string Delete = "products.delete";
    }
}
