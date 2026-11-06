param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('app-theme-tests-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$assembly = Join-Path (Resolve-Path -LiteralPath $AppAssemblyDirectory) 'EverythingonStart.dll'
if (!(Test-Path -LiteralPath $assembly)) { throw "Assembly not found: $assembly" }
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'),
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppTheme.Regression.cs.txt')), $utf8)
$escaped = [Security.SecurityElement]::Escape([string](Resolve-Path -LiteralPath $AppAssemblyDirectory))
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'ThemeTests.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.Windows.Forms" /><Using Remove="System.Drawing" />
    <Reference Include="EverythingonStart"><HintPath>$escaped\EverythingonStart.dll</HintPath></Reference>
    <None Include="$escaped\Everything3.dll" CopyToOutputDirectory="Always" />
    <None Include="$escaped\Everything64.dll" CopyToOutputDirectory="Always" />
  </ItemGroup>
</Project>
"@, $utf8)
& dotnet run --project (Join-Path $WorkDirectory 'ThemeTests.csproj') --verbosity quiet -- $WorkDirectory
exit $LASTEXITCODE
