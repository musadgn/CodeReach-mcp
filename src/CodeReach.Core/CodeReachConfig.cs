using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeReach.Core;

/// <summary>
/// A method that counts as an entry point. Give <see cref="Method"/> together with <see cref="Implements"/>
/// or <see cref="BaseType"/>, or give <see cref="Attribute"/> alone. Type names are full metadata names.
/// </summary>
public sealed record RootRule(string Kind, string? Implements = null, string? BaseType = null, string? Attribute = null, string? Method = null);

/// <summary>User configuration (<c>codereach.json</c>). The tool reads it and never writes it.</summary>
public sealed record CodeReachConfig
{
    public const string FileName = "codereach.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static readonly IReadOnlyList<RootRule> BuiltInRoots =
    [
        new("mediatr-handler", Implements: "MediatR.IRequestHandler`2", Method: "Handle"),
        new("mediatr-handler", Implements: "MediatR.IRequestHandler`1", Method: "Handle"),
        new("mediatr-notification", Implements: "MediatR.INotificationHandler`1", Method: "Handle"),
        new("masstransit-consumer", Implements: "MassTransit.IConsumer`1", Method: "Consume"),
        new("quartz-job", Implements: "Quartz.IJob", Method: "Execute"),
        new("middleware", Implements: "Microsoft.AspNetCore.Http.IMiddleware", Method: "InvokeAsync"),
        new("azure-function", Attribute: "Microsoft.Azure.Functions.Worker.FunctionAttribute"),
        new("signalr-hub", BaseType: "Microsoft.AspNetCore.SignalR.Hub", Method: "*"),
        new("grpc-service", Attribute: "System.ServiceModel.OperationContractAttribute"),
    ];

    public string? Solution { get; init; }

    /// <summary>Globs, relative to the solution directory, of source files that are not indexed.</summary>
    public string[] Exclude { get; init; } = [];

    /// <summary>Project names treated as test projects in addition to the detected ones.</summary>
    public string[] TestProjects { get; init; } = [];

    /// <summary>Attribute names (with or without the <c>Attribute</c> suffix, simple or full) that mean authorization.</summary>
    public string[] AuthAttributes { get; init; } = [];

    /// <summary>Names of fluent endpoint methods that mean authorization, besides <c>RequireAuthorization</c>.</summary>
    public string[] AuthEndpointMethods { get; init; } = [];

    public RootRule[] EntryPoints { get; init; } = [];

    public bool BuiltInEntryPoints { get; init; } = true;

    [JsonIgnore]
    public IReadOnlyList<RootRule> Roots => BuiltInEntryPoints ? [.. BuiltInRoots, .. EntryPoints] : EntryPoints;

    public static CodeReachConfig Parse(string json) =>
        JsonSerializer.Deserialize<CodeReachConfig>(json, Json) ?? new CodeReachConfig();

    /// <summary>A stable hash of everything that changes what the indexer produces.</summary>
    public string Hash()
    {
        var canonical = JsonSerializer.Serialize(this with { Solution = null }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}
