using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace SkyFont.Core;

/// <summary>清单中的一条字体记录。</summary>
public sealed class ManifestItem
{
    /// <summary>远程地址（已规范化为 http/https 绝对地址）。</summary>
    public required string Url { get; init; }

    /// <summary>可选的显示名，仅在 URL 里拿不到文件名时用于生成文件名。</summary>
    public string? Name { get; init; }

    /// <summary>可选的显式文件名（不含目录）。</summary>
    public string? FileName { get; init; }

    /// <summary>可选的预期 SHA-256（十六进制），用于校验下载完整性。</summary>
    public string? Sha256 { get; init; }

    /// <summary>目标文件已存在时是否覆盖。</summary>
    public bool Overwrite { get; init; }

    /// <summary>在清单中的序号，用于生成兜底文件名。</summary>
    public int Index { get; init; }
}

/// <summary>清单解析结果。</summary>
public sealed class ManifestResult
{
    public List<ManifestItem> Items { get; } = new();

    /// <summary>不影响主流程但值得提示的问题（跳过条目、地址无效等）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>下载目录名（相对于程序所在目录），默认 downloads。</summary>
    public string DirectoryName { get; set; } = FontManifest.DefaultDirectory;

    public string ManifestPath { get; init; } = "";

    public string ManifestDirectory =>
        string.IsNullOrEmpty(ManifestPath) ? "." : Path.GetDirectoryName(Path.GetFullPath(ManifestPath)) ?? ".";

    /// <summary>字体下载目录：始终位于程序（SkyFont.exe）所在目录下的 downloads，与清单放在哪无关。</summary>
    public string TargetDirectory => Path.Combine(FontManifest.AppDirectory, DirectoryName);
}

/// <summary>
/// 字体清单解析器：支持 JSON 与 XML 两种格式。
///
/// 设计上刻意容忍写法差异 —— 清单通常由脚本或第三方导出，格式很难完全统一，
/// 与其要求用户改文件，不如在解析层把常见变体都吃掉，无法识别的条目降级为警告。
/// </summary>
public static class FontManifest
{
    public const string DefaultDirectory = "downloads";

    /// <summary>
    /// 程序所在目录。
    ///
    /// 单文件发布时 <see cref="AppContext.BaseDirectory"/> 指向 exe 所在目录（而不是临时解包目录），
    /// 但为了在个别宿主 / 单文件自解压场景下也不出错，优先用 <see cref="Environment.ProcessPath"/>。
    /// </summary>
    public static string AppDirectory { get; } = ResolveAppDirectory();

