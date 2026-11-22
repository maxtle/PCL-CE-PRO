using System;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace PCL.Core.Utils.OS;

public static partial class WindowInterop
{
    // ReSharper disable InconsistentNaming UnusedMember.Local

    // DWM 外边缘结构定义
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int leftWidth, rightWidth, topHeight, bottomHeight; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    // Win32 矩形结构定义
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // MONITOR_DPI_TYPE enum
    private enum MONITOR_DPI_TYPE {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    // Get the primary monitor handle
    private const int MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static partial int GetDpiForMonitor(
        IntPtr hMonitor,
        MONITOR_DPI_TYPE dpiType,
        out uint dpiX,
        out uint dpiY
    );

    // ReSharper enable InconsistentNaming UnusedMember.Local

    /// <summary>
    /// 检测 DWM 组合是否可用
    /// </summary>
    public static bool IsCompositionEnabled()
    {
        var hResult = DwmIsCompositionEnabled(out var enabled);
        return hResult != 0 ? throw new Win32Exception(hResult, "Failed to check DWM status") : enabled;
    }

    /// <summary>
    /// 设置 DWM 窗口边框到客户区域的扩展大小
    /// </summary>
    public static void ExtendFrameIntoClientArea(
        IntPtr hWnd, int marginLeft, int marginTop, int marginRight, int marginBottom)
    {
        MARGINS margins = new()
        {
            leftWidth = marginLeft,
            rightWidth = marginRight,
            topHeight = marginTop,
            bottomHeight = marginBottom
        };
        if (!IsCompositionEnabled()) return;
        var hResult = DwmExtendFrameIntoClientArea(hWnd, ref margins);
        if (hResult != 0) throw new Win32Exception(hResult, "Failed to extend frame into client area");
    }

    /// <summary>
    /// See <see cref="ExtendFrameIntoClientArea(IntPtr, int, int, int, int)"/>
    /// </summary>
    public static void ExtendFrameIntoClientArea(IntPtr hWnd, int margin)
        => ExtendFrameIntoClientArea(hWnd, margin, margin, margin, margin);

    /// <summary>
    /// 获取 Win32 窗口矩形定义
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) GetWindowRectangle(IntPtr hWnd)
    {
        var hResult = GetWindowRect(hWnd, out var rect);
        return hResult ? (rect.left, rect.top, rect.right, rect.bottom)
            : throw new Win32Exception("Failed to get window rectangle");
    }

    /// <summary>
    /// 获取 Win32 窗口位置与大小
    /// </summary>
    public static (int X, int Y, int Width, int Height) ToWindowBounds(
        this (int Left, int Top, int Right, int Bottom) rect)
    {
        var (l, t, r, b) = rect;
        var x = l;
        var y = t;
        var width = r - l;
        var height = b - t;
        return (x, y, width, height);
    }

    /// <summary>
    /// 获取指定屏幕的系统 DPI
    /// </summary>
    /// <param name="hWnd">位于指定屏幕上的任意窗口句柄，默认指定主屏</param>
    public static int GetSystemDpi(IntPtr hWnd = 0) {
        // Get the monitor handle
        var hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTOPRIMARY);
        // 0 is S_OK
        var hr = GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _);
        if (hr == 0)
            return (int)dpiX;
        // fallback to default DPI (96)
        return 96;
    }

    #region 系统级亚克力透窗（真·毛玻璃）

    // SetWindowCompositionAttribute 相关定义，用于实现 Windows 10/11 的
    // Acrylic 亚克力透窗效果（窗口背景实时磨砂模糊后方画面）。

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
        ACCENT_INVALID_STATE = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowCompositionAttribute")]
    private static partial int SetWindowCompositionAttribute(
        IntPtr hWnd, ref WindowCompositionAttributeData data);

    // Windows 11 系统背景材质（Mica / Acrylic）
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private const int WCA_ACCENT_POLICY = 19;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE：Windows 11 22621+ 的系统背景材质属性。</summary>
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>DWMSBT_TRANSIENTWINDOW：亚克力（Acrylic）磨砂背景。</summary>
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    /// <summary>
    ///     当前系统是否支持 Windows 11 系统背景材质（22621+）。
    ///     必须使用 <see cref="NtInterop" /> 获取真实版本，Environment.OSVersion 在缺少
    ///     Win11 supportedOS 声明时会返回兼容用的假版本号。
    /// </summary>
    private static bool _SupportsSystemBackdrop =>
        NtInterop.GetCurrentOsVersion().Build >= 22621;

    /// <summary>
    ///     启用系统级亚克力透窗效果（真·毛玻璃），实时磨砂模糊窗口后方画面。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄</param>
    /// <param name="tintArgb">
    ///     亚克力叠加色调，格式为 0xAABBGGRR（AA 为不透明度）。传 null 使用默认半透明色调。
    /// </param>
    /// <returns>是否成功</returns>
    public static bool EnableAcrylic(IntPtr hWnd, int? tintArgb = null)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        // Windows 11 22621+：优先使用系统背景材质（DWMSBT_TRANSIENTWINDOW = Acrylic），
        // 由 DWM 直接渲染，磨砂模糊效果比旧版 Accent API 明显得多。
        if (_SupportsSystemBackdrop)
        {
            var backdrop = DWMSBT_TRANSIENTWINDOW;
            var hr = DwmSetWindowAttribute(
                hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (hr == 0)
                return true;
        }

        // 回退：Windows 10 / 旧版 Win11 使用 Accent 策略
        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            // 2 = 绘制所有边框
            AccentFlags = 2,
            // ABGR 格式：0xCC 不透明度（约 80%）+ 浅灰 AA AA AA，磨砂质感更明显
            GradientColor = tintArgb ?? unchecked((int)0xCCAAAAAA),
            AnimationId = 0
        };

        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            return SetWindowCompositionAttribute(hWnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    /// <summary>
    ///     移除系统级亚克力透窗效果，恢复普通窗口。
    /// </summary>
    public static bool DisableAcrylic(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        // 关闭 Windows 11 系统背景材质
        if (_SupportsSystemBackdrop)
        {
            var none = 1; // DWMSBT_NONE
            _ = DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_DISABLED,
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0
        };

        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            return SetWindowCompositionAttribute(hWnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    #endregion
}
