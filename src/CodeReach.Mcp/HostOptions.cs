using CodeReach.Core;
using CodeReach.Core.Queries;

namespace CodeReach.Mcp;

/// <summary>Where the solution, its configuration and the index cache are. Resolved once at startup.</summary>
internal sealed record HostOptions(string SolutionPath, CodeReachConfig Config, string CacheDirectory)
{
    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln", "*.csproj"];

    public static HostOptions Resolve(string? solution, string? configPath, string? cacheDirectory, string workingDirectory)
    {
        var start = Path.GetFullPath(solution ?? workingDirectory, workingDirectory);
        var path = File.Exists(start) ? start : Discover(start);
        configPath ??= Path.Combine(Path.GetDirectoryName(path)!, CodeReachConfig.FileName);
        var config = new CodeReachConfig();
        if (File.Exists(configPath))
        {
            try
            {
                config = CodeReachConfig.Parse(File.ReadAllText(configPath));
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new ToolRefusal(Codes.ValidationFailed, $"{configPath} is not valid: {exception.Message}");
            }
        }
        else if (configPath != Path.Combine(Path.GetDirectoryName(path)!, CodeReachConfig.FileName))
        {
            throw new ToolRefusal(Codes.ValidationFailed, $"config file {configPath} does not exist.");
        }

        if (config.Solution is { } configured && solution is null)
        {
            path = Path.GetFullPath(configured, Path.GetDirectoryName(configPath)!);
        }

        cacheDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "codereach");
        return new HostOptions(path, config, cacheDirectory);
    }

    private static string Discover(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new ToolRefusal(Codes.SolutionNotFound, $"'{directory}' is neither a solution file nor a directory. Pass --solution <path to .slnx, .sln or .csproj>.");
        }

        foreach (var pattern in SolutionPatterns)
        {
            var found = Directory.GetFiles(directory, pattern);
            if (found.Length == 1)
            {
                return found[0];
            }

            if (found.Length > 1)
            {
                throw new ToolRefusal(Codes.SolutionNotFound, $"{directory} holds {found.Length} {pattern} files ({string.Join(", ", found.Select(Path.GetFileName))}). Pass --solution with the one to index.");
            }
        }

        throw new ToolRefusal(Codes.SolutionNotFound, $"no .slnx, .sln or .csproj in {directory}. Start the server in the solution directory or pass --solution <path>.");
    }
}
