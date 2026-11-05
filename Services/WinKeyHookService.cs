using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EverythingonStart.Services;

public sealed class WinKeyHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_ESCAPE = 0x1B;
    private const byte VK_DUMMY_MASK = 0xFF;

    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly AppSettingsService _settingsService;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle;

    private bool _winKeyDown;
    private bool _otherKeyPressedWhileWinDown;
    private long _lastSingleWinReleaseTick;

    public event Action? SingleWinKeyTapped;
    public event Action<string>? StartMenuTypingIntercepted;

    public WinKeyHookService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module != null ? GetModuleHandleW(module.ModuleName) : IntPtr.Zero;
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isInjected = (info.flags & LLKHF_INJECTED) != 0;

            if (!isInjected)
            {
                int msg = (int)wParam;
                bool isKeyDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool isKeyUp = msg is WM_KEYUP or WM_SYSKEYUP;
                int vk = (int)info.vkCode;

                if (vk is VK_LWIN or VK_RWIN)
                {
                    if (isKeyDown)
                    {
                        if (!_winKeyDown)
                        {
                            _winKeyDown = true;
                            _otherKeyPressedWhileWinDown = false;
                        }
                    }
                    else if (isKeyUp)
                    {
                        bool wasSingleTap = _winKeyDown && !_otherKeyPressedWhileWinDown;
                        _winKeyDown = false;

                        if (wasSingleTap)
                        {
                            if (_settingsService.Current.TriggerMode == Models.WinKeyTriggerMode.DirectOnSingleWinTap)
                            {
                                SuppressNativeStartMenuAndReleaseWinKey((byte)vk);
                                SingleWinKeyTapped?.Invoke();
                                return 1;
                            }

                            _lastSingleWinReleaseTick = Environment.TickCount64;
                        }
                    }
                }
                else if (isKeyDown)
                {
                    if (_winKeyDown)
                    {
                        _otherKeyPressedWhileWinDown = true;
                    }
                    else if (_settingsService.Current.TriggerMode == Models.WinKeyTriggerMode.InterceptTypingInStartMenu)
                    {
                        if (TryMapPrintableKey(vk, out var typedChar) && IsStartMenuOrSearchHostActive())
                        {
                            _lastSingleWinReleaseTick = 0;
                            DismissNativeStartMenu();
                            StartMenuTypingIntercepted?.Invoke(typedChar);
                            return 1;
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static void SuppressNativeStartMenuAndReleaseWinKey(byte winVk)
    {
        keybd_event(VK_DUMMY_MASK, 0, 0, UIntPtr.Zero);
        keybd_event(VK_DUMMY_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(winVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void DismissNativeStartMenu()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private bool IsStartMenuOrSearchHostActive()
    {
        long elapsed = Environment.TickCount64 - _lastSingleWinReleaseTick;
        if (elapsed is >= 0 and <= 8000)
            return true;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;

        var className = new StringBuilder(128);
        GetClassNameW(fg, className, className.Capacity);
        var cls = className.ToString();
        if (cls is not ("Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"))
            return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0)
            return false;

        using var proc = Process.GetProcessById((int)pid);
        var name = proc.ProcessName;
        return name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapPrintableKey(int vk, out string text)
    {
        text = string.Empty;

        bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                       || (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                       || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

        if (ctrlDown || altDown)
            return false;

        if (vk is >= 0x41 and <= 0x5A)
        {
            text = ((char)(vk + 32)).ToString();
            return true;
        }

        if (vk is >= 0x30 and <= 0x39)
        {
            text = ((char)vk).ToString();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
