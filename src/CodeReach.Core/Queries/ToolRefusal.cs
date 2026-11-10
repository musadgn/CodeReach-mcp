namespace CodeReach.Core.Queries;

/// <summary>The closed list of refusal codes. A refusal is always <c>CODE: reason and remedy</c>.</summary>
public static class Codes
{
    public const string IndexBuilding = "INDEX_BUILDING";
    public const string SolutionNotFound = "SOLUTION_NOT_FOUND";
    public const string SolutionLoadFailed = "SOLUTION_LOAD_FAILED";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string AmbiguousSymbol = "AMBIGUOUS_SYMBOL";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string GitFailed = "GIT_FAILED";
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string Internal = "INTERNAL";
}

public sealed class ToolRefusal(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}
