using System.Text.Json;
using CodeReach.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeReach.Core.Queries;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CodeReach.Mcp;

/// <summary>The MCP door: stdio transport over the tool registry. It holds no query logic.</summary>
internal static class McpDoor
{
    public static async Task RunAsync(IIndexHost host, HostOptions options, CancellationToken cancel)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var tools = ToolRegistry.All.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = JsonDocument.Parse(tool.InputSchema()).RootElement,
            Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false },
        }).ToList();
        var prompts = PromptRegistry.All.Select(prompt => new Prompt
        {
            Name = prompt.Name,
            Title = prompt.Title,
            Description = prompt.Description,
            Arguments = [.. prompt.Args.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required })],
        }).ToList();
        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "codereach",
                    Version = typeof(McpDoor).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
                server.ServerInstructions = Instructions.Build(options.Config, options.SolutionPath);
            })
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }))
            .WithListPromptsHandler((_, _) => ValueTask.FromResult(new ListPromptsResult { Prompts = prompts }))
            .WithGetPromptHandler((request, _) =>
            {
                var name = request.Params?.Name ?? string.Empty;
                var given = request.Params?.Arguments?.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
                string text;
                try
                {
                    text = PromptRegistry.Render(name, given);
                }
                catch (ToolRefusal refusal)
                {
                    throw new McpException(refusal.Message);
                }

                return ValueTask.FromResult(new GetPromptResult
                {
                    Description = PromptRegistry.All.First(p => p.Name == name).Description,
                    Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
                });
            })
            .WithCallToolHandler(async (request, token) =>
            {
                var arguments = request.Params?.Arguments is { } given ? JsonSerializer.SerializeToElement(given) : default;
                var result = await ToolRegistry.CallAsync(host, request.Params?.Name ?? string.Empty, arguments, token).ConfigureAwait(false);
                return new CallToolResult { Content = [new TextContentBlock { Text = result.Text }], IsError = result.IsError };
            });

        // Warm the index while the client is still connecting; a failure here is reported by the first tool call.
        _ = Task.Run(() => host.AcquireAsync(cancel), cancel).ContinueWith(_ => { }, TaskScheduler.Default);
        await builder.Build().RunAsync(cancel).ConfigureAwait(false);
    }
}
