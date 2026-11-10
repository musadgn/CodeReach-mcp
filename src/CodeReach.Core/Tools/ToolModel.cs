using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

public enum ParamType { String, Integer, Boolean }

/// <summary>One tool input. The schema a client sees and the validation a call gets are both made from this.</summary>
public sealed record Param(
    string Name, ParamType Type, string Description, bool Required = false, int Default = 0, int Min = 0, int Max = 0,
    string[]? Options = null, int MaxLength = Limits.MaxSymbolLength);

/// <summary>
/// <see cref="Finding"/> marks a complete answer that reports something wrong; the CLI exits 1 on it.
/// <see cref="Data"/> is the same answer as structured JSON, for screens; text-only tools leave it null.
/// </summary>
public sealed record ToolResult(string Text, bool IsError = false, bool Finding = false, JsonObject? Data = null);

/// <summary>The graph a query runs on, with a notice when it is older than the files on disk.</summary>
public sealed record GraphLease(CodeGraph Graph, string? Notice);

/// <summary>What tools need from the process that owns the index. Implemented by the host, faked in tests.</summary>
public interface IIndexHost
{
    CodeReachConfig Config { get; }

    Task<GraphLease> AcquireAsync(CancellationToken cancel);

    Task<string> ReindexAsync(bool full, CancellationToken cancel);

    string DescribeState();

    /// <summary>Unified diff with zero context lines of the working tree against <paramref name="baseRef"/> (HEAD when null).</summary>
    Task<string> GitDiffAsync(string? baseRef, CancellationToken cancel);
}

public sealed record ToolDef(
    string Name, string Description, IReadOnlyList<Param> Params,
    Func<IIndexHost, Args, CancellationToken, Task<ToolResult>> Run)
{
    public string InputSchema()
    {
        var properties = new JsonObject();
        foreach (var param in Params)
        {
            var schema = new JsonObject
            {
                ["type"] = param.Type switch { ParamType.Integer => "integer", ParamType.Boolean => "boolean", _ => "string" },
                ["description"] = param.Description,
            };
            if (param.Type == ParamType.Integer)
            {
                schema["minimum"] = param.Min;
                schema["maximum"] = param.Max;
                schema["default"] = param.Default;
            }

            if (param.Type == ParamType.String && !param.Required && param.Options is null)
            {
                schema["maxLength"] = param.MaxLength;
            }

            if (param.Options is not null)
            {
                schema["enum"] = new JsonArray([.. param.Options.Select(o => (JsonNode)o)]);
            }

            properties[param.Name] = schema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. Params.Where(p => p.Required).Select(p => (JsonNode)p.Name)]),
            ["additionalProperties"] = false,
        }.ToJsonString();
    }
}

/// <summary>Validated tool input. Construction refuses unknown names, wrong types and out-of-range values.</summary>
public sealed class Args
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private readonly ToolDef _tool;

    public Args(ToolDef tool, JsonElement input)
    {
        _tool = tool;
        if (input.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw Invalid("arguments must be a JSON object.");
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject())
            {
                var param = tool.Params.FirstOrDefault(p => p.Name == property.Name)
                    ?? throw Invalid($"unknown argument '{property.Name}'. Allowed: {string.Join(", ", tool.Params.Select(p => p.Name))}.");
                if (property.Value.ValueKind != JsonValueKind.Null)
                {
                    Check(param, property.Value);
                    _values[param.Name] = property.Value;
                }
            }
        }

        foreach (var missing in tool.Params.Where(p => p.Required && !_values.ContainsKey(p.Name)))
        {
            throw Invalid($"'{missing.Name}' is required: {missing.Description}");
        }
    }

    public string? Text(string name) => _values.TryGetValue(name, out var value) ? value.GetString() : null;

    public string Required(string name) => Text(name)!;

    public int Number(string name) => _values.TryGetValue(name, out var value) ? value.GetInt32() : _tool.Params.First(p => p.Name == name).Default;

    public bool Flag(string name) => _values.TryGetValue(name, out var value) && value.GetBoolean();

    private void Check(Param param, JsonElement value)
    {
        switch (param.Type)
        {
            case ParamType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                throw Invalid($"'{param.Name}' must be true or false.");
            case ParamType.Integer when !value.TryGetInt32(out var number) || number < param.Min || number > param.Max:
                throw Invalid($"'{param.Name}' must be an integer from {param.Min} to {param.Max}.");
            case ParamType.String when value.ValueKind != JsonValueKind.String:
                throw Invalid($"'{param.Name}' must be a string.");
            case ParamType.String when value.GetString()!.Length > param.MaxLength:
                throw Invalid($"'{param.Name}' is longer than {param.MaxLength} characters.");
            case ParamType.String when param.Options is not null && !param.Options.Contains(value.GetString()):
                throw Invalid($"'{param.Name}' must be one of: {string.Join(", ", param.Options)}.");
        }
    }

    private ToolRefusal Invalid(string message) => new(Codes.ValidationFailed, $"{_tool.Name}: {message}");
}

/// <summary>Builds an answer and stamps it with the index it came from and whether that index is complete.</summary>
internal sealed class Report
{
    private readonly StringBuilder _text = new();
    private JsonObject? _data;

    public Report Line(string line = "")
    {
        _text.Append(line).Append('\n');
        return this;
    }

    public Report Data(JsonObject data)
    {
        _data = data;
        return this;
    }

    public ToolResult Finish(GraphLease lease, bool finding = false)
    {
        var graph = lease.Graph;
        var completion = graph.Completion.IsComplete
            ? "complete"
            : "PARTIAL (results may be missing rows): " + string.Join("; ", graph.Completion.Problems);
        var head = lease.Notice is null ? string.Empty : lease.Notice + "\n\n";
        return new ToolResult(
            $"{head}{_text.ToString().TrimEnd()}\n\n-- index generation {graph.Generation}, built {graph.BuiltAt:yyyy-MM-dd HH:mm:ss}Z; analysis: {completion}",
            false, finding, Stamp(_data, graph, lease, completion));
    }

    private static JsonObject? Stamp(JsonObject? data, CodeGraph graph, GraphLease lease, string completion)
    {
        if (data is null)
        {
            return null;
        }

        data["index"] = new JsonObject
        {
            ["generation"] = graph.Generation,
            ["builtAt"] = graph.BuiltAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["complete"] = graph.Completion.IsComplete,
            ["completion"] = completion,
            ["notice"] = lease.Notice,
        };
        return data;
    }
}
