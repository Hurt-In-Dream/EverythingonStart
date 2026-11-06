param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'))

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'EverythingonStart.csproj'
[xml]$definition = [IO.File]::ReadAllText($project)
$version = [string]$definition.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable semantic version in the project.' }
$pending = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $pending) { throw 'Release packaging requires a clean, committed working tree.' }
$commit = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "EverythingonStart-v$version-win-x64"
$package = Join-Path $output $packageName
$binaryZip = Join-Path $output "$packageName.zip"
$sourceZip = Join-Path $output "EverythingonStart-v$version-source.zip"
$sums = Join-Path $output 'SHA256SUMS.txt'
foreach ($path in @($package, $binaryZip, $sourceZip, $sums)) {
    if (Test-Path -LiteralPath $path) { throw "Release output already exists; choose a fresh output directory: $path" }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -o $package
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $package $name)
}
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination (Join-Path $package 'licenses') -Recurse
$utf8 = [Text.UTF8Encoding]::new($false)
$info = [ordered]@{
    application = 'EverythingonStart'; version = $version; sourceCommit = $commit.Trim()
    runtimeIdentifier = 'win-x64'; selfContained = $true
    sourceArchive = [IO.Path]::GetFileName($sourceZip)
    sourceRepository = 'https://github.com/Hurt-In-Dream/EverythingonStart'
}
[IO.File]::WriteAllText((Join-Path $package 'BUILD-INFO.json'), ($info | ConvertTo-Json) + "`n", $utf8)
foreach ($name in @('EverythingonStart.exe', 'EverythingonStart.dll', 'Everything3.dll', 'Everything64.dll',
    'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll',
    'EverythingonStart.runtimeconfig.json', 'LICENSE', 'licenses\EverythingSDK-LICENSE.txt',
    'licenses\EverythingSDK3-LICENSE.txt', 'licenses\MingCute-LICENSE.txt', 'licenses\DotNet-LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $name))) { throw "Missing release dependency or license: $name" }
}
foreach ($name in @('Everything3.dll', 'Everything64.dll')) {
    $original = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $repo "Native\$name")).Hash
    $published = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $package $name)).Hash
    if ($original -ne $published) { throw "Native dependency changed during packaging: $name" }
}
& git -C $repo archive --format=zip --prefix="EverythingonStart-v$version-source/" --output=$sourceZip $commit
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
Compress-Archive -LiteralPath $package -DestinationPath $binaryZip -CompressionLevel Optimal
$lines = foreach ($path in @($binaryZip, $sourceZip)) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllText($sums, ($lines -join "`n") + "`n", $utf8)
Write-Output "Source commit: $commit"
Write-Output "Binary: $binaryZip"
Write-Output "Source: $sourceZip"
Write-Output "Checksums: $sums"
$lines
