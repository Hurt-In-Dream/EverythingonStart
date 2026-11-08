param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('popup-handover-tests-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$directory = [string](Resolve-Path -LiteralPath $AppAssemblyDirectory)
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'),
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PopupHandover.Regression.cs.txt')), $utf8)
$escaped = [Security.SecurityElement]::Escape($directory)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'PopupTests.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup><Using Remove="System.Drawing" /><Using Remove="System.Windows.Forms" />
    <Reference Include="EverythingonStart"><HintPath>$escaped\EverythingonStart.dll</HintPath></Reference>
  </ItemGroup>
</Project>
"@, $utf8)
& dotnet run --project (Join-Path $WorkDirectory 'PopupTests.csproj') --verbosity quiet -- $SourceRoot
exit $LASTEXITCODE
