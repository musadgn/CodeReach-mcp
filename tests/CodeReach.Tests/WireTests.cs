using CodeReach.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeReach.Tests;

/// <summary>Drives the built server over real stdio with the SDK's client: the same path an MCP host takes.</summary>
public sealed class WireTests
{
    [Fact]
    public async Task Server_lists_the_registry_and_answers_over_stdio()
    {
        using var sample = new TempSample();
        var cancel = TestContext.Current.CancellationToken;
        var server = Path.Combine(AppContext.BaseDirectory, "codereach.dll");
        Assert.True(File.Exists(server), server + " was not built next to the tests");
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "codereach",
                Command = "dotnet",
                Arguments = [server, "--solution", sample.SolutionPath, "--cache-dir", sample.CacheDirectory],
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }),
            cancellationToken: cancel);

        Assert.Equal("codereach", client.ServerInfo?.Name);
        Assert.Contains("SampleShop.slnx", client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);
        var tools = await client.ListToolsAsync(cancellationToken: cancel);
        Assert.Equal(ToolRegistry.All.Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());

        var trace = await CallUntilBuiltAsync(client, "trace_to_entrypoints", new() { ["symbol"] = "OrderRepository.HardDeleteAsync" }, cancel);
        Assert.NotEqual(true, trace.IsError);
        Assert.Contains("4 entry points (3 endpoint, 1 hosted-service), 1 unrooted tops", Text(trace), StringComparison.Ordinal);

        var routes = await client.CallToolAsync("routes", new Dictionary<string, object?> { ["auth"] = "none", ["limit"] = 100 }, cancellationToken: cancel);
        Assert.Contains("7 of 18 endpoints", Text(routes), StringComparison.Ordinal);

        var prompts = await client.ListPromptsAsync(cancellationToken: cancel);
        Assert.Equal(PromptRegistry.All.Select(p => p.Name).Order(), prompts.Select(p => p.Name).Order());
        var prompt = await client.GetPromptAsync("before-change", new Dictionary<string, object?> { ["symbol"] = "OrderService.CancelAsync" }, cancellationToken: cancel);
        var promptText = string.Concat(prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("I am about to change OrderService.CancelAsync.", promptText, StringComparison.Ordinal);

        var refusal = await client.CallToolAsync("callers", new Dictionary<string, object?> { ["symbol"] = "Delete" }, cancellationToken: cancel);
        Assert.True(refusal.IsError);
        Assert.StartsWith("AMBIGUOUS_SYMBOL:", Text(refusal), StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>A server that has never seen the solution refuses with INDEX_BUILDING until its first index exists.</summary>
    private static async Task<CallToolResult> CallUntilBuiltAsync(McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancel);
            if (result.IsError != true || !Text(result).StartsWith("INDEX_BUILDING", StringComparison.Ordinal) || attempt > 20)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancel);
        }
    }
}
