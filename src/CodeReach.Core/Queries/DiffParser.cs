using System.Text.RegularExpressions;

namespace CodeReach.Core.Queries;

/// <summary>Changed line ranges on the new side of one file in a unified diff.</summary>
public sealed record FileChange(string Path, bool Deleted, List<(int Start, int End)> Ranges);

public static partial class DiffParser
{
    public static List<FileChange> Parse(string diff)
    {
        var changes = new List<FileChange>();
        FileChange? current = null;
        string? oldPath = null;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = PathOf(line[4..]);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var newPath = PathOf(line[4..]);
                current = new FileChange(newPath ?? oldPath ?? string.Empty, newPath is null, []);
                changes.Add(current);
            }
            else if (current is not null && Hunk().Match(line) is { Success: true } hunk)
            {
                var start = int.Parse(hunk.Groups[1].Value);
                var count = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;

                // A pure deletion has no new-side lines: it sits between `start` and the line after it.
                current.Ranges.Add(count == 0 ? (Math.Max(start, 1), start + 1) : (start, start + count - 1));
            }
        }

        return changes;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex Hunk();

    private static string? PathOf(string text)
    {
        text = text.Split('\t')[0].Trim().Trim('"');
        if (text == "/dev/null")
        {
            return null;
        }

        return text.StartsWith("a/", StringComparison.Ordinal) || text.StartsWith("b/", StringComparison.Ordinal) ? text[2..] : text;
    }
}
