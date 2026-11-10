namespace CodeReach.Tests;

/// <summary>A private copy of the sample solution that a test may edit.</summary>
internal sealed class TempSample : IDisposable
{
    public TempSample()
    {
        Root = Path.Combine(Path.GetTempPath(), "codereach-tests", Guid.NewGuid().ToString("N")[..12]);
        Copy(SampleFixture.SampleDirectory, Root);
        SampleFixture.Restore(Root);
    }

    public string Root { get; }

    public string SolutionPath => Path.Combine(Root, "SampleShop.slnx");

    public string CacheDirectory => Path.Combine(Root, ".cache");

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Edit(string relative, string find, string replace)
    {
        var path = PathOf(relative);
        var text = File.ReadAllText(path);
        if (!text.Contains(find, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{find}' is not in {relative}; the sample changed under this test.");
        }

        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A build host may still hold a file for a moment; the directory lives under the temp folder.
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(from).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
