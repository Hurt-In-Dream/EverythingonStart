using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const byte VK_ESCAPE = 0x1B;
    private const byte VK_DUMMY_MASK = 0xFF;

    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int ASFW_ANY = -1;

    private readonly AppSettingsService _settingsService;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly IAppVisibility? _appVisibility;
    private IntPtr _hookHandle;
    private IntPtr _mainWindowHwnd;

    private bool _winKeyDown;
    private bool _otherKeyPressedWhileWinDown;
    private bool _startMenuSessionArmed;
    private long _lastSingleWinReleaseTick;
    private long _handoverWindowUntilTick;

    public event Action? SingleWinKeyTapped;
    public event Action<string>? StartMenuTypingIntercepted;
    public event Action<string>? AdditionalTypingIntercepted;

    public WinKeyHookService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _hookProc = HookCallback;
        _appVisibility = CreateAppVisibilityInstance();
    }

    public void SetMainWindowHandle(IntPtr hwnd)
    {
        _mainWindowHwnd = hwnd;
    }

    private static IAppVisibility? CreateAppVisibilityInstance()
    {
        var type = Type.GetTypeFromCLSID(new Guid("7E5FE3D9-985F-4908-91F9-EE19F9FD1514"));
        if (type == null)
            return null;

        return Activator.CreateInstance(type) as IAppVisibility;
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

                            // 默认模式：放行 Win 键让系统正常弹出带磁贴的开始菜单及全屏下的任务栏
                            bool wasAlreadyOpen = IsNativeStartMenuOpen();
                            _startMenuSessionArmed = !wasAlreadyOpen;
                            _lastSingleWinReleaseTick = Environment.TickCount64;
                            SingleWinKeyTapped?.Invoke();
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
                        long now = Environment.TickCount64;

                        // 如果正在从开始菜单切换到 EverythingonStart 的焦点的短暂窗口期内，继续捕获快速连打的按键
                        if (now < _handoverWindowUntilTick &&
                            _mainWindowHwnd != IntPtr.Zero &&
                            GetForegroundWindow() != _mainWindowHwnd &&
                            TryMapPrintableKey(vk, out var extraChar))
                        {
                            AdditionalTypingIntercepted?.Invoke(extraChar);
                            return 1;
                        }

                        if (TryMapPrintableKey(vk, out var typedChar) && ShouldInterceptStartMenuTyping(now))
                        {
                            _startMenuSessionArmed = false;
                            _handoverWindowUntilTick = now + 260;

                            // 保持当前前台为 StartMenuExperienceHost 时发送 Esc，使其立即收到按键并隐藏开始菜单
                            DismissNativeStartMenu();
                            AllowSetForegroundWindow(ASFW_ANY);

                            StartMenuTypingIntercepted?.Invoke(typedChar);
                            return 1;
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool ShouldInterceptStartMenuTyping(long nowTick)
    {
        var fg = GetForegroundWindow();
        if (_mainWindowHwnd != IntPtr.Zero && fg == _mainWindowHwnd)
            return false;

        if (IsNativeStartMenuOpen())
            return true;

        if (_startMenuSessionArmed && (nowTick - _lastSingleWinReleaseTick) is >= 0 and <= 850)
            return true;

        return false;
    }

    public bool IsNativeStartMenuOpen()
    {
        if (_appVisibility != null && _appVisibility.IsLauncherVisible(out bool launcherVisible) == 0 && launcherVisible)
            return true;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0)
            return false;

        using var proc = Process.GetProcessById((int)pid);
        var name = proc.ProcessName;
        return name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
    }

    public static void DismissNativeStartMenu()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SuppressNativeStartMenuAndReleaseWinKey(byte winVk)
    {
        keybd_event(VK_DUMMY_MASK, 0, 0, UIntPtr.Zero);
        keybd_event(VK_DUMMY_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(winVk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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

        bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        if (vk is >= 0x41 and <= 0x5A)
        {
            char c = (char)(shiftDown ? vk : vk + 32);
            text = c.ToString();
            return true;
        }

        if (vk is >= 0x30 and <= 0x39 && !shiftDown)
        {
            text = ((char)vk).ToString();
            return true;
        }

        if (vk is >= 0x60 and <= 0x69)
        {
            text = ((char)('0' + (vk - 0x60))).ToString();
            return true;
        }

        if (vk == 0xBE && !shiftDown)
        {
            text = ".";
            return true;
        }

        if (vk == 0xBD)
        {
            text = shiftDown ? "_" : "-";
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }

    [ComImport]
    [Guid("2246EA2D-CAEA-4444-A3C4-6DE827E44313")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppVisibility
    {
        [PreserveSig]
        int GetAppVisibilityOnMonitor(IntPtr hMonitor, out int pMode);

        [PreserveSig]
        int IsLauncherVisible([MarshalAs(UnmanagedType.Bool)] out bool pfVisible);

        [PreserveSig]
        int Advise(IntPtr pCallback, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
