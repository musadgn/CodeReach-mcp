using System.Security.Cryptography;
using System.Text;
using CodeReach.Core.Facts;
using CodeReach.Roslyn;

namespace CodeReach.Mcp;

/// <summary>
/// Keeps the facts and their stamps in one file in the cache directory. The file name carries the format,
/// extractor version and configuration hash, so a snapshot made under other rules is simply never found.
/// </summary>
internal sealed class SnapshotStore
{
    public SnapshotStore(HostOptions options)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.SolutionPath)))[..16];
        FilePath = Path.Combine(
            options.CacheDirectory, key,
            $"index-f{FactsSerializer.FormatVersion}-x{RoslynIndexer.ExtractorVersion}-{options.Config.Hash()}.bin");
    }

    public string FilePath { get; }

    public (IndexFacts Facts, StampSet Stamps)? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            // Read fully and close at once: another server process may want to replace the file.
            using var stream = new MemoryStream(File.ReadAllBytes(FilePath));
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var taken = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var files = new Dictionary<string, Stamp>(StringComparer.Ordinal);
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                files[reader.ReadString()] = new Stamp(reader.ReadInt64(), reader.ReadInt64(), reader.ReadString());
            }

            return FactsSerializer.Read(stream) is { } facts ? (facts, new StampSet(taken, files)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"[codereach] snapshot {FilePath} could not be read and is ignored: {exception.Message}");
            return null;
        }
    }

    /// <summary>Writes to a temporary file and renames it. A failure is reported and never fails a query.</summary>
    public void Save(IndexFacts facts, StampSet stamps)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(stamps.TakenUtc.Ticks);
                    writer.Write(stamps.Files.Count);
                    foreach (var (path, stamp) in stamps.Files)
                    {
                        writer.Write(path);
                        writer.Write(stamp.Length);
                        writer.Write(stamp.ModifiedTicks);
                        writer.Write(stamp.Hash);
                    }
                }

                FactsSerializer.Write(stream, facts);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, FilePath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[codereach] snapshot was not saved to {FilePath}: {exception.Message}");
            File.Delete(temporary);
        }
    }
}
