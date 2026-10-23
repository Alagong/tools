using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyFont.Core;

public sealed class CachedFontFile
{
    public long Size { get; set; }
    public long Mtime { get; set; }
    public FontFlavor Flavor { get; set; }
    public List<FontFaceInfo> Faces { get; set; } = new();
    public long LastUsed { get; set; }
}

/// <summary>
/// 字体元数据索引缓存。以 (路径, 大小, 修改时间) 为键命中，避免每次启动都重新解析上千个字体文件。
/// </summary>
public sealed class FontCache
{
    private const int MaxEntries = 40000;

    public Dictionary<string, CachedFontFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static FontCache Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<FontCache>(json, JsonOptions);
                if (loaded is not null)
                {
                    var fresh = new Dictionary<string, CachedFontFile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in loaded.Files) fresh[kv.Key] = kv.Value;
                    loaded.Files = fresh;
                    return loaded;
                }
            }
        }
        catch
        {
            // 缓存损坏时静默重建。
        }

        return new FontCache();
    }

    public void Save(string path)
    {
        try
        {
            if (Files.Count > MaxEntries)
            {
                var keep = Files.OrderByDescending(kv => kv.Value.LastUsed).Take(MaxEntries).ToDictionary(kv => kv.Key, kv => kv.Value);
                Files = new Dictionary<string, CachedFontFile>(keep, StringComparer.OrdinalIgnoreCase);
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 缓存写入失败不应影响主流程。
        }
    }

    public bool TryGet(string path, long size, long mtime, out CachedFontFile entry)
    {
        if (Files.TryGetValue(path, out var cached) && cached.Size == size && cached.Mtime == mtime && cached.Faces.Count > 0)
        {
            cached.LastUsed = DateTime.UtcNow.Ticks;
            entry = cached;
            return true;
        }

        entry = null!;
        return false;
    }

    public void Put(string path, CachedFontFile entry)
    {
        entry.LastUsed = DateTime.UtcNow.Ticks;
        Files[path] = entry;
    }
}
