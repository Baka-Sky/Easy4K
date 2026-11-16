# SFDP 训练流水线启动器（后台运行，异常自动重启）
# 用法：右键「使用 PowerShell 运行」，或 powershell -ExecutionPolicy Bypass -File run_pipeline.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$cfg = Get-Content (Join-Path $root "config.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$python = $cfg.python
$logDir = Join-Path $root "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null

$outLog = Join-Path $logDir "pipeline.out.log"
$errLog = Join-Path $logDir "pipeline.err.log"

Write-Host "启动 SFDP 流水线：$python pipeline.py" -ForegroundColor Cyan
Write-Host "日志：$outLog / $errLog"
Write-Host "停止：关闭本窗口或结束 python 进程"

while ($true) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$stamp] pipeline start" | Out-File -Append -Encoding UTF8 $outLog
    & $python (Join-Path $root "pipeline.py") *>> $outLog
    $code = $LASTEXITCODE
    "[$stamp] pipeline exited ($code)，5 秒后重启" | Out-File -Append -Encoding UTF8 $outLog
    Start-Sleep -Seconds 5
}
