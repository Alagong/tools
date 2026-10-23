using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyFont.Core;

public sealed class AppSettings
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyFont");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string CachePath => Path.Combine(DataDir, "fontindex.json");
    public static string ActivePath => Path.Combine(DataDir, "active.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- 字体来源 ----
    public List<string> FontFolders { get; set; } = new();
    public bool RecursiveScan { get; set; } = true;

    // ---- 预览 ----
    public string SampleText { get; set; } = "The quick brown fox jumps over the lazy dog.\n敏捷的棕狐狸跳过懒狗。\n0123456789 ©®™";
    public double PreviewSize { get; set; } = 32;
    public bool PreviewBold { get; set; }
    public bool PreviewItalic { get; set; }
    public bool PreviewWrap { get; set; } = true;

    // ---- 行为 ----
    /// <summary>退出时移除本次会话的全部临时激活，保持系统字体表干净。</summary>
    public bool DeactivateOnExit { get; set; } = true;

    // ---- 视图 ----
    public string SortMode { get; set; } = "Family";
    public string LastSource { get; set; } = "all";
    public List<string> Favorites { get; set; } = new();
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }
    public double PreviewPaneWidth { get; set; } = 440;

    /// <summary>上次运行结束时的激活列表，用于异常退出后的状态恢复/清理。</summary>
    public List<string> LastActivePaths { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.FontFolders ??= new List<string>();
                    loaded.Favorites ??= new List<string>();
                    loaded.LastActivePaths ??= new List<string>();
                    if (loaded.PreviewSize < 6 || loaded.PreviewSize > 400) loaded.PreviewSize = 32;
                    if (loaded.WindowWidth < 800) loaded.WindowWidth = 1360;
                    if (loaded.WindowHeight < 500) loaded.WindowHeight = 860;
                    if (loaded.PreviewPaneWidth < 260 || loaded.PreviewPaneWidth > 1200) loaded.PreviewPaneWidth = 440;
                    if (string.IsNullOrWhiteSpace(loaded.SampleText)) loaded.SampleText = "The quick brown fox";
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值。
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 写入失败不阻塞退出流程。
        }
    }
}
