using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyFont.Core;

/// <summary>
/// 命令行自检：验证「解析 → 激活 → GDI 可见性 → 广播 → 反激活」整条链路。
/// 用法：SkyFont.exe --selftest [字体文件路径]
/// </summary>
public static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static int Run(string[] args)
    {
        AttachConsole(-1);

        StreamWriter? file = null;
        try
        {
            file = new StreamWriter(Path.Combine(Path.GetTempPath(), "SkyFont-selftest.log"), false, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // 日志文件仅用于诊断。
        }

        void W(string s)
        {
            try
            {
                Console.WriteLine(s);
                Console.Out.Flush();
            }
            catch
            {
                // 无控制台时忽略。
            }

            try
            {
                file?.WriteLine(s);
            }
            catch
            {
                // 忽略写入失败。
            }
        }

        try
        {
            W("SkyFont 自检");
            W("==================================================");

            var visibleBefore = EnumerateGdiFamilies();
            W($"[0] 当前会话 GDI 可见字族：{visibleBefore.Count:N0}");

            var (fontPath, decisive) = SelectTestFont(args, visibleBefore);
            if (fontPath is null)
            {
                W("[失败] 未找到可用于测试的字体文件");
                return 2;
            }

            W($"[1] 测试字体：{fontPath}");
            W($"    判定模式：{(decisive ? "字体当前不可见，可验证激活效果" : "字体已可见，仅验证 API 返回值")}");

            var parsed = FontParser.Parse(fontPath);
            if (!parsed.Ok)
            {
                W($"[失败] 解析失败：{parsed.Error}");
                return 2;
            }

            W($"    格式     : {parsed.Flavor}");
            W($"    大小     : {parsed.FileSize / 1024.0:F1} KB");
            W($"    字面数量 : {parsed.Faces.Count}");
            foreach (var face in parsed.Faces.Take(6))
            {
                W($"      #{face.FaceIndex} 家族={face.Family} | 样式={face.StyleName} | 字重={face.WeightClass} | 字形={face.GlyphCount} | unitsPerEm={face.UnitsPerEm} | 可变={face.IsVariable}");
            }

            var coverage = FontParser.ReadCoverage(fontPath, 0);
            W($"    字符覆盖 : {(coverage is null ? "无法解析" : coverage.Count.ToString("N0") + " 个码位")}");

            string family = parsed.Faces[0].Family;

            // ---- 激活 ----
            W("[2] 会话级激活 AddFontResourceEx");
            var activator = new FontActivator();
            var sw = Stopwatch.StartNew();
            var activation = activator.Activate(fontPath, broadcast: false);
            sw.Stop();
            W($"    结果 : {(activation.Success ? "成功" : "失败 - " + activation.Error)}（{sw.ElapsedMilliseconds} ms）");
            if (!activation.Success) return 3;

            var visibleAfter = EnumerateGdiFamilies();
            bool appeared = visibleAfter.Contains(family);
            W($"    激活后字族数 : {visibleAfter.Count:N0}");
            W($"    字族可见     : {(appeared ? "是" : "否")}");

            // ---- 广播 ----
            W("[3] 广播 WM_FONTCHANGE（EnumWindows + PostMessage）");
            sw.Restart();
            FontActivator.BroadcastFontChange();
            sw.Stop();
            W($"    结果 : 已投递（{sw.ElapsedMilliseconds} ms，非阻塞）");

            // ---- 反激活 ----
            W("[4] 反激活 RemoveFontResourceEx");
            sw.Restart();
            var deactivation = activator.Deactivate(fontPath, broadcast: false);
            sw.Stop();
            W($"    结果 : {(deactivation.Success ? "成功" : "失败 - " + deactivation.Error)}（{sw.ElapsedMilliseconds} ms）");

            var visibleFinal = EnumerateGdiFamilies();
            bool stillVisible = visibleFinal.Contains(family);
            W($"    字族残留 : {(stillVisible ? "是" : "否")}");

            bool activateCheck = decisive ? appeared : true;
            bool deactivateCheck = decisive ? !stillVisible : true;

            W("==================================================");
            W($"激活可见性检查 : {(activateCheck ? "PASS" : "FAIL")}");
            W($"反激活清理检查 : {(deactivateCheck ? "PASS" : "FAIL")}");
            bool pass = activation.Success && deactivation.Success && activateCheck && deactivateCheck;
            W(pass ? "自检结果 : PASS" : "自检结果 : FAIL");
            return pass ? 0 : 4;
        }
        catch (Exception ex)
        {
            W("[异常] " + ex);
            return 5;
        }
    }

    /// <summary>
    /// 把一个目录下的全部字体从当前会话的字体表中卸载。
    /// 用于清理异常退出后残留的临时激活（此时文件仍被 GDI 占用，无法删除）。
    /// </summary>
    public static int DeactivateFolder(string folder)
    {
        AttachConsole(-1);

        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"目录不存在：{folder}");
            return 2;
        }

        int removed = 0;
        int total = 0;

        foreach (string path in FontScanner.Enumerate(folder, true))
        {
            total++;
            if (NativeMethods.RemoveFontResourceEx(path, NativeMethods.FR_SESSION, IntPtr.Zero)) removed++;
        }

        FontActivator.BroadcastFontChange();

        string message = $"已从当前会话卸载 {removed} / {total} 个字体文件（目录：{folder}）";
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
            // 无控制台时忽略。
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "SkyFont-deactivate.log"), message + Environment.NewLine, Encoding.UTF8);
        return 0;
    }

    /// <summary>扫描目录并输出解析摘要，用于验证批量场景。</summary>
    public static int Dump(string folder)
    {
        AttachConsole(-1);
        var cache = new FontCache();
        var sw = Stopwatch.StartNew();
        var result = FontScanner.Scan(folder, true, folder, cache);
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine($"目录: {folder}");
        sb.AppendLine($"文件: {result.FilesScanned}  字面: {result.Entries.Count}  失败: {result.FilesFailed}  耗时: {sw.ElapsedMilliseconds} ms");
        sb.AppendLine();

        foreach (var e in result.Entries
                     .OrderBy(x => x.Family, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.WeightClass)
                     .Take(60))
        {
            sb.AppendLine($"{e.ShortFormat,-4} {e.DisplayName,-44} w={e.WeightClass,-3} glyphs={e.GlyphCount,-6} {e.FileName}");
        }

        string text = sb.ToString();
        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // 无控制台时仅写文件。
        }

        string logPath = Path.Combine(Path.GetTempPath(), "SkyFont-scan.log");
        File.WriteAllText(logPath, text, Encoding.UTF8);
        return 0;
    }

    /// <summary>
    /// 优先选择一个「未注册且当前 GDI 不可见」的字体，这样激活前后的差异才是可判定的。
    /// </summary>
    private static (string? Path, bool Decisive) SelectTestFont(string[] args, HashSet<string> visible)
    {
        if (args.Length > 1 && File.Exists(args[1]))
            return (args[1], true);

        var registered = new HashSet<string>(InstalledFonts.GetRegisteredFiles(), StringComparer.OrdinalIgnoreCase);
        string? fallback = null;

        var candidates = FontScanner.Enumerate(InstalledFonts.WindowsFontsFolder, false)
            .Concat(FontScanner.Enumerate(InstalledFonts.UserFontsFolder, true));

        foreach (string path in candidates)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (registered.Contains(full)) continue;
            fallback ??= full;

            var parsed = FontParser.Parse(full);
            if (!parsed.Ok) continue;

            if (!visible.Contains(parsed.Faces[0].Family))
                return (full, true);
        }

        return (fallback, false);
    }

    public static HashSet<string> EnumerateGdiFamilies()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr hdc = NativeMethods.CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return families;

        try
        {
            var lf = new NativeMethods.LOGFONT
            {
                lfCharSet = NativeMethods.DEFAULT_CHARSET,
                lfFaceName = ""
            };

            NativeMethods.EnumFontFamExProc callback = (ref NativeMethods.ENUMLOGFONTEX e, IntPtr _, uint _, IntPtr _) =>
            {
                if (!string.IsNullOrEmpty(e.elfLogFont.lfFaceName))
                    families.Add(e.elfLogFont.lfFaceName);
                return 1;
            };

            NativeMethods.EnumFontFamiliesEx(hdc, ref lf, callback, IntPtr.Zero, 0);
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }

        return families;
    }
}
