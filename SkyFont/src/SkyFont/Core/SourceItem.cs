using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyFont.Core;

/// <summary>左侧「字体来源」列表项。</summary>
public sealed class SourceItem : INotifyPropertyChanged
{
    private int _count;

    public SourceItem(string kind, string name, string icon, string path = "")
    {
        Kind = kind;
        Name = name;
        Icon = icon;
        Path = path;
    }

    /// <summary>分组标题（不可选中、不参与统计）。</summary>
    public static SourceItem Header(string name) => new("header", name, "") { IsHeader = true };

    /// <summary>all / active / installed / favorites / inactive / folder / header</summary>
    public string Kind { get; }

    public bool IsHeader { get; private init; }
    public string Name { get; }
    public string Icon { get; }
    public string Path { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public string CountText => _count.ToString("N0");

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>字符映射表中的一个格子。</summary>
public sealed class GlyphCell
{
    public GlyphCell(uint code)
    {
        Value = code;
        Glyph = char.ConvertFromUtf32((int)code);
        Hex = code <= 0xFFFF ? code.ToString("X4") : code.ToString("X5");
    }

    public uint Value { get; }
    public string Glyph { get; }
    public string Hex { get; }
    public string Code => $"U+{Hex}";
}
