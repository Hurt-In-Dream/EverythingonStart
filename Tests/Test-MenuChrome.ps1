param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('menu-chrome-tests-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$directory = [string](Resolve-Path -LiteralPath $AppAssemblyDirectory)
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MenuChrome.Regression.cs.txt')), $utf8)
$escaped = [Security.SecurityElement]::Escape($directory)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'MenuTests.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Reference Include="EverythingonStart"><HintPath>$escaped\EverythingonStart.dll</HintPath></Reference></ItemGroup>
</Project>
"@, $utf8)
& dotnet run --project (Join-Path $WorkDirectory 'MenuTests.csproj') --verbosity quiet -- $WorkDirectory
exit $LASTEXITCODE
