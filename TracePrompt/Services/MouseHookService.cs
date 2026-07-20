using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TracePrompt.Services;

/// <summary>
/// グローバルなマウス操作を監視するサービスです（WH_MOUSE_LL）。
/// 左/右ボタン、移動（左ドラッグ追跡時）、ホイールを通知します。
/// </summary>
public sealed class MouseHookService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    private LowLevelMouseProc? _proc;
    private nint _hookId = nint.Zero;
    private bool _disposed;
    private bool _leftButtonDown;

    /// <summary>左ボタン押し下げ（画面座標）。</summary>
    public event Action<int, int>? LeftButtonDown;

    /// <summary>左ボタン解放（画面座標）。</summary>
    public event Action<int, int>? LeftButtonUp;

    /// <summary>右ボタン押し下げ（画面座標）。</summary>
    public event Action<int, int>? RightButtonDown;

    /// <summary>右ボタン解放（画面座標）。</summary>
    public event Action<int, int>? RightButtonUp;

    /// <summary>
    /// 左ボタン押下中の移動のみ通知します（ドラッグ判定用）。
    /// 無制限保存を避けるため、押下中以外は送出しません。
    /// </summary>
    public event Action<int, int>? MouseMoveWhileLeftDown;

    /// <summary>ホイール（画面座標, delta: 上向き正）。</summary>
    public event Action<int, int, int>? MouseWheel;

    public bool IsRunning => _hookId != nint.Zero;

    /// <summary>OS のダブルクリック判定時間（ミリ秒）です。</summary>
    public static int GetOsDoubleClickTimeMs() => (int)GetDoubleClickTime();

    /// <summary>OS のダブルクリック許容距離（幅・高さ）です。</summary>
    public static (int Width, int Height) GetOsDoubleClickSize()
        => (GetSystemMetrics(SmCxDoubleClk), GetSystemMetrics(SmCyDoubleClk));

    /// <summary>OS のドラッグ開始しきい値です。</summary>
    public static (int Width, int Height) GetOsDragThreshold()
        => (GetSystemMetrics(SmCxDrag), GetSystemMetrics(SmCyDrag));

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hookId != nint.Zero)
        {
            return;
        }

        _proc = HookCallback;

        nint moduleHandle = nint.Zero;
        using (Process currentProcess = Process.GetCurrentProcess())
        using (ProcessModule? mainModule = currentProcess.MainModule)
        {
            if (mainModule?.ModuleName is not null)
            {
                moduleHandle = GetModuleHandle(mainModule.ModuleName);
            }
        }

        _hookId = SetWindowsHookEx(WhMouseLl, _proc, moduleHandle, 0);
        if (_hookId == nint.Zero)
        {
            _proc = null;
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"マウスフックの開始に失敗しました。Win32 エラー: {error}");
        }
    }

    public void Stop()
    {
        if (_hookId == nint.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
        _proc = null;
        _leftButtonDown = false;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                int x = data.Pt.X;
                int y = data.Pt.Y;
                int msg = (int)wParam;

                switch (msg)
                {
                    case WmLButtonDown:
                        _leftButtonDown = true;
                        LeftButtonDown?.Invoke(x, y);
                        break;
                    case WmLButtonUp:
                        _leftButtonDown = false;
                        LeftButtonUp?.Invoke(x, y);
                        break;
                    case WmRButtonDown:
                        RightButtonDown?.Invoke(x, y);
                        break;
                    case WmRButtonUp:
                        RightButtonUp?.Invoke(x, y);
                        break;
                    case WmMouseMove:
                        if (_leftButtonDown)
                        {
                            MouseMoveWhileLeftDown?.Invoke(x, y);
                        }

                        break;
                    case WmMouseWheel:
                        // mouseData の上位ワードが delta（signed short）
                        short delta = (short)((data.MouseData >> 16) & 0xFFFF);
                        MouseWheel?.Invoke(x, y, delta);
                        break;
                }
            }
            catch
            {
                // フック内例外で入力全体が止まらないようにします。
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        LeftButtonDown = null;
        LeftButtonUp = null;
        RightButtonDown = null;
        RightButtonUp = null;
        MouseMoveWhileLeftDown = null;
        MouseWheel = null;
    }
}
