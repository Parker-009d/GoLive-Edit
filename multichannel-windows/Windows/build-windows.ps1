$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GoLive.Windows.csproj'
$output = Join-Path $PSScriptRoot 'dist\win-x64'
$pluginSource = Join-Path $PSScriptRoot '..\vst-plugin\GoLiveFX.cpp'
$pluginOutput = Join-Path $PSScriptRoot 'Resources\GoLiveFX.dll'
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
  throw 'Build GoLiveFX.dll with the Visual Studio C++ x64 tools first. The Windows app needs this OBS processor DLL.'
}
cl.exe /nologo /std:c++17 /O2 /LD $pluginSource /link "/OUT:$pluginOutput"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pluginOutput)) { throw 'GoLiveFX.dll build failed.' }
dotnet publish $project -Configuration Release -RuntimeIdentifier win-x64 `
  --SelfContained true `
  -p:EnableWindowsTargeting=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $output
Write-Host "Windows build created at $output"

