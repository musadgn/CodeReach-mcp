using System.Text;

namespace CodeReach.Core.Tools;

/// <summary>The text an MCP client receives at connect time. Generated from live configuration, never hand-kept.</summary>
public static class Instructions
{
    public static string Build(CodeReachConfig config, string solutionPath)
    {
        var text = new StringBuilder()
            .Append("CodeReach answers from a compiler-accurate index of ").Append(Path.GetFileName(solutionPath)).Append(". ")
            .Append("Every answer is bound by the C# semantic model: comments and strings never count, and calls through interfaces and base classes are resolved. ")
            .Append("Start from trace_to_entrypoints (what reaches this code), impact (what a diff affects) or routes (endpoints and their authorization). ")
            .Append("Name symbols as Type.Member, as path/File.cs:line, or with an id copied from an answer; an ambiguous name is refused with the candidates. ")
            .Append("Test projects are excluded unless include_tests=true, and each answer says how many rows that hid. ")
            .Append("Read the last line of every answer: 'analysis: PARTIAL' means rows may be missing, and a first line starting with STALE means the index is behind the files. ")
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
