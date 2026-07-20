using System.Runtime.InteropServices;

namespace TracePrompt.Services;

/// <summary>
/// マウス座標やフォーカス中のウィンドウが、TracePrompt 自身のウィンドウ（メイン画面・別窓・
/// オーバーレイなど、プロセス内のあらゆる HWND）に属しているかどうかを判定します。
/// 記録開始ボタンのクリックなど、記録対象ではなく TracePrompt 自体の操作を記録から除外するために使います。
/// </summary>
internal static class OwnWindowHitTest
{
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>指定した画面座標（物理ピクセル）の直下にあるウィンドウが自プロセスのものなら true。</summary>
    public static bool IsPointOverOwnProcessWindow(int screenX, int screenY) =>
        IsOwnProcessWindow(WindowFromPoint(new Point { X = screenX, Y = screenY }));

    /// <summary>現在のフォアグラウンドウィンドウ（キー入力の受け手）が自プロセスのものなら true。</summary>
    public static bool IsForegroundWindowOwnProcess() =>
        IsOwnProcessWindow(GetForegroundWindow());

    private static bool IsOwnProcessWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
