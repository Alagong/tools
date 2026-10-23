using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SkyFont.Core;

/// <summary>
/// 一个可展示、可激活的字体条目。注意：激活/反激活的作用对象是「字体文件」，
/// 因此同一 .ttc 中的多个 face 共享同一激活状态。
/// </summary>
public sealed class FontEntry : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isChecked;
    private bool _isInstalled;
    private bool _isInstalledConflict;
    private bool _isFavorite;
    private bool _isDuplicate;
    private FontFamily? _previewFamily;
    private HashSet<uint>? _coverage;
    private bool _coverageLoaded;

    public FontEntry(string filePath, string rootFolder, FontFaceInfo face, long fileSize, FontFlavor flavor)
    {
        FilePath = filePath;
        RootFolder = rootFolder;
        FaceIndex = face.FaceIndex;
        Family = face.Family;
        TypographicFamily = face.TypographicFamily;
        Subfamily = face.StyleName;
        FullName = face.FullName;
        PostScriptName = face.PostScriptName;
        Version = face.Version;
        Vendor = face.Vendor;
        WeightClass = face.WeightClass;
        WidthClass = face.WidthClass;
        IsItalic = face.Italic;
        IsBold = face.Bold || face.WeightClass >= 600;
        GlyphCount = face.GlyphCount;
        UnitsPerEm = face.UnitsPerEm;
        IsVariable = face.IsVariable;
        Flavor = flavor;
        FileSize = fileSize;
    }

    public string FilePath { get; }
    public string RootFolder { get; }
    public int FaceIndex { get; }

    public string Family { get; }
    public string TypographicFamily { get; }
    public string Subfamily { get; }
    public string FullName { get; }
    public string PostScriptName { get; }
    public string Version { get; }
    public string Vendor { get; }
    public int WeightClass { get; }
    public int WidthClass { get; }
    public bool IsItalic { get; }
    public bool IsBold { get; }
    public int GlyphCount { get; }
    public int UnitsPerEm { get; }
    public bool IsVariable { get; }
    public FontFlavor Flavor { get; }
    public long FileSize { get; }

    public string Key => $"{FilePath}#{FaceIndex}";
    public string FileName => Path.GetFileName(FilePath);
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";
    public string DisplayName => Subfamily.Equals("Regular", StringComparison.OrdinalIgnoreCase)
        ? Family
        : $"{Family} {Subfamily}";

    public string FormatLabel => Flavor switch
    {
        FontFlavor.TrueType => "TrueType",
        FontFlavor.OpenType => "OpenType",
        FontFlavor.TrueTypeCollection => "TTC",
        _ => "未知"
    };

    public string ShortFormat => Flavor switch
    {
        FontFlavor.TrueType => "TTF",
        FontFlavor.OpenType => "OTF",
        FontFlavor.TrueTypeCollection => "TTC",
        _ => "?"
    };

    public string WeightLabel => WeightClass switch
    {
        <= 150 => "Thin",
        <= 250 => "ExtraLight",
        <= 350 => "Light",
        <= 450 => "Regular",
        <= 550 => "Medium",
        <= 650 => "SemiBold",
        <= 750 => "Bold",
        <= 850 => "ExtraBold",
        _ => "Black"
    };

    public string FileSizeLabel => FileSize < 1024 * 1024
        ? $"{FileSize / 1024.0:F0} KB"
        : $"{FileSize / 1024.0 / 1024.0:F2} MB";

    public string FolderLabel => RootFolder;

    // ---------------------------------------------------------------- 状态

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>批量操作的勾选标记。仅界面状态，与选择、预览、激活都无关。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set => Set(ref _isInstalled, value);
    }

    /// <summary>是否可以通过 SkyFont 激活。永久安装的字体不需要也不允许再次激活。</summary>
    public bool CanActivate => !IsInstalled;

    /// <summary>该字体与系统中已安装的同名字族/样式冲突（激活时可能被系统字体掩盖）。</summary>
    public bool IsInstalledConflict
    {
        get => _isInstalledConflict;
        set => Set(ref _isInstalledConflict, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => Set(ref _isFavorite, value);
    }

    public bool IsDuplicate
    {
        get => _isDuplicate;
        set => Set(ref _isDuplicate, value);
    }

    public HashSet<uint>? Coverage
    {
        get
        {
            if (!_coverageLoaded)
            {
                _coverageLoaded = true;
                _coverage = FontParser.ReadCoverage(FilePath, FaceIndex);
            }

            return _coverage;
        }
    }

    /// <summary>按需构造预览用的 FontFamily：已安装字体按字族名直取，否则按文件 URI 取。</summary>
    public FontFamily PreviewFamily
    {
        get
        {
            if (_previewFamily is not null) return _previewFamily;
            try
            {
                if (IsInstalled)
                {
                    _previewFamily = new FontFamily(Family);
                }
                else
                {
                    string dir = Folder;
                    string baseUri = "file:///" + dir.Replace('\\', '/').TrimStart('/') + "/";
                    _previewFamily = new FontFamily(new Uri(baseUri, UriKind.Absolute), "./#" + Family);
                }
            }
            catch
            {
                _previewFamily = new FontFamily("Segoe UI");
            }

            return _previewFamily;
        }
    }

    public void InvalidatePreview() => _previewFamily = null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(IsInstalled))
        {
            OnPropertyChanged(nameof(PreviewFamily));
            OnPropertyChanged(nameof(CanActivate));
        }
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
