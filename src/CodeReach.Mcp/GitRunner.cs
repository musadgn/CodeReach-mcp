using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeReach.Core;
using CodeReach.Core.Queries;

namespace CodeReach.Mcp;

/// <summary>Runs <c>git diff</c> with an argument vector. Nothing here goes through a shell.</summary>
internal static partial class GitRunner
{
    public static async Task<string> DiffAsync(string directory, string? baseRef, CancellationToken cancel)
    {
        baseRef = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        if (baseRef.StartsWith('-') || !SafeRef().IsMatch(baseRef))
        {
            throw new ToolRefusal(Codes.ValidationFailed, $"'{baseRef}' is not accepted as a git ref. Use a branch, tag, commit or an expression such as HEAD~3.");
        }

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "diff", "--no-color", "--no-ext-diff", "--unified=0", "--relative", baseRef, "--" })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var error = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ToolRefusal(Codes.GitFailed, $"git diff {baseRef} exited {process.ExitCode} in {directory}: {(await error.ConfigureAwait(false)).Trim()}");
            }

            var text = await output.ConfigureAwait(false);
            return text.Length <= Limits.MaxDiffBytes
                ? text
                : throw new ToolRefusal(Codes.GitFailed, $"the diff against {baseRef} is larger than {Limits.MaxDiffBytes} bytes. Compare against a closer ref.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ToolRefusal(Codes.GitFailed, $"git could not be started ({exception.Message}). Pass the diff text in the 'diff' argument instead.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/~^@{}\-]{1,200}$")]
    private static partial Regex SafeRef();
}
