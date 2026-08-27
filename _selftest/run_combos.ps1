# Easy4K foreground UI self-test driver (ASCII-only to avoid PS5.1 GBK issues)
# Sets win-x64\appsettings.json -> launches Easy4K.exe --selftest <video> <report> <mask>
# selftest runs the pipeline inside the foreground UI automatically, writes report, exits.
# mask: 1=split 2=superres 4=interpolate 8=merge 16=audio
$ErrorActionPreference = "Stop"
$rel  = "i:\Easy4K\src\Easy4K\bin\Release\net10.0-windows10.0.26100.0\win-x64"
$exe  = "$rel\Easy4K.exe"
$cfg  = "$rel\appsettings.json"
$video2s = "i:\Easy4K\_selftest\test_video.mp4"
$video10s = "i:\Easy4K\_selftest\" + [char]0x5f69 + [char]0x6761 + [char]0x6d4b + [char]0x8bd5 + [char]0x89c6 + [char]0x9891 + "_10s.mp4"
$outDir = "i:\Easy4K\_selftest\combos"
$markDone = "$([char]0x5168)$([char]0x90e8)$([char]0x5b8c)$([char]0x6210)"
$markFail = "$([char]0x5931)$([char]0x8d25)"
$markEx   = "$([char]0x5f02)$([char]0x5e38)"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-Config {
    param(
        [bool]$cpu = $false,
        [bool]$safe = $true,
        [bool]$lowerQ = $false,
        [bool]$gpuAccel = $true,
        [string]$ifEngine = "NCNN",
        [string]$ifModel = "rife-v4.6",
        [int]$threads = 2,
        [string]$srModel = "realesr-animevideov3",
        [int]$srScale = 2,
        [int]$ifMult = 2
    )
    $json = @{
        AppConfig = @{
            ToolsRoot = "Tools"
            TempRoot = "Temp"
            OutputRoot = "Output"
            DefaultSrModel = $srModel
            DefaultIfModel = $ifModel
            DefaultIfEngine = $ifEngine
            DefaultSrScale = $srScale
            DefaultIfMultiplier = $ifMult
            ThreadCount = $threads
            UseSafeFrameRate = $safe
            UseCpuProcessing = $cpu
            LowerQualityForVram = $lowerQ
            UseGpuAcceleration = $gpuAccel
            EncodePreset = "medium"
            Language = "zh-CN"
            Theme = "system"
            Version = "1.0.4"
        }
        ToolPaths = @{
            FFmpegDir = "FFmpeg-Lei"
            FFprobeDir = "FFmpeg-Lei"
            RealEsrganDir = "realesrgan-ncnn"
            RifeDir = "rife"
            NvEncDir = "NVEncC_9.32_x64"
        }
    } | ConvertTo-Json -Depth 5
    Set-Content -Path $cfg -Value $json -Encoding UTF8
}

function Invoke-Run {
    param([string]$name, [int]$mask, [string]$video, [hashtable]$cfgParams, [string]$note)
    Write-Host "=== [$name] mask=$mask $note ===" -ForegroundColor Cyan
    Write-Config @cfgParams
    $report = "$outDir\$name.txt"
    Get-Process -Name "Easy4K" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $p = Start-Process -FilePath $exe -ArgumentList @("--selftest", "`"$video`"", "`"$report`"", "$mask") -PassThru
    if (-not $p.WaitForExit(240000)) {
        $p.Kill()
        Add-Content $report "TIMEOUT: 运行超过 4 分钟被强制结束"
    }
    Start-Sleep -Milliseconds 300
    if (Test-Path $report) {
        $t = Get-Content $report -Raw -Encoding UTF8
        $ok  = $t.Contains($markDone)
        $bad = ([regex]::Matches($t, $markFail).Count + [regex]::Matches($t, $markEx).Count)
        $timeout = $t.Contains("TIMEOUT")
        # 无合并掩码(不含 8)时软件不写"全部完成"标记：以 失败/异常 行数为 0 且未超时判定
        $noMerge = ($mask -band 8) -ne 8
        $pass = if ($timeout) { $false } elseif ($noMerge) { $bad -eq 0 } else { $ok }
        $line = "`n[$name] mask=$mask note=$note => " + $(if ($pass) { "PASS" } else { "FAIL" }) + " (bad-lines=$bad)"
        if ($pass) { Write-Host $line -ForegroundColor Green } else { Write-Host $line -ForegroundColor Red }
    } else {
        $line = "`n[$name] mask=$mask note=$note => REPORT_MISSING"
        Write-Host $line -ForegroundColor Red
    }
    Add-Content "i:\Easy4K\_selftest\combos\_summary.txt" $line
}

Remove-Item "$outDir\_summary.txt" -ErrorAction SilentlyContinue

# ===== A: stage pipelines (default config, NCNN GPU) =====
Invoke-Run -name "run01_split"      -mask 1  -video $video2s -cfgParams @{} -note "split only"
Invoke-Run -name "run02_split_sr"   -mask 3  -video $video2s -cfgParams @{} -note "split+superres"
Invoke-Run -name "run03_split_if"   -mask 5  -video $video2s -cfgParams @{} -note "split+interp NCNN"
Invoke-Run -name "run04_full"       -mask 15 -video $video2s -cfgParams @{} -note "split+sr+if+merge"
Invoke-Run -name "run05_full_audio" -mask 31 -video $video2s -cfgParams @{} -note "full+audio"

# ===== B: Offical engine =====
Invoke-Run -name "run06_offical" -mask 12 -video $video2s -cfgParams @{ ifEngine="Offical"; ifModel="official_4.6" } -note "Offical interp+merge"

# ===== C: CPU mode =====
Invoke-Run -name "run07_cpu_full"    -mask 31 -video $video2s -cfgParams @{ cpu=$true } -note "CPU full (SR->GPU fallback, IF CPU)"
Invoke-Run -name "run08_cpu_offical" -mask 12 -video $video2s -cfgParams @{ cpu=$true; ifEngine="Offical"; ifModel="official_4.6" } -note "CPU Offical interp+merge"

# ===== D: switch combinations =====
Invoke-Run -name "run09_safe_off"     -mask 31 -video $video2s -cfgParams @{ safe=$false } -note "safe-frame-rate=off"
Invoke-Run -name "run10_lowerq"       -mask 31 -video $video2s -cfgParams @{ lowerQ=$true } -note "lower-quality(-u)=on"
Invoke-Run -name "run11_gpuaccel_off" -mask 31 -video $video2s -cfgParams @{ gpuAccel=$false } -note "ffmpeg-gpu-accel=off"
Invoke-Run -name "run12_all_switches" -mask 31 -video $video2s -cfgParams @{ cpu=$true; safe=$false; lowerQ=$true; gpuAccel=$false } -note "cpu+safe-off+lowerq+gpuaccel-off"

# ===== E: 10s video final validation (default) =====
Invoke-Run -name "run13_full_10s" -mask 31 -video $video10s -cfgParams @{} -note "10s video full+audio"

Write-Host "`n=== ALL DONE ===" -ForegroundColor Green
Get-Content "$outDir\_summary.txt"
