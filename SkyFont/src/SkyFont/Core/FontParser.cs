using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SkyFont.Core;

public enum FontFlavor
{
    TrueType,
    OpenType,
    TrueTypeCollection,
    Unsupported
}

/// <summary>单个字面（face）的元数据。一个 .ttc 文件内可包含多个 face。</summary>
public sealed class FontFaceInfo
{
    public int FaceIndex { get; set; }
    public string Family { get; set; } = "";
    public string TypographicFamily { get; set; } = "";
    public string Subfamily { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PostScriptName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Vendor { get; set; } = "";
    public int WeightClass { get; set; } = 400;
    public int WidthClass { get; set; } = 5;
    public bool Italic { get; set; }
    public bool Bold { get; set; }
    public int GlyphCount { get; set; }
    public int UnitsPerEm { get; set; } = 1000;
    public bool IsVariable { get; set; }
    public FontFlavor Flavor { get; set; }

    public string StyleName => string.IsNullOrWhiteSpace(Subfamily) ? "Regular" : Subfamily;
}

public sealed class FontParseResult
{
    public FontFlavor Flavor { get; set; } = FontFlavor.Unsupported;
    public List<FontFaceInfo> Faces { get; } = new();
    public string? Error { get; set; }
    public long FileSize { get; set; }
    public bool Ok => Error is null && Faces.Count > 0;
}

/// <summary>
/// 轻量级 sfnt（TrueType / OpenType / TTC）解析器。
/// 只按需读取表目录与小表，不整文件载入，因此可在数千个字体上做快速扫描。
/// </summary>
public static class FontParser
{
    private const int MaxFaces = 512;
    private const int MaxTables = 1024;

    public static FontParseResult Parse(string path)
    {
        var result = new FontParseResult();
        try
        {
            using var fs = OpenRead(path);
            result.FileSize = fs.Length;
            if (fs.Length < 12)
            {
                result.Error = "文件过小，不是有效的字体文件";
                return result;
            }

            uint tag = ReadU32(fs, 0);
            long[] faceOffsets;
            FontFlavor containerFlavor;

            if (tag == TagOf("ttcf"))
            {
                uint numFonts = ReadU32(fs, 8);
                if (numFonts == 0 || numFonts > MaxFaces)
                {
                    result.Error = "字体集合的 face 数量异常";
                    return result;
                }

                faceOffsets = new long[numFonts];
                for (int i = 0; i < numFonts; i++)
                    faceOffsets[i] = ReadU32(fs, 12 + i * 4L);

                containerFlavor = FontFlavor.TrueTypeCollection;
            }
            else if (tag == TagOf("wOFF") || tag == TagOf("wOF2"))
            {
                result.Error = "WOFF / WOFF2 网页字体不受支持";
                return result;
            }
            else
            {
                containerFlavor = FlavorFromTag(tag);
                if (containerFlavor == FontFlavor.Unsupported)
                {
                    result.Error = $"无法识别的 sfnt 标识 0x{tag:X8}";
                    return result;
                }

                faceOffsets = new[] { 0L };
            }

            result.Flavor = containerFlavor;

            for (int i = 0; i < faceOffsets.Length; i++)
            {
                var face = ParseFace(fs, faceOffsets[i], i);
                if (face is null) continue;
                face.Flavor = containerFlavor == FontFlavor.TrueTypeCollection
                    ? FlavorFromTag(ReadU32(fs, faceOffsets[i]))
                    : containerFlavor;
                result.Faces.Add(face);
            }

            if (result.Faces.Count == 0 && result.Error is null)
                result.Error = "未能解析出任何字面（name 表缺失）";
        }
        catch (UnauthorizedAccessException)
        {
            result.Error = "没有读取权限";
        }
        catch (IOException ex)
        {
            result.Error = $"读取失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            result.Error = $"解析异常：{ex.Message}";
        }

        return result;
    }

