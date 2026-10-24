param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with C++ tools not found' }
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $cmake -S $root -B "$root/build" -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
& $cmake --build "$root/build" --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Output: $root/build/$Configuration"
