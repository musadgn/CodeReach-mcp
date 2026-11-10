using System.Text;

namespace CodeReach.Core.Tools;

/// <summary>The text an MCP client receives at connect time. Generated from live configuration, never hand-kept.</summary>
public static class Instructions
{
    public static string Build(CodeReachConfig config, string solutionPath)
    {
        var text = new StringBuilder()
            .Append("CodeReach answers from a compiler-accurate index of ").Append(Path.GetFileName(solutionPath)).Append(" built with Roslyn: ")
            .Append("comments and strings never count, calls through interfaces and base classes are resolved, and test projects are excluded unless include_tests=true. ")
            .Append("Prefer it over grep and file search for any C# question about symbols. ")
            .Append("Before changing a method or type: trace_to_entrypoints (which endpoints and jobs reach it) and callers. ")
            .Append("Before changing an interface, base class or virtual member: type_hierarchy (every implementation that shares the contract). ")
            .Append("When adding a service, a constructor parameter or a background job: di_registrations (where and with which lifetime things are registered; it also reports captive dependencies and unregistered services). ")
            .Append("When adding or reviewing an endpoint: routes (verb, full route, authorization). ")
            .Append("After editing, before finishing: impact (the endpoints and jobs the diff affects). ")
            .Append("To learn how existing code works: callees down from a handler, find_usages for exact references, architecture for the project layout. ")
            .Append("Name symbols as Type.Member, path/File.cs:line, or an id from an answer; an ambiguous name is refused with the candidates. ")
            .Append("Read the last line of each answer: 'analysis: PARTIAL' means rows may be missing; a first line starting with STALE means the index is catching up with the files. ")
            .Append("An error starts with a CODE and says what to do next.");
        if (config.AuthAttributes.Length > 0)
        {
            text.Append(" Authorization attributes configured here: ").Append(string.Join(", ", config.AuthAttributes)).Append('.');
        }

        if (config.Exclude.Length > 0)
        {
            text.Append(" Not indexed: ").Append(string.Join(", ", config.Exclude)).Append('.');
        }

        if (config.EntryPoints.Length > 0)
        {
            text.Append(" Extra entry point kinds: ").Append(string.Join(", ", config.EntryPoints.Select(e => e.Kind).Distinct())).Append('.');
        }

        return text.Length <= Limits.InstructionsMaxChars ? text.ToString() : text.ToString(0, Limits.InstructionsMaxChars - 3) + "...";
    }
}
