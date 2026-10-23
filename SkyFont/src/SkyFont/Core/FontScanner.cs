using System.IO;

namespace SkyFont.Core;

public sealed class ScanResult
{
    public List<FontEntry> Entries { get; } = new();
    public int FilesScanned { get; set; }
    public int FilesFailed { get; set; }
    public int FilesFromCache { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public static class FontScanner
{
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc", ".otc"
    };

    public static bool IsFontFile(string path) => FontExtensions.Contains(Path.GetExtension(path));

    /// <summary>递归枚举字体文件；对无权限目录静默跳过，并避免跟随目录联接造成环。</summary>
    public static IEnumerable<string> Enumerate(string folder, bool recursive, CancellationToken ct = default)
    {
        var stack = new Stack<string>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (IsFontFile(file)) yield return file;
            }

            if (!recursive) continue;

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string dir in dirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LinkTarget is not null) continue; // 跳过符号链接/联接
                    if (info.Attributes.HasFlag(FileAttributes.System)) continue;
                }
                catch
                {
                    continue;
                }

                stack.Push(dir);
            }
        }
    }

    public static ScanResult Scan(
        string folder,
        bool recursive,
        string rootFolder,
        FontCache cache,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ScanResult();
        var started = DateTime.UtcNow;

        var files = Enumerate(folder, recursive, ct).ToList();
        int total = files.Count;
        int done = 0;

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            result.FilesScanned++;

            if (done % 25 == 0 || done == total)
                progress?.Report((done, total, file));

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                result.FilesFailed++;
                continue;
            }

            long size = info.Length;
            long mtime = info.LastWriteTimeUtc.Ticks;

            if (cache.TryGet(file, size, mtime, out var cached))
            {
                result.FilesFromCache++;
                foreach (var face in cached.Faces)
                    result.Entries.Add(new FontEntry(file, rootFolder, face, size, cached.Flavor));
                continue;
            }

            var parsed = FontParser.Parse(file);
            if (!parsed.Ok)
            {
                result.FilesFailed++;
                continue;
            }

            cache.Put(file, new CachedFontFile
            {
                Size = size,
                Mtime = mtime,
                Flavor = parsed.Flavor,
                Faces = parsed.Faces
            });

            foreach (var face in parsed.Faces)
                result.Entries.Add(new FontEntry(file, rootFolder, face, size, parsed.Flavor));
        }

        result.Elapsed = DateTime.UtcNow - started;
        return result;
    }
}
