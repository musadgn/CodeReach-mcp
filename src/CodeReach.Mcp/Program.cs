using CodeReach.Core.Queries;
using CodeReach.Mcp;

var positional = new List<string>();
var named = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--solution" or "--config" or "--cache-dir" && i + 1 < args.Length)
    {
        named[args[i]] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

var command = positional.Count > 0 ? positional[0] : "serve";
if (command is "--version" or "version")
{
    Console.WriteLine(typeof(CliDoor).Assembly.GetName().Version?.ToString(3));
    return CliDoor.Clean;
}

if (command is "tools")
{
    Console.WriteLine(CliDoor.ListTools());
    return CliDoor.Clean;
}

if (command is not ("serve" or "call") || (command == "call" && positional.Count < 2))
{
    Console.Error.WriteLine(
        """
        codereach: which endpoints and jobs reach this code, and what does your diff affect.

          codereach [serve]                    run as an MCP server on stdio (the default)
          codereach call <tool> ['{json}']     run one tool and print its answer
          codereach tools                      list the tools and their arguments

          --solution <path>    a .slnx, .sln or .csproj, or a directory holding exactly one (default: current directory)
          --config <path>      configuration file (default: codereach.json beside the solution)
          --cache-dir <path>   where the index snapshot is kept (default: the user's local application data)

        Exit codes of `call`: 0 answered, 1 answered with a finding, 2 could not run.
        """);
    return command is "--help" or "-h" or "help" ? CliDoor.Clean : CliDoor.CouldNotRun;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

HostOptions options;
try
{
    options = HostOptions.Resolve(
        named.GetValueOrDefault("--solution"), named.GetValueOrDefault("--config"), named.GetValueOrDefault("--cache-dir"),
        Directory.GetCurrentDirectory());
}
catch (ToolRefusal refusal)
{
    Console.Error.WriteLine(refusal.Message);
    return CliDoor.CouldNotRun;
}

// A one-shot command has nothing better to do than wait; a server answers from the previous index instead.
using var host = new IndexHost(options, command == "call" ? Timeout.InfiniteTimeSpan : null);
if (command == "call")
{
    var exit = await CliDoor.CallAsync(host, positional[1], positional.Count > 2 ? positional[2] : null, cancellation.Token);
    await host.SettleAsync();
    return exit;
}

host.Watch();

await McpDoor.RunAsync(host, options, cancellation.Token);
return CliDoor.Clean;
