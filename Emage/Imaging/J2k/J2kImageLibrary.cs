using System.Collections.Concurrent;

namespace Emage.Imaging.J2k;

public sealed class IndexedImage
{
    public required byte[] Data { get; init; }
    public required J2kCodestream Codestream { get; init; }
}

public sealed class J2kImageLibrary
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, IndexedImage> _cache = new();

    public J2kImageLibrary(string imageRoot)
    {
        _root = Path.GetFullPath(imageRoot);
    }

    public string RootPath => _root;

    public bool TryResolve(string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        string normalized = relative.Replace('\\', '/');
        string combined = Path.GetFullPath(Path.Combine(_root, normalized));
        string rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal)) return false;
        fullPath = combined;
        return true;
    }

    public bool TryDelete(string relative, out bool existed)
    {
        existed = false;
        if (!TryResolve(relative, out string fullPath)) return false;
        _cache.TryRemove(fullPath, out _);
        existed = File.Exists(fullPath);
        try
        {
            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public IEnumerable<(string RelativePath, long Length)> EnumerateImages()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(_root, file), new FileInfo(file).Length);
    }

    public bool TryGet(string relative, out IndexedImage image)
    {
        image = null!;
        if (!TryResolve(relative, out string fullPath)) return false;
        if (_cache.TryGetValue(fullPath, out var cached) && cached is not null)
        {
            image = cached;
            return true;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(fullPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        J2kCodestream codestream;
        try
        {
            codestream = J2kCodestreamParser.Parse(data, fullPath);
        }
        catch (J2kException)
        {
            return false;
        }

        image = new IndexedImage { Data = data, Codestream = codestream };
        _cache[fullPath] = image;
        return true;
    }
}