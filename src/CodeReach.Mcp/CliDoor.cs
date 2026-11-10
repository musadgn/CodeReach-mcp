using System.Text.Json;
using CodeReach.Core.Tools;

namespace CodeReach.Mcp;

/// <summary>The shell door: <c>codereach call &lt;tool&gt; '{json}'</c>. Exit 0 clean, 1 finding, 2 could not run.</summary>
internal static class CliDoor
{
    public const int Clean = 0;
    public const int Finding = 1;
    public const int CouldNotRun = 2;

    public static string ListTools() =>
        string.Join("\n\n", ToolRegistry.All.Select(t => $"{t.Name}\n  {t.Description}\n  arguments: {t.InputSchema()}"));

    public static async Task<int> CallAsync(IIndexHost host, string tool, string? json, CancellationToken cancel)
    {
        JsonElement input;
        try
        {
            input = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement;
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync($"VALIDATION_FAILED: the arguments are not valid JSON: {exception.Message}").ConfigureAwait(false);
            return CouldNotRun;
        }

        var result = await ToolRegistry.CallAsync(host, tool, input, cancel).ConfigureAwait(false);
        await (result.IsError ? Console.Error : Console.Out).WriteLineAsync(result.Text).ConfigureAwait(false);
        return result.IsError ? CouldNotRun : result.Finding ? Finding : Clean;
    }
}
