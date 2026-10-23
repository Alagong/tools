using System.IO;
using System.Windows;
using SkyFont.Core;

namespace SkyFont;

public partial class App : Application
{
    private FontActivator? _activator;

    public FontActivator Activator => _activator ??= new FontActivator();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception) LogCrash(exception);
        };

        // 命令行模式：自检 / 目录导出，不进入 UI。
        if (e.Args.Length > 0)
        {
            switch (e.Args[0].ToLowerInvariant())
            {
                case "--selftest":
                case "-t":
                    Shutdown(SelfTest.Run(e.Args));
                    return;
                case "--scan":
                case "-s":
                    Shutdown(e.Args.Length > 1 ? SelfTest.Dump(e.Args[1]) : 2);
                    return;
                case "--deactivate":
                case "-d":
                    Shutdown(e.Args.Length > 1 ? SelfTest.DeactivateFolder(e.Args[1]) : 2);
                    return;
                case "--makeicon":
                    if (e.Args.Length > 1)
                    {
                        IconMaker.Create(e.Args[1]);
                        Shutdown(0);
                    }
                    else
                    {
                        Shutdown(2);
                    }

                    return;
                case "--help":
                case "-h":
                    MessageBox.Show(
                        "SkyFont 命令行参数：\n\n" +
                        "  --selftest [字体路径]   运行激活/反激活自检\n" +
                        "  --scan <目录>           扫描目录并输出解析结果\n" +
                        "  --shot <图片路径>       渲染主界面截图（调试用）\n" +
                        "  --help                  显示本帮助",
                        "SkyFont", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(0);
                    return;
            }
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0 && e.Args[0].Equals("--shot", StringComparison.OrdinalIgnoreCase))
            _ = window.RunScreenshotAsync(
                e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "skyfont-shot.png"),
                e.Args.Length > 2 ? e.Args[2] : "preview",
                e.Args.Length > 3 ? e.Args[3] : null,
                e.Args.Length > 4 ? int.Parse(e.Args[4]) : 1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(
                Path.Combine(AppSettings.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 记录失败不应引发二次异常。
        }
    }
}
