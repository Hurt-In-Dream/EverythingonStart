param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [switch]$LaunchSmokeTest,
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '..\obj\start-menu-index-tests')
)
$ErrorActionPreference = 'Stop'
$directory = [string](Resolve-Path -LiteralPath $AppAssemblyDirectory)
$work = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $work -Force | Out-Null

$catalog = @(Get-StartApps | Select-Object Name, AppID)
if ($catalog.Count -eq 0) {
    throw 'Windows 没有返回已注册的应用。'
}

$launchTargets = @()
if ($LaunchSmokeTest) {
    $packages = @(Get-AppxPackage)
    foreach ($target in $catalog | Where-Object { $_.Name -in 'Microsoft Store', 'Microsoft Rewards' }) {
        $family, $applicationId = $target.AppID -split '!', 2
        $package = $packages | Where-Object { $_.PackageFamilyName -eq $family }
        if (!$package) {
            throw "没有找到应用对应的包：$($target.Name)"
        }
        $manifest = Get-AppxPackageManifest -Package $package.PackageFullName
        $application = $manifest.Package.Applications.Application | Where-Object { $_.Id -eq $applicationId }
        if (!$application.Executable) {
            throw "没有找到应用对应的可执行文件：$($target.Name)"
        }
        $launchTargets += [pscustomobject]@{
            Name = $target.Name
            AppID = $target.AppID
            ExecutablePath = Join-Path $package.InstallLocation $application.Executable
        }
    }
}

$dataPath = Join-Path $work 'installed-apps.json'
[pscustomobject]@{ Catalog = $catalog; LaunchTargets = $launchTargets } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $dataPath -Encoding utf8

& dotnet run --project (Join-Path $PSScriptRoot 'StartMenuIndex.Tests.csproj') --verbosity quiet `
    "-p:AppAssemblyDirectory=$directory" "-p:BaseIntermediateOutputPath=$work\obj\" `
    "-p:OutputPath=$work\bin\" '-p:RestoreSources=https://api.nuget.org/v3/index.json' -- $dataPath
exit $LASTEXITCODE