    private static string ResolveAppDirectory()
    {
        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable))
        {
            string? directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrEmpty(directory)) return directory;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static readonly string[] UrlKeys = { "url", "link", "href", "src", "address" };
    private static readonly string[] NameKeys = { "name", "title", "family" };
    private static readonly string[] FileKeys = { "file", "filename", "target", "saveas", "saveAs" };
    private static readonly string[] DirKeys = { "downloads", "dir", "directory", "folder" };
    private static readonly string[] ListKeys = { "fonts", "files", "items", "urls", "list" };

    private static readonly HashSet<string> ItemElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "font", "file", "item", "entry", "asset", "url", "link", "href"
    };

    private static readonly HashSet<string> ContainerElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fonts", "files", "items", "urls", "manifest", "list", "root"
    };

    public static bool IsManifestFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".json" or ".xml";

    /// <summary>按扩展名（并辅以内容嗅探）解析清单文件。</summary>
    public static ManifestResult Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("找不到清单文件", full);

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("无法读取清单文件：" + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("清单文件是空的");

        var result = new ManifestResult { ManifestPath = full };

        switch (Path.GetExtension(full).ToLowerInvariant())
        {
            case ".json":
                ParseJson(text, result);
                break;
            case ".xml":
                ParseXml(text, result);
                break;
            default:
                // 扩展名不可靠时按首个非空白字符嗅探
                char first = text.TrimStart()[0];
                if (first is '{' or '[') ParseJson(text, result);
                else if (first == '<') ParseXml(text, result);
                else throw new InvalidDataException("无法识别的清单格式（仅支持 JSON / XML）");
                break;
        }

        Deduplicate(result);

        if (result.Items.Count == 0)
            throw new InvalidDataException("清单中没有可用的字体地址");

        return result;
    }

    private static void Deduplicate(ManifestResult result)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = result.Items.Where(i => seen.Add(i.Url)).ToList();
        int removed = result.Items.Count - unique.Count;
        if (removed > 0)
        {
            result.Items.Clear();
            result.Items.AddRange(unique);
            result.Warnings.Add($"清单中有 {removed} 条重复地址已忽略");
        }
    }

    // ------------------------------------------------------------------ JSON

    private static void ParseJson(string text, ManifestResult result)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 解析失败：" + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                ParseJsonArray(root, result);
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("JSON 根节点必须是对象或数组");

            if (JsonString(root, DirKeys) is { } dir)
                result.DirectoryName = SanitizeDirectoryName(dir);

            if (FindArray(root, ListKeys) is { } list)
            {
                ParseJsonArray(list, result);
                return;
            }

            // 根对象本身就是单个字体描述
            var single = CreateJsonItem(root, 1, result);
            if (single is not null) result.Items.Add(single);
            else throw new InvalidDataException("JSON 中未找到字体列表（期望 fonts / files / items / urls 数组）");
        }
    }

    private static void ParseJsonArray(JsonElement array, ManifestResult result)
    {
        int index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;

            if (element.ValueKind == JsonValueKind.String)
            {
                var item = FromUrl(element.GetString(), index, result);
                if (item is not null) result.Items.Add(item);
                continue;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                string? raw = JsonString(element, UrlKeys);
                var item = CreateJsonItem(element, index, result);
                if (item is not null) result.Items.Add(item);
                else if (raw is null) result.Warnings.Add($"第 {index} 条记录没有 url 字段，已跳过");
                // 地址存在但无效时，FromUrl 已经给出具体原因，这里不重复提示
                continue;
            }

            result.Warnings.Add($"第 {index} 条记录类型不支持（{element.ValueKind}），已跳过");
        }
    }

    private static ManifestItem? CreateJsonItem(JsonElement obj, int index, ManifestResult result)
    {
        string? raw = JsonString(obj, UrlKeys);
        var item = FromUrl(raw, index, result);
        if (item is null) return null;

        return new ManifestItem
        {
            Url = item.Url,
            Name = JsonString(obj, NameKeys),
            FileName = JsonString(obj, FileKeys),
            Sha256 = JsonString(obj, new[] { "sha256", "checksum", "hash" }),
            Overwrite = JsonBool(obj, "overwrite") ?? false,
            Index = index
        };
    }

    private static ManifestItem? FromUrl(string? raw, int index, ManifestResult result)
    {
        var (url, rewritten) = Normalize(raw);
        if (url is null)
        {
            if (!string.IsNullOrWhiteSpace(raw))
                result.Warnings.Add($"第 {index} 条记录的地址无效（仅支持 http/https）：{raw}");
            return null;
        }

        if (rewritten)
            result.Warnings.Add($"第 {index} 条是 GitHub 网页地址（/blob/），已自动改用 raw 直链");

        return new ManifestItem { Url = url, Index = index };
    }

    private static string? JsonString(JsonElement obj, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (!property.NameEquals(key) && !string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    string? value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
        }

        return null;
    }

    private static bool? JsonBool(JsonElement obj, string key)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(property.Value.GetString(), out bool b) ? b : null,
                _ => null
            };
        }

        return null;
    }

    private static JsonElement? FindArray(JsonElement obj, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.Array) return property.Value;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ XML

    private static void ParseXml(string text, ManifestResult result)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.None);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("XML 解析失败：" + ex.Message, ex);
        }

        var root = doc.Root ?? throw new InvalidDataException("XML 内容为空");

        string? dir = XmlDirectoryName(root);
        if (!string.IsNullOrWhiteSpace(dir)) result.DirectoryName = SanitizeDirectoryName(dir);

        int index = 0;

        // 根节点本身就可能是一个字体节点：<font url="…"/>
        if (IsItemElement(root) && XmlUrl(root) is not null)
        {
            index++;
            var self = CreateXmlItem(root, index, result);
            if (self is not null) result.Items.Add(self);
        }

        foreach (var element in root.Descendants())
        {
            if (!IsItemElement(element)) continue;
            if (XmlUrl(element) is null) continue;

            // 形如 <item><url>…</url></item>：内层 <url> 不作为独立条目
            if (element.Parent is { } parent && IsItemElement(parent) && XmlUrl(parent) is not null) continue;

            index++;
            var item = CreateXmlItem(element, index, result);
            if (item is not null) result.Items.Add(item);
        }
    }

    private static bool IsItemElement(XElement element)
    {
        string name = LocalName(element);
        if (!ItemElementNames.Contains(name)) return false;

        // <url> 只有在没有子元素、且文本本身是地址时才算条目
        if (name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("link", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("href", StringComparison.OrdinalIgnoreCase))
        {
            return !element.HasElements && !string.IsNullOrWhiteSpace(element.Value);
        }

        return true;
    }

    private static ManifestItem? CreateXmlItem(XElement element, int index, ManifestResult result)
    {
        var item = FromUrl(XmlUrl(element), index, result);
        if (item is null) return null;

        return new ManifestItem
        {
            Url = item.Url,
            Name = FirstAttribute(element, NameKeys) ?? ChildValue(element, NameKeys),
            FileName = FirstAttribute(element, FileKeys) ?? ChildValue(element, FileKeys),
            Sha256 = FirstAttribute(element, new[] { "sha256", "checksum", "hash" })
                     ?? ChildValue(element, new[] { "sha256", "checksum", "hash" }),
            Overwrite = ParseBool(FirstAttribute(element, new[] { "overwrite" })) ?? false,
            Index = index
        };
    }

    private static string? XmlUrl(XElement element)
    {
        string? attributed = FirstAttribute(element, UrlKeys);
        if (attributed is not null) return attributed;

        string? child = ChildValue(element, new[] { "url", "href", "link", "path", "src" });
        if (child is not null) return child;

        if (ItemElementNames.Contains(LocalName(element)) && !element.HasElements)
        {
            string text = element.Value.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        return null;
    }

    private static string LocalName(XElement element) => element.Name.LocalName;

    private static string? FirstAttribute(XElement element, IEnumerable<string> keys)
    {
        foreach (var attribute in element.Attributes())
        {
            foreach (string key in keys)
            {
                if (!string.Equals(attribute.Name.LocalName, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(attribute.Value)) return attribute.Value.Trim();
            }
        }

        return null;
    }

    private static string? ChildValue(XElement element, IEnumerable<string> keys)
    {
        foreach (var child in element.Elements())
        {
            string name = LocalName(child);
            if (!keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))) continue;
            string value = child.Value.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }

        return null;
    }

    private static bool? ParseBool(string? value) =>
        bool.TryParse(value, out bool b) ? b : null;

    /// <summary>读取目录名的三种写法：根属性、<downloads dir="…"/>、<downloads>…</downloads>。</summary>
    private static string? XmlDirectoryName(XElement root)
    {
        string? attributed = FirstAttribute(root, DirKeys);
        if (attributed is not null) return attributed;

        foreach (var child in root.Elements())
        {
            if (!DirKeys.Any(k => string.Equals(k, LocalName(child), StringComparison.OrdinalIgnoreCase)))
                continue;

            string? value = FirstAttribute(child, new[] { "path", "name", "value", "dir" });
            if (value is not null) return value;

            // 兜底：任一带值的属性都认
            value = child.Attributes()
                         .Select(a => a.Value.Trim())
                         .FirstOrDefault(v => v.Length > 0);
            if (value is not null) return value;

            string text = child.Value.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        return null;
    }

    // ------------------------------------------------------------------ 公共工具

    /// <summary>把地址规范化为 http/https 绝对地址；无法识别时返回 null。</summary>
    public static string? NormalizeUrl(string? raw) => Normalize(raw).Url;

    /// <summary>
    /// 规范化地址。第二个返回值表示是否做过 GitHub 网页地址改写 ——
    /// github.com/…/blob/… 返回的是 HTML 页面而不是字体文件，直接下载必然失败，
    /// 这类地址是手写清单时最常见的错，顺手改写成 raw 直链。
    /// </summary>
    private static (string? Url, bool Rewritten) Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, false);

        string value = raw.Trim().Trim('"', '\'');

        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;

        // 省略协议但形如 host/path.ext 的写法
        if (!value.Contains("://", StringComparison.Ordinal) &&
            value.Contains('.') && value.Contains('/'))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return (null, false);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return (null, false);

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Contains("/blob/", StringComparison.Ordinal))
        {
            string path = uri.AbsolutePath.Replace("/blob/", "/", StringComparison.Ordinal);
            return ("https://raw.githubusercontent.com" + path + uri.Query, true);
        }

        return (uri.AbsoluteUri, false);
    }

    /// <summary>目录名只允许单层相对路径，禁止 . / .. 与绝对路径，避免写到清单目录之外。</summary>
    public static string SanitizeDirectoryName(string raw)
    {
        string name = raw.Trim().Replace('\\', '/').Trim('/');
        if (name.Length == 0) return DefaultDirectory;

        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => p != "." && p != "..")
                        .ToArray();
        if (parts.Length == 0) return DefaultDirectory;

        // 只取最后一段，拒绝多级目录
        string last = parts[^1];
        if (last.Length > 64) last = last[..64];
        foreach (char c in Path.GetInvalidFileNameChars()) last = last.Replace(c, '_');
        return string.IsNullOrWhiteSpace(last) ? DefaultDirectory : last;
    }
}
