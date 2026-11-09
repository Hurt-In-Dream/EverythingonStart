param(
    [Parameter(Mandatory)][string]$AppAssemblyDirectory,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('menu-opacity-tests-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$directory = [string](Resolve-Path -LiteralPath $AppAssemblyDirectory)
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Program.cs'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MenuOpacity.Regression.cs.txt')), $utf8)
$escaped = [Security.SecurityElement]::Escape($directory)
[IO.File]::WriteAllText((Join-Path $WorkDirectory 'Tests.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF><UseWindowsForms>true</UseWindowsForms><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup><Using Remove="System.Drawing" /><Using Remove="System.Windows.Forms" />
    <Reference Include="EverythingonStart"><HintPath>$escaped\EverythingonStart.dll</HintPath></Reference>
    <Reference Include="HtmlAgilityPack" Condition="Exists('$escaped\HtmlAgilityPack.dll')"><HintPath>$escaped\HtmlAgilityPack.dll</HintPath></Reference>
    <None Include="$escaped\Everything3.dll" CopyToOutputDirectory="Always" />
    <None Include="$escaped\Everything64.dll" CopyToOutputDirectory="Always" />
  </ItemGroup>
</Project>
"@, $utf8)
& dotnet run --project (Join-Path $WorkDirectory 'Tests.csproj') --verbosity quiet -- $SourceRoot $WorkDirectory
exit $LASTEXITCODE
