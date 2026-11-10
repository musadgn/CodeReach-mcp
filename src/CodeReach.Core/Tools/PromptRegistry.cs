using CodeReach.Core.Queries;

namespace CodeReach.Core.Tools;

public sealed record PromptArg(string Name, string Description, bool Required = false);

/// <summary>A guided workflow: a ready-made request that tells the agent which tools to call, in which order.</summary>
public sealed record PromptDef(
    string Name, string Title, string Description, IReadOnlyList<PromptArg> Args,
    Func<IReadOnlyDictionary<string, string>, string> Text);

/// <summary>The one list of prompts. MCP clients show them as commands (in Claude Code: /mcp__codereach__name).</summary>
public static class PromptRegistry
{
    private const string Rules =
        "Rules: answer C# questions about callers, usages, implementations, routes and registrations with CodeReach tools, not grep or file search. "
        + "Quote file:line locations from the answers. If an answer ends with 'analysis: PARTIAL' or starts with STALE, say so before drawing conclusions.";

    public static IReadOnlyList<PromptDef> All { get; } =
    [
        new("review-diff", "Review my change",
            "What the current change affects: endpoints and their authorization, background jobs, implementations that must change with it, and dependency injection problems.",
            [new("base", "Git ref to compare against, for example main. Default: uncommitted changes.")],
            a => $"""
                Review my change with CodeReach before I finish it.
                1. Call impact{(a.TryGetValue("base", out var b) && b.Length > 0 ? $" with base=\"{b}\"" : " with no arguments (uncommitted changes)")}.
                2. For every affected endpoint, report verb, route and authorization. Point out any endpoint that changes data (POST, PUT, PATCH, DELETE) and has auth 'none declared'.
                3. For every affected background job or other entry point, say what it does and how the change reaches it.
                4. If a changed symbol is an interface member, an abstract or virtual member, or a base class, call type_hierarchy on it and list the implementations that may need the same change.
                5. If the change adds or edits constructors or service registrations, call di_registrations and report captive dependencies and injected-but-unregistered services that involve the changed types.
                6. List changed symbols that reach no entry point (dead or only reached through reflection or DI).
                Start the answer with a one-paragraph risk summary, then the details.
                {Rules}
                """),
        new("before-change", "Before I change a symbol",
            "The blast radius of changing one method, property or type: who reaches it, from which endpoints and jobs, what implements it, and how it is wired.",
            [new("symbol", "The method, property or type you are about to change (OrderService.CancelAsync, IOrderService, path/File.cs:42).", Required: true)],
            a => $"""
                I am about to change {a["symbol"]}. Using CodeReach:
                1. Call trace_to_entrypoints for it and list every endpoint (with route and auth) and background job that reaches it.
                2. Call callers with depth 2 to show the direct and second-level callers.
                3. Call type_hierarchy for it (or its type) and list implementations or overrides that share the contract.
                4. Call di_registrations with its type and say how it is registered, its lifetime, and who has it injected.
                5. Call callers with include_tests=true and name the tests that exercise it.
                Then tell me what could break, which other places must change together, and which tests to run.
                {Rules}
                """),
        new("add-feature", "Plan a feature",
            "Find where a new feature fits by following how similar features are already built: the endpoint, the service layer, the interfaces to implement and the registrations to add.",
            [new("feature", "What you want to build, in a sentence.", Required: true)],
            a => $"""
                I want to build: {a["feature"]}
                Plan it to fit how this codebase already works, using CodeReach:
                1. Call routes with a filter for the closest existing feature to find similar endpoints, and note their route style and authorization.
                2. For the most similar endpoint, call callees on its handler and follow the chain down a few levels (controller, service, repository) to learn the layers and the interfaces used.
                3. Call type_hierarchy on those interfaces to see the existing implementations to copy.
                4. Call di_registrations for the services involved to see where and with which lifetime new services must be registered.
                5. Call architecture if the project layout is unclear.
                Then propose the files to add or change, in order, each with the existing file it should mirror. Do not write code until I confirm the plan.
                {Rules}
                """),
        new("audit-auth", "Audit endpoint authorization",
            "Every endpoint without declared authorization, every endpoint that allows anonymous access, and any global policy that might cover them.",
            [new("filter", "Optional route or controller text to limit the audit to.")],
            a => $"""
                Audit endpoint authorization with CodeReach{(a.TryGetValue("filter", out var f) && f.Length > 0 ? $", limited to routes matching \"{f}\"" : string.Empty)}.
                1. Call routes with auth="none"{(a.TryGetValue("filter", out var f1) && f1.Length > 0 ? $" and filter=\"{f1}\"" : string.Empty)}, then with auth="anonymous".
                2. Read the 'Global:' lines: a fallback policy or global filter may protect endpoints with none declared. Say which.
                3. Group the results by controller or route group. Mark endpoints that change data (POST, PUT, PATCH, DELETE) as highest risk.
                4. For each high-risk endpoint, call callees on its handler and say what data it touches.
                End with a table: route, verb, auth state, risk, suggested fix.
                {Rules}
                """),
        new("explain-endpoint", "Explain an endpoint",
            "What an HTTP endpoint does from the route down to the data: handler, authorization, every layer it calls and the background work it triggers.",
            [new("route", "Route text, for example /api/orders/{id} or a controller action name.", Required: true)],
            a => $"""
                Explain the endpoint {a["route"]} using CodeReach.
                1. Call routes with filter="{a["route"]}" and pick the matching endpoint; report verb, full route and authorization.
                2. Call callees on its handler, then on each service and repository it reaches, until you reach data access or external calls.
                3. Where a call goes through an interface, say which implementation runs (callees shows each candidate).
                Describe the flow step by step with file:line for each step, then list side effects (writes, messages, notifications).
                {Rules}
                """),
    ];

    /// <summary>The prompt text with arguments filled in. Refuses an unknown prompt or a missing required argument.</summary>
    public static string Render(string name, IReadOnlyDictionary<string, string> arguments)
    {
        var prompt = All.FirstOrDefault(p => p.Name == name)
            ?? throw new ToolRefusal(Codes.ValidationFailed, $"'{name}' is not a prompt. Prompts: {string.Join(", ", All.Select(p => p.Name))}.");
        foreach (var argument in prompt.Args)
        {
            if (arguments.TryGetValue(argument.Name, out var value) && value.Length > Limits.MaxSymbolLength)
            {
                throw new ToolRefusal(Codes.ValidationFailed, $"{name}: '{argument.Name}' is longer than {Limits.MaxSymbolLength} characters.");
            }

            if (argument.Required && (!arguments.TryGetValue(argument.Name, out var given) || string.IsNullOrWhiteSpace(given)))
            {
                throw new ToolRefusal(Codes.ValidationFailed, $"{name}: '{argument.Name}' is required: {argument.Description}");
            }
        }

        return prompt.Text(arguments);
    }
}
