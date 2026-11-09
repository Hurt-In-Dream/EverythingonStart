param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\Services\WinKeyHookService.cs'),
    [string]$MainWindowPath,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('win-key-regression-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $SourcePath))
if (!$MainWindowPath) {
    $MainWindowPath = Join-Path (Split-Path (Split-Path (Resolve-Path $SourcePath) -Parent) -Parent) 'MainWindow.xaml.cs'
}
$main = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $MainWindowPath))
# Also execute the real focus-retry body against deterministic window/textbox doubles.
$signature = '    private void RetryInputHandoverFocus()'
$start = $main.IndexOf($signature, [StringComparison]::Ordinal)
if ($start -ge 0) {
    $opening = $main.IndexOf('{', $start)
    $depth = 1
    $end = $opening + 1
    while ($depth -gt 0 -and $end -lt $main.Length) {
        if ($main[$end] -eq '{') { $depth++ }
        if ($main[$end] -eq '}') { $depth-- }
        $end++
    }
    $focusMethod = $main.Substring($start, $end - $start)
} else {
    $legacy = [regex]::Match($main, '(?s)focusTimer\.Tick \+= \(_, _\) =>\s*\{(.*?)\r?\n            \};')
    if (!$legacy.Success) { throw 'Could not find the original focus timer or the focus retry method.' }
    $focusMethod = $signature + "`n    {" + $legacy.Groups[1].Value.Replace('focusTimer.Stop()', '_inputHandoverFocusTimer.Stop()') + "`n    }"
}
$focusMethod = $focusMethod.Replace('Environment.TickCount64', 'TestNative.Now')
# Compile the real hook/state machine. Substitute only clock, OS calls and service dependencies
# so focus delays and foreground-without-text-focus can be reproduced without typing into user apps.
$source = $source.Replace('Environment.TickCount64', 'TestNative.Now')
$source = [regex]::Replace($source,
    '(?s)    private static IAppVisibility\? CreateAppVisibilityInstance\(\).*?(?=    public void Start\(\))',
    "    private static IAppVisibility? CreateAppVisibilityInstance() => null;`n`n")
$source = [regex]::Replace($source,
    '(?s)    private static unsafe bool IsStartMenuHostWindowZeroAlloc\(IntPtr fg\).*?(?=    public static void DismissNativeStartMenu\(\))',
    "    private static bool IsStartMenuHostWindowZeroAlloc(IntPtr fg) => fg == new IntPtr(2);`n`n")

$replacements = @{
    'private static extern IntPtr GetForegroundWindow();' = 'private static IntPtr GetForegroundWindow() => TestNative.Foreground;'
    'private static extern short GetAsyncKeyState(int vKey);' = 'private static short GetAsyncKeyState(int vKey) => TestNative.ControlDown && vKey == 0x11 ? unchecked((short)0x8000) : (short)0;'
    'private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);' = 'private static IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam) => new(-99);'
    'private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);' = 'private static void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo) { TestNative.SyntheticKeys.Add((bVk, dwFlags)); }'
    'private static extern bool AllowSetForegroundWindow(int dwProcessId);' = 'private static bool AllowSetForegroundWindow(int dwProcessId) => true;'
    'private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);' = 'private static int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size) { value = TestNative.MenuCloaked ? 2 : 0; return 0; }'
}
foreach ($signature in $replacements.Keys) {
    $pattern = '\[DllImport\([^\r\n]+\)\]\s+' + [regex]::Escape($signature)
    if ($signature.Contains('DwmGetWindowAttribute') -and !$source.Contains($signature)) { continue }
    if ([regex]::Matches($source, $pattern).Count -ne 1) {
        throw "Expected exactly one platform declaration: $signature"
    }
    $source = [regex]::Replace($source, $pattern, $replacements[$signature])
}

New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'WinKeyHookService.cs'), $source, $utf8)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'),
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WinKeyHandover.Regression.cs.txt')), $utf8)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'FocusProbe.cs'),
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WinKeyHandover.FocusProbe.cs.txt')).Replace('/* REAL_FOCUS_METHOD */', $focusMethod), $utf8)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Regression.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@, $utf8)
& dotnet run --project (Join-Path $WorkDirectory 'Regression.csproj') --verbosity quiet
exit $LASTEXITCODE
