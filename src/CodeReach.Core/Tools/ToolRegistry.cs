using System.Text.Json;
using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

/// <summary>The one list of tools. Every door loops over it; none holds a second list.</summary>
public static class ToolRegistry
{
    private static readonly Param Symbol = new(
        "symbol", ParamType.String,
        "A method, property, field, constant or type: a qualified-name suffix (OrderService.CancelAsync), a file:line location (src/Orders/OrderService.cs:42), or an id from another answer.",
        Required: true);

    private static readonly Param IncludeTests = new(
        "include_tests", ParamType.Boolean, "Also count code in test projects (projects that reference a test framework). Default false.");

    private static readonly Param Limit = new(
        "limit", ParamType.Integer, "Maximum rows to print.", Default: Limits.DefaultRows, Min: 1, Max: Limits.MaxRows);

    public static IReadOnlyList<ToolDef> All { get; } =
    [
        new("status",
            "Index state: solution, projects and which are test projects, counts, freshness, configuration in effect, and anything that failed to load. Call this first when an answer looks wrong.",
            [], SurveyTools.Status),
        new("reindex",
            "Bring the index up to date with the files on disk and report what changed. The index refreshes itself, so this is only for forcing it. full=true reloads the solution and rebuilds everything (about a minute on a large solution).",
            [new("full", ParamType.Boolean, "Reload the solution and rebuild from scratch. Default false.")], SurveyTools.Reindex),
        new("find_symbol",
            "Search declarations by name. Use it to discover the exact symbol before asking about it. Matches any part of the qualified name, case-insensitive.",
            [
                new("query", ParamType.String, "Text contained in the qualified name.", Required: true),
                new("kind", ParamType.String, "Restrict to one kind.", Options: ["type", "method", "constructor", "property", "field", "event", "handler"]),
                IncludeTests, Limit,
            ], ReachTools.FindSymbol),
        new("callers",
            "Who calls this symbol, resolved by the compiler. Calls made through an interface or a base class are included and labelled with the member they were bound to. Tests are excluded and counted. depth>1 walks further up.",
            [
                Symbol,
                new("depth", ParamType.Integer, "Levels of callers to walk.", Default: Limits.DefaultCallDepth, Min: 1, Max: Limits.MaxCallDepth),
                IncludeTests, Limit,
            ], ReachTools.Callers),
        new("callees",
            "What this symbol calls or accesses (methods, constructors, properties, constants and static fields; instance fields are left out). A call through an interface or virtual member is expanded to the implementations it can reach.",
            [Symbol, new("include_external", ParamType.Boolean, "Also list framework and library members. Default false."), Limit],
            ReachTools.Callees),
        new("find_usages",
            "Every reference bound to exactly this symbol (calls, reads, writes, nameof, type references, attributes), with the kind of each. Never textual: comments and strings do not count. Works for framework members too.",
            [Symbol, IncludeTests, Limit], ReachTools.FindUsages),
        new("trace_to_entrypoints",
            "Which HTTP endpoints, background jobs and other entry points reach this symbol, with one call path for each, crossing interfaces automatically. Chains that end where nothing calls them are reported as unrooted instead of dropped.",
            [
                Symbol, IncludeTests,
                new("max_depth", ParamType.Integer, "Maximum call-chain length.", Default: Limits.DefaultTraceDepth, Min: 1, Max: Limits.MaxTraceDepth),
                Limit,
            ], ReachTools.Trace),
        new("routes",
            "HTTP endpoints: verb, full route template, handler and authorization, for controllers and minimal APIs. auth=none lists endpoints with no authorization declared anywhere.",
            [
                new("filter", ParamType.String, "Text contained in the route, the handler name or the authorization (for example a permission key)."),
                new("verb", ParamType.String, "HTTP verb.", Options: ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"]),
                new("auth", ParamType.String, "Filter by authorization state.", Options: ["any", "none", "anonymous", "declared"]),
                IncludeTests, Limit,
            ], SurveyTools.Routes),
        new("entrypoints",
            "Every root the trace stops at: endpoints, hosted services, program main, and configured kinds such as message handlers.",
            [new("kind", ParamType.String, "One kind, as printed in the summary (endpoint, hosted-service, main, ...).", MaxLength: 60), IncludeTests, Limit],
            SurveyTools.EntryPoints),
        new("impact",
            "What a change affects: maps a git diff to the symbols it touches and traces each to the endpoints and jobs that reach it. With no arguments it uses the uncommitted changes; with symbol it answers for a change you are about to make to that symbol.",
            [
                new("symbol", ParamType.String, "Instead of a diff: the method, property or type you plan to change (Type.Member, path/File.cs:line, or an id)."),
                new("base", ParamType.String, "Git ref to compare the working tree against, for example main or HEAD~3. Default HEAD.", MaxLength: 200),
                new("diff", ParamType.String, "A unified diff to analyse instead of running git. It must describe the files as they are on disk now.", MaxLength: Limits.MaxDiffBytes),
                IncludeTests, Limit,
            ], SurveyTools.Impact),
        new("type_hierarchy",
            "A type's place in the hierarchy, from the compiler: what it inherits and implements, every type that derives from it or implements it (direct or indirect), its members with what each implements or overrides, and how it is registered for dependency injection. Use it before adding an implementation or changing a base class.",
            [
                new("symbol", ParamType.String, "A class or interface (OrderService, IOrderService, BackgroundService), a member of one, a file:line, or an id.", Required: true),
                IncludeTests, Limit,
            ], TypeTools.TypeHierarchy),
        new("di_registrations",
            "Dependency injection as the code declares it: every AddScoped/AddTransient/AddSingleton/TryAdd/keyed/AddHostedService/AddHttpClient/AddDbContext call with lifetime and location, and what each constructor asks for. Without a symbol it also reports captive dependencies (a singleton holding a scoped service), interfaces that are injected but never registered, and services registered twice. With a symbol it shows that type's registrations, implementations, who injects it, and whether each of its own constructor dependencies is registered. Use it when adding a service, a constructor parameter or a background job.",
            [
                new("symbol", ParamType.String, "Optional class or interface to focus on (IOrderService, OrderPurgeService)."),
                new("lifetime", ParamType.String, "Only list registrations with this lifetime.", Options: ["singleton", "scoped", "transient"]),
                IncludeTests, Limit,
            ], TypeTools.Registrations),
        new("architecture",
            "Overview: projects and how much each really uses the others, entry point counts, the most called methods and the most implemented interfaces.",
            [new("limit", ParamType.Integer, "Rows per ranking.", Default: 10, Min: 1, Max: 100)], SurveyTools.Architecture),
    ];

    /// <summary>Runs a tool by name. Never throws: refusals and failures come back as error results.</summary>
    public static async Task<ToolResult> CallAsync(IIndexHost host, string name, JsonElement input, CancellationToken cancel)
    {
        try
        {
            var tool = All.FirstOrDefault(t => t.Name == name)
                ?? throw new ToolRefusal(Codes.UnknownTool, $"'{name}' is not a tool. Tools: {string.Join(", ", All.Select(t => t.Name))}.");
            return await tool.Run(host, new Args(tool, input), cancel).ConfigureAwait(false);
        }
        catch (ToolRefusal refusal)
        {
            return new ToolResult(refusal.Message, IsError: true);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var correlation = Guid.NewGuid().ToString("N")[..8];
            await Console.Error.WriteLineAsync($"[codereach] {correlation} {name} failed: {exception}").ConfigureAwait(false);
            return new ToolResult($"{Codes.Internal}: the tool failed unexpectedly (correlation {correlation}; details are in the server's stderr). Call status to check the index.", IsError: true);
        }
    }
}
