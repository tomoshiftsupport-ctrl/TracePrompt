using System.Runtime.InteropServices;
using TracePrompt.Models;
using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.Services;

/// <summary>
/// 接続中のモニター一覧を取得するサービスです。「全画面」モードのモニター選択に使います。
/// </summary>
public sealed class MonitorEnumerationService
{
    private const uint MonitorInfoFPrimary = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref Rect lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoEx lpmi);

    /// <summary>
    /// 接続中のモニターを、メイン→左から右の順で返します。
    /// </summary>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var results = new List<MonitorInfo>();

        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint hdcMonitor, ref Rect lprcMonitor, nint dwData) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(hMonitor, ref info))
            {
                return true;
            }

            int width = info.rcMonitor.Right - info.rcMonitor.Left;
            int height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            if (width <= 0 || height <= 0)
            {
                return true;
            }

            bool isPrimary = (info.dwFlags & MonitorInfoFPrimary) != 0;
            var bounds = new WindowBounds(info.rcMonitor.Left, info.rcMonitor.Top, width, height);
            string label = $"{info.szDevice} ({width}×{height}){(isPrimary ? Loc.Instance.Get("Monitor_PrimarySuffix") : string.Empty)}";
            results.Add(new MonitorInfo(info.szDevice, label, bounds, isPrimary));
            return true;
        }, nint.Zero);

        return results
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ToList();
    }
}
