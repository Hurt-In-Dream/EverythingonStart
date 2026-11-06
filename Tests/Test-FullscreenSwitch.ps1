param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\MainWindow.xaml.cs'),
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('fullscreen-switch-tests-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $SourcePath))
function Get-Method([string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method not found: $signature" }
    $open = $source.IndexOf('{', $start)
    $depth = 1; $end = $open + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    return $source.Substring($start, $end - $start)
}
# Execute the exact production bodies with real native HWND/Z-order calls.
# Foreground, shell notifications and monitor dimensions are isolated test inputs.
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FullscreenSwitch.Regression.cs.txt'))
$template = $template.Replace('/* REAL_DEMOTE */', (Get-Method '    private void DemoteFullscreenWindowsBelowTaskbar('))
$template = $template.Replace('/* REAL_RESTORE */', (Get-Method '    private void RestoreDemotedFullscreenWindows()'))
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'), $template, $utf8)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Probe.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@, $utf8)
Write-Output "SOURCE=$SourcePath SHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $SourcePath).Hash)"
& dotnet run --project (Join-Path $WorkDirectory 'Probe.csproj') --verbosity quiet
exit $LASTEXITCODE
