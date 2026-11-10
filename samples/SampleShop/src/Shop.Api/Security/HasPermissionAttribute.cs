namespace Shop.Api.Security;

/// <summary>A custom permission attribute that implements no framework authorization contract.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class HasPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}
