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
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ASFW_ANY = -1;

    private readonly AppSettingsService _settingsService;
    private readonly StartMenuBoundsService _boundsService;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly IAppVisibility? _appVisibility;
    private IntPtr _hookHandle;
    private IntPtr _mainWindowHwnd;
    private IntPtr _lastNormalFgHwnd;
    private IntPtr _lastAppBeforeWinKeyHwnd;
    private IntPtr _cachedInspectedFgHwnd;
    private bool _cachedInspectedFgIsStartMenu;

    private bool _winKeyDown;
    private bool _otherKeyPressedWhileWinDown;
    private bool _startMenuSessionArmed;
    private long _lastSingleWinReleaseTick;
    private long _handoverWindowUntilTick;
    private bool _inputHandoverPending;
    private IntPtr _nativeHandoverWindow;
    private readonly HashSet<int> _interceptedKeyUps = [];

    public bool IsInputHandoverPending =>
        _inputHandoverPending && Environment.TickCount64 < _handoverWindowUntilTick;

    public IntPtr NativeHandoverWindow => _nativeHandoverWindow;

    public bool CanContinueNativeHandover() => IsInputHandoverPending &&
        _nativeHandoverWindow != IntPtr.Zero && GetForegroundWindow() == _nativeHandoverWindow &&
        IsStartMenuHostWindowCached(_nativeHandoverWindow);

    public void CompleteInputHandover()
    {
        _inputHandoverPending = false;
        _handoverWindowUntilTick = 0;
        _nativeHandoverWindow = IntPtr.Zero;
    }

    public IntPtr LastAppBeforeWinKeyHwnd => _lastAppBeforeWinKeyHwnd;

    public event Action? SingleWinKeyTapped;
    public event Action<string>? StartMenuTypingIntercepted;
    public event Action<string>? AdditionalTypingIntercepted;

    public WinKeyHookService(AppSettingsService settingsService, StartMenuBoundsService boundsService)
    {
        _settingsService = settingsService;
        _boundsService = boundsService;
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
        CompleteInputHandover();
        _interceptedKeyUps.Clear();
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

                // 被接管的按键必须成对吞掉，避免 KeyUp 落入切换前的应用。
                if (isKeyUp && _interceptedKeyUps.Remove(vk))
                    return 1;

                if (vk is VK_LWIN or VK_RWIN)
                {
                    if (isKeyDown)
                    {
                        CompleteInputHandover();
                        if (!_winKeyDown)
                        {
                            _winKeyDown = true;
                            _otherKeyPressedWhileWinDown = false;
                            var currentFg = GetForegroundWindow();
                            if (currentFg != IntPtr.Zero && currentFg != _mainWindowHwnd && !IsStartMenuHostWindowZeroAlloc(currentFg))
                            {
                                _lastAppBeforeWinKeyHwnd = currentFg;
                            }
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
                            _lastNormalFgHwnd = IntPtr.Zero;
                            bool wasAlreadyOpen = IsStartMenuHostWindowCached(GetForegroundWindow());
                            _startMenuSessionArmed = !wasAlreadyOpen;
                            _lastSingleWinReleaseTick = Environment.TickCount64;
                            if (_startMenuSessionArmed && !_boundsService.HasValidCacheForCurrentScreen())
                            {
                                ScheduleStartMenuBoundsProbe();
                            }

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
                    else if (_settingsService.Current.TriggerMode == Models.WinKeyTriggerMode.InterceptTypingInStartMenu &&
                             IsPrintableKeyCandidate(vk))
                    {
                        long now = Environment.TickCount64;

                        // 直到搜索框确认获得稳定的键盘焦点才放行，不能仅凭前台 HWND 或 260ms 超时判断。
                        if ((IsInputHandoverPending || _interceptedKeyUps.Contains(vk)) &&
                            TryMapPrintableKey(vk, out var extraChar))
                        {
                            _interceptedKeyUps.Add(vk);
                            AdditionalTypingIntercepted?.Invoke(extraChar);
                            return 1;
                        }

                        if (ShouldInterceptStartMenuTyping(now) && TryMapPrintableKey(vk, out var typedChar))
                        {
                            var nativeWindow = GetForegroundWindow();
                            if (!IsStartMenuHostWindowCached(nativeWindow))
                                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                            _startMenuSessionArmed = false;
                            _nativeHandoverWindow = nativeWindow;
                            _inputHandoverPending = true;
                            // 有界保护：窗口无法激活时不能无限接管其他应用的输入。
                            _handoverWindowUntilTick = now + 1500;
                            _interceptedKeyUps.Add(vk);

                            // Keep the native menu visible until the replacement cover has rendered.
                            // Focus transfer then dismisses it; never send a global Esc into the new popup.
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

    private void ScheduleStartMenuBoundsProbe()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(220);
            if (!_startMenuSessionArmed)
                return;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                () =>
                {
                    if (_startMenuSessionArmed && IsNativeStartMenuOpen())
                    {
                        _boundsService.TryCaptureOpenStartMenuBounds(GetForegroundWindow());
                    }
                },
                System.Windows.Threading.DispatcherPriority.Background
            );
        });
    }

    private static bool IsPrintableKeyCandidate(int vk) =>
        vk is (>= 0x41 and <= 0x5A) or (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x69) or 0xBE or 0xBD;

    private bool ShouldInterceptStartMenuTyping(long nowTick)
    {
        var fg = GetForegroundWindow();
        if (_mainWindowHwnd != IntPtr.Zero && fg == _mainWindowHwnd)
            return false;

        if (!_startMenuSessionArmed && fg != IntPtr.Zero && fg == _lastNormalFgHwnd)
            return false;

        if (IsNativeStartMenuOpen(fg))
            return true;

        if (_startMenuSessionArmed)
        {
            // Win 键释放只用于安排尺寸探测，不能证明开始菜单已打开。
            // 前台不是已确认的开始菜单宿主时，必须放行输入，不能合成 Esc。
            if ((nowTick - _lastSingleWinReleaseTick) is < 0 or > 850)
                _startMenuSessionArmed = false;
        }

        if (fg != IntPtr.Zero)
            _lastNormalFgHwnd = fg;

        return false;
    }

    public bool IsNativeStartMenuOpen() => IsNativeStartMenuOpen(GetForegroundWindow());

    private bool IsNativeStartMenuOpen(IntPtr fg)
    {
        if (IsStartMenuHostWindowCached(fg))
            return true;

        return false;
    }

    private bool IsStartMenuHostWindowCached(IntPtr fg)
    {
        if (fg == IntPtr.Zero)
            return false;

        if (fg == _cachedInspectedFgHwnd)
            return _cachedInspectedFgIsStartMenu;

        bool result = IsStartMenuHostWindowZeroAlloc(fg);
        _cachedInspectedFgHwnd = fg;
        _cachedInspectedFgIsStartMenu = result;
        return result;
    }

    private static unsafe bool IsStartMenuHostWindowZeroAlloc(IntPtr fg)
    {
        if (fg == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0)
            return false;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            const int bufferCapacity = 260;
            char* buffer = stackalloc char[bufferCapacity];
            uint size = bufferCapacity;
            if (!QueryFullProcessImageNameW(hProcess, 0, buffer, ref size) || size == 0)
                return false;

            ReadOnlySpan<char> fullPath = new(buffer, (int)size);
            int lastSlash = fullPath.LastIndexOfAny('\\', '/');
            ReadOnlySpan<char> fileName = lastSlash >= 0 ? fullPath[(lastSlash + 1)..] : fullPath;

            return fileName.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                   || fileName.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase)
                   || fileName.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(hProcess);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
