using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Loc = TracePrompt.Localization.LocalizationManager;

namespace TracePrompt.Services;

/// <summary>
/// グローバルキーボード操作を監視するサービスです（WH_KEYBOARD_LL）。
/// KeyDown のみ通知します。表示用文字列の組み立ては呼び出し側で行います。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private LowLevelKeyboardProc? _proc;
    private nint _hookId = nint.Zero;
    private bool _disposed;

    /// <summary>
    /// キー押し下げ時。引数は仮想キーコード、修飾キー込みの表示用ラベル、
    /// 続けて Ctrl/Alt/Shift/Win の押下状態です（いずれも <c>GetAsyncKeyState</c> によるグローバルな
    /// 状態で、自アプリのウィンドウがフォーカスを持っているかどうかに関係なく正しく取れます。
    /// 呼び出し側は <c>System.Windows.Input.Keyboard.Modifiers</c> のような WPF のフォーカス依存 API を
    /// 使わず、必ずこの値を使ってください）。
    /// </summary>
    public event Action<int, string, bool, bool, bool, bool>? KeyDown;

    public bool IsRunning => _hookId != nint.Zero;

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

        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, moduleHandle, 0);
        if (_hookId == nint.Zero)
        {
            _proc = null;
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(Loc.Instance.Format("Error_KeyboardHookStartFailed_Format", error));
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
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            try
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                int vk = (int)data.VkCode;

                // 修飾キー単独は記録しない（組み合わせは次のキーでまとめる）
                if (IsModifierKey(vk))
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool ctrl = IsKeyDown(VkControl);
                bool alt = IsKeyDown(VkMenu);
                bool shift = IsKeyDown(VkShift);
                bool win = IsKeyDown(VkLWin) || IsKeyDown(VkRWin);

                string display = BuildDisplayLabel(vk, ctrl, alt, shift, win);
                if (!string.IsNullOrEmpty(display))
                {
                    KeyDown?.Invoke(vk, display, ctrl, alt, shift, win);
                }
            }
            catch
            {
                // フック内例外で入力が止まらないようにします。
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// 表示用ラベルを組み立てます。文字キーも具体名で出します（例: w, Shift + W, Ctrl + S）。
    /// 物理キー（仮想キー）単位です。IME 変換後の「全角ｗ」ではなく、押したキーそのものです。
    /// </summary>
    public static string BuildDisplayLabel(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        string keyName = GetKeyName(vk, shift, ctrl || alt || win);
        if (string.IsNullOrEmpty(keyName))
        {
            keyName = $"VK_{vk:X2}";
        }

        var sb = new StringBuilder();
        if (ctrl)
        {
            sb.Append("Ctrl + ");
        }

        if (alt)
        {
            sb.Append("Alt + ");
        }

        if (win)
        {
            sb.Append("Win + ");
        }

        // 英字で Shift のみのときは「W」と出し、"Shift + w" にはしない（見やすさ優先）
        bool letterWithShiftOnly = shift && !ctrl && !alt && !win && vk is >= 0x41 and <= 0x5A;
        if (shift && !letterWithShiftOnly)
        {
            sb.Append("Shift + ");
        }

        sb.Append(keyName);
        return sb.ToString();
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private static bool IsModifierKey(int vk) =>
        vk is VkShift or VkControl or VkMenu or VkLWin or VkRWin
            or 0xA0 or 0xA1 // L/R Shift
            or 0xA2 or 0xA3 // L/R Ctrl
            or 0xA4 or 0xA5; // L/R Alt

    /// <summary>
    /// 仮想キーを表示名にします。英字は修飾なしなら小文字（w）、Shift のみなら大文字（W）。
    /// </summary>
    private static string GetKeyName(int vk, bool shift, bool otherModifiers)
    {
        // A-Z
        if (vk is >= 0x41 and <= 0x5A)
        {
            char c = (char)vk;
            // Ctrl/Alt/Win 付きは大文字で表記（Ctrl + W）
            if (otherModifiers)
            {
                return c.ToString();
            }

            return shift ? c.ToString() : char.ToLowerInvariant(c).ToString();
        }

        return vk switch
        {
            0x0D => "Enter",
            0x1B => "Escape",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x20 => "Space",
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0x14 => "CapsLock",
            0x2C => "PrintScreen",
            0x13 => "Pause",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            // よく使う OEM（JIS/US で表示が違う場合あり。キー位置としては VK で一致）
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xE2 => "\\", // 日本配列の＼ など
            _ => string.Empty
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        KeyDown = null;
    }
}
