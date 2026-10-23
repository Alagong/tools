using System.Runtime.InteropServices;

namespace SkyFont.Core;

/// <summary>
/// Win32 互操作声明。仅使用 GDI / User32 的稳定导出函数，不依赖任何第三方包。
/// </summary>
internal static class NativeMethods
{
    // ---- AddFontResourceEx flags -------------------------------------------------
    /// <summary>字体仅对本进程可见。</summary>
    public const uint FR_PRIVATE = 0x10;
    /// <summary>字体可被使用，但不出现在枚举列表中。</summary>
    public const uint FR_NOT_ENUM = 0x20;
    /// <summary>会话级激活：本会话内所有进程可用，重启后失效。</summary>
    public const uint FR_SESSION = 0x00;

    [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    // ---- 字体变更广播 ------------------------------------------------------------
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint WM_FONTCHANGE = 0x001D;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 同步广播，仅在明确需要等待处理完成时使用。
    /// 注意：对 HWND_BROADCAST 调用此函数时，若桌面存在无响应窗口可能长时间阻塞，主流程不应依赖它。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // ---- GDI 字体枚举（用于验证激活结果）-----------------------------------------
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    public delegate int EnumFontFamExProc(ref ENUMLOGFONTEX lpelfe, IntPtr lpntme, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", EntryPoint = "EnumFontFamiliesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT lpLogfont, EnumFontFamExProc lpEnumFontFamExProc, IntPtr lParam, uint dwFlags);

    public const byte DEFAULT_CHARSET = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ENUMLOGFONTEX
    {
        public LOGFONT elfLogFont;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string elfFullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string elfStyle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string elfScript;
    }
}
