using System.Security.Cryptography;

namespace CodeReach.Mcp;

internal sealed record Stamp(long Length, long ModifiedTicks, string Hash);

/// <summary>What the index was built from: every source file and build input, as it was just before it was read.</summary>
internal sealed record StampSet(DateTime TakenUtc, Dictionary<string, Stamp> Files);

internal sealed record FileChanges(List<string> Changed, List<string> Added, List<string> Removed, List<string> BuildInputs, StampSet Current)
{
    public bool Any => Changed.Count + Added.Count + Removed.Count + BuildInputs.Count > 0;

    public string Describe()
    {
        var all = BuildInputs.Concat(Changed).Concat(Added).Concat(Removed).Select(Path.GetFileName).ToList();
        var names = string.Join(", ", all.Take(5)) + (all.Count > 5 ? ", ..." : string.Empty);
        return $"{all.Count} files changed ({names})";
    }
}

/// <summary>Decides whether the files on disk are still the ones the index was built from. Needs no compiler.</summary>
internal static class FileStamps
{
    private static readonly string[] BuildInputPatterns = [".csproj", ".props", ".targets", ".slnx", ".sln"];
    private static readonly string[] BuildInputNames = ["global.json", "nuget.config", "packages.lock.json"];

    /// <summary>A file stamped this close to its own modification time may change again within the same tick.</summary>
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    public static StampSet Take(string root, IEnumerable<string> projectDirectories)
    {
        var taken = DateTime.UtcNow;
        var files = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        foreach (var path in Enumerate(root, projectDirectories))
        {
            if (Read(path) is { } stamp)
            {
                files[path] = stamp;
            }
        }

        return new StampSet(taken, files);
    }

    public static FileChanges Compare(StampSet previous, string root, IEnumerable<string> projectDirectories)
    {
        var taken = DateTime.UtcNow;
        var current = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        var changed = new List<string>();
        var added = new List<string>();
        var inputs = new List<string>();
        foreach (var path in Enumerate(root, projectDirectories))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                continue;
            }

            var known = previous.Files.GetValueOrDefault(path);
            var settled = info.LastWriteTimeUtc < previous.TakenUtc - RacyWindow;
            if (known is not null && settled && known.Length == info.Length && known.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
            {
                current[path] = known;
                continue;
            }

            if (Read(path) is not { } stamp)
            {
                continue;
            }

            current[path] = stamp;
            if (known?.Hash == stamp.Hash)
            {
                continue;
            }

            (IsSource(path) ? known is null ? added : changed : inputs).Add(path);
        }

        var removed = previous.Files.Keys.Where(path => !current.ContainsKey(path)).ToList();
        inputs.AddRange(removed.Where(path => !IsSource(path)));
        removed.RemoveAll(path => !IsSource(path));
        return new FileChanges(changed, added, removed, inputs, new StampSet(taken, current));
    }

    public static bool IsRelevant(string path) =>
        (IsSource(path) || IsBuildInput(path)) && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(Skipped);

    private static bool IsSource(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildInput(string path) =>
        BuildInputPatterns.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase))
        || BuildInputNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static bool Skipped(string directory) =>
        directory is "bin" or "obj" or "node_modules" || (directory.Length > 1 && directory[0] == '.' && directory != "..");

    private static IEnumerable<string> Enumerate(string root, IEnumerable<string> projectDirectories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var directories = projectDirectories.ToList();
        foreach (var path in Walk(root).Where(IsBuildInput))
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var directory in directories)
        {
            var assets = Path.Combine(directory, "obj", "project.assets.json");
            if (File.Exists(assets) && seen.Add(assets))
            {
                yield return assets;
            }

            foreach (var path in Walk(directory).Where(p => IsSource(p) || IsBuildInput(p)))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> Walk(string directory)
    {
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(current);
                children = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in children.Where(c => !Skipped(Path.GetFileName(c))))
            {
                pending.Push(child);
            }
        }
    }

    private static Stamp? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new Stamp(info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(stream))[..32]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
