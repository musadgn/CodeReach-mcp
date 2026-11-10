using System.Text.RegularExpressions;
using CodeReach.Core.Queries;
using CodeReach.Core.Tools;
using Xunit;

namespace CodeReach.Tests;

public sealed partial class PromptTests
{
    [Fact]
    public void Prompts_and_instructions_name_only_tools_and_parameters_that_exist()
    {
        var tools = ToolRegistry.All.Select(t => t.Name).Concat(ToolRegistry.All.SelectMany(t => t.Params.Select(p => p.Name))).ToHashSet(StringComparer.Ordinal);
        var texts = PromptRegistry.All
            .Select(p => PromptRegistry.Render(p.Name, p.Args.ToDictionary(a => a.Name, _ => "X", StringComparer.Ordinal)))
            .Append(Instructions.Build(SampleFixture.Config, SampleFixture.SolutionPath))
            .ToList();
        var named = texts.SelectMany(t => ToolName().Matches(t).Select(m => m.Value)).Distinct().ToList();

        Assert.True(named.Count >= 8, "the check found too few tool names to mean anything");
        Assert.All(named, name => Assert.Contains(name, tools));
    }

    [Fact]
    public void Every_tool_is_mentioned_in_the_instructions()
    {
        var text = Instructions.Build(SampleFixture.Config, SampleFixture.SolutionPath);

        Assert.All(ToolRegistry.All.Where(t => t.Name is not ("status" or "reindex" or "find_symbol" or "entrypoints")),
            tool => Assert.Contains(tool.Name, text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_required_argument_is_refused_with_its_description()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => PromptRegistry.Render("before-change", new Dictionary<string, string>()));

        Assert.StartsWith("VALIDATION_FAILED: before-change: 'symbol' is required: The method", refusal.Message, StringComparison.Ordinal);
        Assert.StartsWith("VALIDATION_FAILED: 'nope' is not a prompt.", Assert.Throws<ToolRefusal>(() => PromptRegistry.Render("nope", new Dictionary<string, string>())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_arguments_change_the_request_only_when_given()
    {
        var plain = PromptRegistry.Render("review-diff", new Dictionary<string, string>());
        var based = PromptRegistry.Render("review-diff", new Dictionary<string, string> { ["base"] = "main" });

        Assert.Contains("Call impact with no arguments (uncommitted changes).", plain, StringComparison.Ordinal);
        Assert.Contains("Call impact with base=\"main\".", based, StringComparison.Ordinal);
    }

    // Tool names in prose: lower-case words joined by underscores, plus the single-word tools written as calls.
    [GeneratedRegex(@"\b(?:[a-z]+_[a-z_]+|routes|callers|callees|impact|architecture)\b")]
    private static partial Regex ToolName();
}
