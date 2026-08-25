# Easy4K 一键构建并启动：编译 Debug + Release，默认启动 Debug 版
# 用法: powershell -ExecutionPolicy Bypass -File build-and-run.ps1  [release]
#   （不带参数=启动Debug版；带 release=启动Release版）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Easy4K\Easy4K.csproj"

Write-Host "== 编译 Debug ==" -ForegroundColor Cyan
dotnet build $proj -c Debug
if ($LASTEXITCODE -ne 0) { throw "Debug 编译失败" }

Write-Host "== 编译 Release ==" -ForegroundColor Cyan
dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "Release 编译失败" }

$cfg = if ($args[0] -eq "release") { "Release" } else { "Debug" }
$exe = Join-Path $root "src\Easy4K\bin\$cfg\net10.0-windows10.0.26100.0\win-x64\Easy4K.exe"
if (-not (Test-Path $exe)) { throw "未找到 $exe" }

Write-Host "== 启动 $cfg 版 ==" -ForegroundColor Green
Start-Process $exe
