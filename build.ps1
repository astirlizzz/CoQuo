$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& "$root/TrayIconSize/build.ps1"
if ($LASTEXITCODE -ne 0) { throw 'TrayIconSize build failed' }
& dotnet run --project "$root/CoQuo.Tests/CoQuo.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Usage parser tests failed' }
& dotnet publish "$root/CoQuo/CoQuo.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root/dist"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "CoQuo.exe: $root/dist/CoQuo.exe"
