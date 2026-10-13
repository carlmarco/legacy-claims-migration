namespace Etl.Core.Mapping;

/// <summary>
/// Where a mapping's companion files (code maps, reference code lists, target DDL, source) come from. The engine reads
/// through this interface only, so the same compiled mapping runs from disk (CLI) or from memory (browser demo).
/// Paths are relative to the mapping file's directory, exactly as written in the YAML.
/// </summary>
public interface IFileResolver
{
    Stream Open(string relativePath);
    bool Exists(string relativePath);
    /// <summary>A stable identity for the path: an absolute path on disk, a normalized key in memory.</summary>
    string FullPath(string relativePath);
}

public sealed class FileSystemResolver(string baseDir) : IFileResolver
{
    public string BaseDir { get; } = Path.GetFullPath(baseDir);
    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(BaseDir, relativePath));
    public Stream Open(string relativePath) => File.OpenRead(FullPath(relativePath));
    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));
}

/// <summary>Files held in memory, keyed by normalized path relative to a virtual base directory (e.g. "datasets/synpuf").</summary>
public sealed class InMemoryResolver(IReadOnlyDictionary<string, byte[]> files, string baseDir = "") : IFileResolver
{
    public string FullPath(string relativePath) => Normalize(baseDir, relativePath);
    public bool Exists(string relativePath) => files.ContainsKey(FullPath(relativePath));
    public Stream Open(string relativePath) =>
        files.TryGetValue(FullPath(relativePath), out var bytes) ? new MemoryStream(bytes, writable: false)
            : throw new FileNotFoundException($"'{relativePath}' (resolved to '{FullPath(relativePath)}') is not loaded");

    /// <summary>Resolve "a/b" + "../../c/d" → "c/d" without touching a real filesystem.</summary>
    public static string Normalize(string baseDir, string relative)
    {
        var parts = new List<string>();
        foreach (var p in (baseDir + "/" + relative).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return string.Join('/', parts);
    }
}
