param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$NuGetSource = 'https://api.nuget.org/v3/index.json',
    [string]$InnoSetupCompiler
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$definition = [IO.File]::ReadAllText((Join-Path $repo 'EverythingonStart.csproj'))
$version = [string]$definition.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable semantic version.' }
if (& git -C $repo status --porcelain) { throw 'Commit the source before release packaging.' }
$commit = (& git -C $repo rev-parse HEAD).Trim()
if (!$InnoSetupCompiler) { $InnoSetupCompiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (!$InnoSetupCompiler -or !(Test-Path -LiteralPath $InnoSetupCompiler)) { throw 'Specify -InnoSetupCompiler PATH to a trusted Inno Setup 6/7 ISCC.exe.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $output 'single-file-publish'
$portable = Join-Path $output "EverythingonStart-v$version-Portable-x64.exe"
$installer = Join-Path $output "EverythingonStart-v$version-Setup-x64.exe"
$source = Join-Path $output "EverythingonStart-v$version-source.zip"
$sums = Join-Path $output 'SHA256SUMS.txt'
foreach ($path in @($publish,$portable,$installer,$source,$sums)) { if (Test-Path -LiteralPath $path) { throw "Existing release output; choose a fresh directory: $path" } }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet publish (Join-Path $repo 'EverythingonStart.csproj') -c Release -r win-x64 --self-contained true --source $NuGetSource `
    -p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "Single-file publish failed: $LASTEXITCODE" }
$externalDlls = Get-ChildItem -LiteralPath $publish -Filter '*.dll'
if ($externalDlls) { throw 'Publish left external DLLs; the portable edition is not single-file.' }
Copy-Item -LiteralPath (Join-Path $publish 'EverythingonStart.exe') -Destination $portable
& $InnoSetupCompiler "/DAppVersion=$version" "/DPublishDir=$publish" "/DReleaseDir=$output" (Join-Path $repo 'Installer\EverythingonStart.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
& git -c core.autocrlf=false -C $repo archive --format=zip --prefix="EverythingonStart-v$version-source/" --output=$source $commit
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
$lines = foreach ($path in @($portable,$installer,$source)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing release asset: $path" }
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllText($sums,($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
[ordered]@{ version=$version; commit=$commit; portable=$portable; installer=$installer; source=$source; checksums=$sums } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.json') -Encoding utf8
Write-Output "Source commit: $commit"
$lines