    /// <summary>读取单个 face 的字符覆盖集合（按码位）。返回 null 表示无法解析。</summary>
    public static HashSet<uint>? ReadCoverage(string path, int faceIndex)
    {
        try
        {
            using var fs = OpenRead(path);
            long faceOffset = 0;
            if (ReadU32(fs, 0) == TagOf("ttcf"))
            {
                uint numFonts = ReadU32(fs, 8);
                if (faceIndex < 0 || faceIndex >= numFonts) return null;
                faceOffset = ReadU32(fs, 12 + faceIndex * 4L);
            }

            var tables = ReadTableDirectory(fs, faceOffset);
            if (!tables.TryGetValue("cmap", out var cmapLoc)) return null;

            byte[] cmap = ReadTable(fs, cmapLoc.Offset, cmapLoc.Length, 1 << 20);
            if (cmap.Length < 4) return null;

            int numSubtables = BE16(cmap, 2);
            if (numSubtables <= 0 || numSubtables > 64) return null;

            int bestScore = int.MaxValue;
            int bestOffset = -1;

            for (int i = 0; i < numSubtables; i++)
            {
                int p = 4 + i * 8;
                if (p + 8 > cmap.Length) break;
                int platformId = BE16(cmap, p);
                int encodingId = BE16(cmap, p + 2);
                int offset = (int)BE32(cmap, p + 4);
                if (offset <= 0 || offset + 2 > cmap.Length) continue;
                int format = BE16(cmap, offset);

                int score = (platformId, encodingId, format) switch
                {
                    (3, 10, 12) => 0,
                    (0, _, 12) => 1,
                    (3, 10, 13) => 2,
                    (3, 1, 4) => 3,
                    (0, _, 4) => 4,
                    (3, 0, 4) => 5,
                    _ => format is 4 or 12 ? 6 : 99
                };

                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                }
            }

            if (bestOffset < 0) return null;
            int fmt = BE16(cmap, bestOffset);
            return fmt switch
            {
                4 => ReadFormat4(cmap, bestOffset),
                12 => ReadFormat12(cmap, bestOffset),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ face 解析

    private static FontFaceInfo? ParseFace(FileStream fs, long faceOffset, int faceIndex)
    {
        var tables = ReadTableDirectory(fs, faceOffset);
        if (tables.Count == 0) return null;

        var face = new FontFaceInfo { FaceIndex = faceIndex };

        // name
        if (tables.TryGetValue("name", out var nameLoc))
        {
            byte[] nameTable = ReadTable(fs, nameLoc.Offset, nameLoc.Length, 1 << 20);
            var names = ParseNameTable(nameTable);
            face.Family = FirstNonEmpty(names, 16, 1, 21, 4);
            face.TypographicFamily = names.TryGetValue(16, out var tf) ? tf : "";
            face.Subfamily = FirstNonEmpty(names, 17, 2, 22);
            face.FullName = names.TryGetValue(4, out var fn) ? fn : "";
            face.PostScriptName = names.TryGetValue(6, out var ps) ? ps : "";
            face.Version = names.TryGetValue(5, out var ver) ? ver : "";
        }

        // OS/2
        if (tables.TryGetValue("OS/2", out var os2Loc))
        {
            byte[] os2 = ReadTable(fs, os2Loc.Offset, os2Loc.Length, 4096);
            if (os2.Length >= 64)
            {
                int w = BE16(os2, 4);
                int wd = BE16(os2, 6);
                if (w is > 0 and <= 1000) face.WeightClass = w;
                if (wd is > 0 and <= 9) face.WidthClass = wd;
                int fsSelection = BE16(os2, 62);
                face.Italic = (fsSelection & 0x0001) != 0;
                face.Bold = (fsSelection & 0x0020) != 0;
                if (os2.Length >= 62)
                    face.Vendor = Encoding.ASCII.GetString(os2, 58, 4).TrimEnd('\0', ' ');
            }
        }

        // head
        if (tables.TryGetValue("head", out var headLoc))
        {
            byte[] head = ReadTable(fs, headLoc.Offset, headLoc.Length, 256);
            if (head.Length >= 54)
            {
                int upem = BE16(head, 18);
                if (upem is > 0 and <= 16384) face.UnitsPerEm = upem;
                int macStyle = BE16(head, 44);
                if ((macStyle & 0x0001) != 0) face.Bold = true;
                if ((macStyle & 0x0002) != 0) face.Italic = true;
            }
        }

        // maxp
        if (tables.TryGetValue("maxp", out var maxpLoc))
        {
            byte[] maxp = ReadTable(fs, maxpLoc.Offset, maxpLoc.Length, 64);
            if (maxp.Length >= 6) face.GlyphCount = BE16(maxp, 4);
        }

        // fvar → 可变字体
        face.IsVariable = tables.ContainsKey("fvar");

        // 若 OS/2 缺失，从字面名推断
        if (face.WeightClass == 400 && face.Subfamily.Length > 0)
        {
            string s = face.Subfamily.ToLowerInvariant();
            if (s.Contains("thin")) face.WeightClass = 100;
            else if (s.Contains("extralight") || s.Contains("ultralight")) face.WeightClass = 200;
            else if (s.Contains("light")) face.WeightClass = 300;
            else if (s.Contains("medium")) face.WeightClass = 500;
            else if (s.Contains("semibold") || s.Contains("demibold")) face.WeightClass = 600;
            else if (s.Contains("extrabold") || s.Contains("ultrabold")) face.WeightClass = 800;
            else if (s.Contains("black") || s.Contains("heavy")) face.WeightClass = 900;
            else if (s.Contains("bold")) face.WeightClass = 700;
        }

        if (string.IsNullOrWhiteSpace(face.Family))
            face.Family = Path.GetFileNameWithoutExtension(face.FullName.Length > 0 ? face.FullName : "Unknown");
        if (string.IsNullOrWhiteSpace(face.Subfamily))
            face.Subfamily = face.Bold && face.Italic ? "Bold Italic" : face.Bold ? "Bold" : "Regular";
        if (string.IsNullOrWhiteSpace(face.FullName))
            face.FullName = face.Family + " " + face.Subfamily;

        return face;
    }

    private static Dictionary<string, (long Offset, long Length)> ReadTableDirectory(FileStream fs, long fontOffset)
    {
        var tables = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (fontOffset < 0 || fontOffset + 12 > fs.Length) return tables;

        ushort numTables = ReadU16(fs, fontOffset + 4);
        if (numTables == 0 || numTables > MaxTables) return tables;

        long dirStart = fontOffset + 12;
        if (dirStart + numTables * 16L > fs.Length) return tables;

        byte[] dir = new byte[numTables * 16];
        fs.Position = dirStart;
        fs.ReadExactly(dir);

        for (int i = 0; i < numTables; i++)
        {
            int p = i * 16;
            string tag = Encoding.ASCII.GetString(dir, p, 4);
            long offset = BE32(dir, p + 8);
            long length = BE32(dir, p + 12);
            if (offset < 0 || offset >= fs.Length) continue;
            tables[tag] = (offset, Math.Min(length, fs.Length - offset));
        }

        return tables;
    }

    private static Dictionary<int, string> ParseNameTable(byte[] name)
    {
        var result = new Dictionary<int, string>();
        if (name.Length < 6) return result;

        int count = BE16(name, 2);
        int stringOffset = BE16(name, 4);
        if (count <= 0 || count > 4096) return result;

        var bestScore = new Dictionary<int, int>();

        for (int i = 0; i < count; i++)
        {
            int p = 6 + i * 12;
            if (p + 12 > name.Length) break;

            int platformId = BE16(name, p);
            int languageId = BE16(name, p + 4);
            int nameId = BE16(name, p + 6);
            int length = BE16(name, p + 8);
            int offset = BE16(name, p + 10);

            if (nameId is < 1 or > 25 || length <= 0) continue;

            int start = stringOffset + offset;
            if (start < 0 || start + length > name.Length) continue;

            int score = platformId switch { 3 => 0, 0 => 1, 1 => 2, _ => 5 };
            if (languageId != 0x0409 && languageId != 0) score += 10;

            if (bestScore.TryGetValue(nameId, out int prev) && prev <= score) continue;

            string text = DecodeName(name, start, length, platformId);
            if (string.IsNullOrWhiteSpace(text)) continue;

            bestScore[nameId] = score;
            result[nameId] = text.Replace('\0', ' ').Trim();
        }

        return result;
    }

    private static string DecodeName(byte[] buffer, int start, int length, int platformId)
    {
        try
        {
            if (platformId is 3 or 0)
                return Encoding.BigEndianUnicode.GetString(buffer, start, length & ~1);
            return Encoding.Latin1.GetString(buffer, start, length);
        }
        catch
        {
            return "";
        }
    }

    private static string FirstNonEmpty(Dictionary<int, string> names, params int[] ids)
    {
        foreach (int id in ids)
            if (names.TryGetValue(id, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return "";
    }

    // ------------------------------------------------------------------ cmap 覆盖

    private static HashSet<uint> ReadFormat4(byte[] cmap, int offset)
    {
        var set = new HashSet<uint>();
        int segCountX2 = BE16(cmap, offset + 6);
        if (segCountX2 <= 0 || segCountX2 > 0xFFFE) return set;
        int segCount = segCountX2 / 2;

        int endCodes = offset + 14;
        int startCodes = endCodes + segCountX2 + 2;

        for (int i = 0; i < segCount; i++)
        {
            int endP = endCodes + i * 2;
            int startP = startCodes + i * 2;
            if (endP + 2 > cmap.Length || startP + 2 > cmap.Length) break;

            uint start = (uint)BE16(cmap, startP);
            uint end = (uint)BE16(cmap, endP);
            if (end == 0xFFFF && start == 0xFFFF) continue;
            if (end < start) continue;
            for (uint c = start; c <= end && c < 0xFFFF; c++) set.Add(c);
        }

        return set;
    }

    private static HashSet<uint> ReadFormat12(byte[] cmap, int offset)
    {
        var set = new HashSet<uint>();
        uint nGroups = BE32(cmap, offset + 12);
        if (nGroups == 0 || nGroups > 200000) return set;

        for (uint i = 0; i < nGroups; i++)
        {
            int p = offset + 16 + (int)(i * 12);
            if (p + 12 > cmap.Length) break;
            uint start = BE32(cmap, p);
            uint end = BE32(cmap, p + 4);
            if (end < start || end > 0x10FFFF) continue;
            if (end - start > 200000) continue;
            for (uint c = start; c <= end; c++) set.Add(c);
        }

        return set;
    }

    // ------------------------------------------------------------------ 基础 IO

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.RandomAccess);

    private static byte[] ReadTable(FileStream fs, long offset, long length, int maxLength)
    {
        long len = Math.Min(Math.Min(length, maxLength), fs.Length - offset);
        if (len <= 0) return Array.Empty<byte>();
        var buffer = new byte[len];
        fs.Position = offset;
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static uint ReadU32(FileStream fs, long offset)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = offset;
        fs.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static ushort ReadU16(FileStream fs, long offset)
    {
        Span<byte> b = stackalloc byte[2];
        fs.Position = offset;
        fs.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static int BE16(byte[] b, int p) => p + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2)) : 0;

    private static uint BE32(byte[] b, int p) => p + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4)) : 0u;

    private static uint TagOf(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static FontFlavor FlavorFromTag(uint tag)
    {
        if (tag == 0x00010000 || tag == TagOf("true") || tag == TagOf("typ1")) return FontFlavor.TrueType;
        if (tag == TagOf("OTTO")) return FontFlavor.OpenType;
        return FontFlavor.Unsupported;
    }
}
