using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SkyFont.Core;

/// <summary>系统已安装字体的定位。以注册表为准，因为部分已安装字体并不位于 Fonts 目录。</summary>
public static class InstalledFonts
{
    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    public static string WindowsFontsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    public static string UserFontsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Fonts");

    /// <summary>返回注册表中登记的全部字体文件绝对路径（去重）。</summary>
    public static List<string> GetRegisteredFiles()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(FontsKeyPath);
                if (key is null) continue;

                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string value || string.IsNullOrWhiteSpace(value)) continue;

                    // 值可能是纯文件名（相对 Fonts 目录），也可能是绝对路径（用户级安装）。
                    string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (string part in parts)
                    {
                        string candidate = Path.IsPathRooted(part)
                            ? part
                            : Path.Combine(WindowsFontsFolder, part);

                        try
                        {
                            candidate = Path.GetFullPath(candidate);
                        }
                        catch
                        {
                            continue;
                        }

                        if (File.Exists(candidate)) result.Add(candidate);
                    }
                }
            }
            catch (SecurityException)
            {
                // 无权限读取时忽略该 hive，改用目录扫描兜底。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result.ToList();
    }
}
