using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyFont.Core;

public readonly record struct ActivationResult(bool Success, string? Error)
{
    public static ActivationResult Ok() => new(true, null);
    public static ActivationResult Fail(string error) => new(false, error);
}

/// <summary>
/// 字体激活引擎。
///
/// 激活使用 GDI 的 <c>AddFontResourceEx(path, 0, NULL)</c>：把字体临时加入**当前登录会话**的字体表，
/// 本会话内的所有进程（Office、Photoshop、Word…）立即可用，但不写入注册表、不复制文件、重启即失效。
/// 这正是 NexusFont 的「临时激活」语义，也是无需管理员权限即可完成的原因。
///
/// 反激活使用 <c>RemoveFontResourceEx</c>，并在每次变更后广播 <c>WM_FONTCHANGE</c>，
/// 通知已运行的应用刷新字体列表。
/// </summary>
public sealed class FontActivator
{
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);

    // 批量操作在后台线程执行，同时界面可能读取状态，所有状态变更都走这把锁。
    private readonly object _gate = new();

    public IReadOnlyCollection<string> ActivePaths
    {
        get
        {
            lock (_gate) return _active.Keys.ToList();
        }
    }

    public bool IsActive(string path)
    {
        string full = Normalize(path);
        lock (_gate) return _active.ContainsKey(full);
    }

    public ActivationResult Activate(string path, bool broadcast = true)
    {
        string full = Normalize(path);
        ActivationResult result;

        lock (_gate)
        {
            if (_active.TryGetValue(full, out int count))
            {
                _active[full] = count + 1;
                return ActivationResult.Ok();
            }

            if (!File.Exists(full))
                return ActivationResult.Fail("字体文件不存在");

            int added = NativeMethods.AddFontResourceEx(full, NativeMethods.FR_SESSION, IntPtr.Zero);
            if (added <= 0)
            {
                int err = Marshal.GetLastWin32Error();
                string message = err != 0 ? new Win32Exception(err).Message : "系统拒绝加载该字体";
                return ActivationResult.Fail($"激活失败：{message}");
            }

            _active[full] = 1;
            result = ActivationResult.Ok();
        }

        if (broadcast) BroadcastFontChange();
        return result;
    }

    public ActivationResult Deactivate(string path, bool broadcast = true)
    {
        string full = Normalize(path);

        lock (_gate)
        {
            if (_active.TryGetValue(full, out int count) && count > 1)
            {
                _active[full] = count - 1;
                return ActivationResult.Ok();
            }

            NativeMethods.RemoveFontResourceEx(full, NativeMethods.FR_SESSION, IntPtr.Zero);
            _active.Remove(full);
        }

        // 返回值 false 通常表示该字体本来就未被临时激活（例如已永久安装），不视为致命错误。
        if (broadcast) BroadcastFontChange();
        return ActivationResult.Ok();
    }

    /// <summary>强制清除全部临时激活（用于退出清理）。</summary>
    public int DeactivateAll()
    {
        int removed = 0;

        lock (_gate)
        {
            foreach (string path in _active.Keys.ToList())
            {
                try
                {
                    if (NativeMethods.RemoveFontResourceEx(path, NativeMethods.FR_SESSION, IntPtr.Zero)) removed++;
                }
                catch
                {
                    // 单个失败不影响其余清理。
                }
            }

            _active.Clear();
        }

        if (removed > 0) BroadcastFontChange();
        return removed;
    }

    /// <summary>
    /// 通知所有顶层窗口字体表已变化。
    ///
    /// 使用 EnumWindows + PostMessage 逐个投递 <c>WM_FONTCHANGE</c>，而不是
    /// <c>SendMessageTimeout(HWND_BROADCAST)</c>：后者是同步调用，桌面上只要有一个进程的窗口
    /// 长时间不处理消息，调用方就会被拖住（实测可无限阻塞）。投递方式不会阻塞调用线程。
    /// </summary>
    public static void BroadcastFontChange()
    {
        try
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                NativeMethods.PostMessage(hWnd, NativeMethods.WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 广播失败不影响激活结果本身。
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
