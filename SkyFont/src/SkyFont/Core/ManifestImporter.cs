using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyFont.Core;

public sealed class ImportResult
{
    public required ManifestResult Manifest { get; init; }
    public required DownloadReport Report { get; init; }

    public string TargetDirectory => Manifest.TargetDirectory;

    /// <summary>下载成功 + 已存在的文件数，即该目录里"可用"的字体数量。</summary>
    public int UsableFiles => Report.Downloaded + Report.Skipped;
}

/// <summary>
/// 字体清单导入流程：解析清单 → 下载到程序目录的 downloads → 把该目录登记为字体库。
/// 界面与命令行共用这一条路径，避免两套逻辑跑偏。
/// </summary>
public static class ManifestImporter
{
    public static async Task<ImportResult> ImportAsync(
        string manifestPath,
        IProgress<(int Done, int Total, string Detail)>? progress = null,
        CancellationToken ct = default)
    {
        var manifest = FontManifest.Load(manifestPath);
        var report = await FontDownloader.DownloadAsync(
            manifest.Items, manifest.TargetDirectory, progress, ct).ConfigureAwait(false);

        return new ImportResult { Manifest = manifest, Report = report };
    }

    // ------------------------------------------------------------------ 命令行

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// 无界面导入：SkyFont.exe --import &lt;清单&gt;
    /// 结果同时写到 %TEMP%\SkyFont-import.log，便于在无控制台环境（如双击 exe）下排查。
    /// </summary>
    public static int RunCli(string manifestPath)
    {
        AttachConsole(-1);

        StreamWriter? log = null;
        try
        {
            log = new StreamWriter(Path.Combine(Path.GetTempPath(), "SkyFont-import.log"), false, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // 日志仅用于诊断。
        }

        void W(string line)
        {
            Console.WriteLine(line);
            log?.WriteLine(line);
        }

        try
        {
            W($"清单 : {manifestPath}");

            // 放到线程池执行：界面线程此时可能已安装 Dispatcher 同步上下文，
            // 直接 GetResult() 会自我等待而死锁。
            var result = Task.Run(() => ImportAsync(manifestPath)).GetAwaiter().GetResult();
            var report = result.Report;

            W($"目录 : {result.TargetDirectory}");
            W($"条目 : {result.Manifest.Items.Count}    下载 {report.Downloaded}    跳过 {report.Skipped}    失败 {report.Failed}");
            W($"耗时 : {report.Elapsed.TotalSeconds:F1} s");

            foreach (string warning in result.Manifest.Warnings)
                W("提示 : " + warning);

            foreach (var outcome in report.Outcomes.Where(o => o.Status == DownloadStatus.Failed))
                W($"失败 : {outcome.Url}  →  {outcome.Message}");

            if (result.UsableFiles > 0)
            {
                var settings = AppSettings.Load();
                if (!settings.FontFolders.Contains(result.TargetDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    settings.FontFolders.Add(result.TargetDirectory);
                    settings.Save();
                    W($"已加入字体库：{result.TargetDirectory}");
                }
                else
                {
                    W("该目录此前已在字体库中");
                }
            }
            else
            {
                W("没有任何字体可用，未修改字体库配置");
            }

            W(report.Failed == 0 ? "结果 : PASS" : "结果 : 部分失败");
            return report.Failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            W("错误 : " + ex.Message);
            return 1;
        }
        finally
        {
            log?.Dispose();
        }
    }
}
