param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [switch]$WallpaperSmokeTest,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('gaussian-blur-tests-' + [guid]::NewGuid().ToString('N')))
)
# Compatibility entry point: v1.4 uses live native composition, not wallpaper bitmaps.
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-NativeBackdrop.ps1') -AppAssemblyDirectory $AppAssemblyDirectory -WorkDirectory $WorkDirectory
exit $LASTEXITCODE
