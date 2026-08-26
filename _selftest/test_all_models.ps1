$ErrorActionPreference = "Continue"
$root = "i:\Easy4K"
$sr = "$root\Tools\realesrgan-ncnn"
$rife = "$root\Tools\rife"
$py = "$root\Tools\officalrife\python\python.exe"
$runPy = "$root\Tools\officalrife\run.py"
$in = "$root\_selftest\test_in"
$outBase = "$root\_selftest\out"
$result = "$root\_selftest\model_test_results.txt"

Remove-Item "$outBase" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $in | Out-Null
& $py -c "from PIL import Image; import numpy as np; [Image.fromarray(np.full((360,640,3), 60+i*30, dtype=np.uint8)).save(r'$in\%08d.png'%(i+1)) for i in range(3)]"

$lines = New-Object System.Collections.Generic.List[string]

function Test-SR {
    param($name, $scale)
    $out = "$outBase\sr_$name"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # 注意：本工具版本忽略 -m，模型 .param/.bin 必须位于 exe 同目录 models 根级
    $err = & "$sr\realesrgan-ncnn-vulkan.exe" -i $in -o $out -n $name -s $scale -g 0 -j 1:1:1 2>&1 | Out-String
    $n = (Get-ChildItem $out -Filter *.png -ErrorAction SilentlyContinue).Count
    if ($n -ge 3) { $lines.Add("SR|PASS|$name|scale=$scale|frames=$n") }
    else { $lines.Add("SR|FAIL|$name|scale=$scale|frames=$n|ERR=$($err.Substring(0, [Math]::Min(200, $err.Length)))") }
}

function Test-Rife {
    param($name)
    $out = "$outBase\if_$name"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Push-Location $rife
    if ($name -like "rife-v4*") {
        $err = & ".\rife-ncnn-vulkan.exe" -i $in -o $out -m $name -g 0 -n 5 -j 1:1:1 2>&1 | Out-String
    } else {
        $err = & ".\rife-ncnn-vulkan.exe" -i $in -o $out -m $name -g 0 -j 1:1:1 2>&1 | Out-String
    }
    Pop-Location
    $n = (Get-ChildItem $out -Filter *.png -ErrorAction SilentlyContinue).Count
    if ($n -ge 3) { $lines.Add("IF|PASS|$name|frames=$n") }
    else { $lines.Add("IF|FAIL|$name|frames=$n|ERR=$($err.Substring(0, [Math]::Min(200, $err.Length)))") }
}

function Test-Offical {
    param($name)
    $modelDir = "$root\Tools\officalrife\models\$name"
    $out = "$outBase\of_$name"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $err = & $py $runPy -i $in -o $out -m $modelDir -mult 2 2>&1 | Out-String
    $n = (Get-ChildItem $out -Filter *.png -ErrorAction SilentlyContinue).Count
    if ($n -ge 3) { $lines.Add("OFF|PASS|$name|frames=$n") }
    else { $lines.Add("OFF|FAIL|$name|frames=$n|ERR=$($err.Substring(0, [Math]::Min(200, $err.Length)))") }
}

# SR models
$srTests = @(
    @("realesr-animevideov3-x2", 2),
    @("realesr-animevideov3-x3", 3),
    @("realesr-animevideov3-x4", 4),
    @("realesrgan-x4plus", 4),
    @("realesrgan-x4plus-anime", 4)
)
foreach ($t in $srTests) { Test-SR -name $t[0] -scale $t[1] }

# RIFE NCNN models
$rifeModels = Get-ChildItem $rife -Directory | Where-Object { $_.Name -like "rife-v*" -and (Test-Path "$($_.FullName)\flownet.param") } | Select-Object -ExpandProperty Name
foreach ($m in $rifeModels) { Test-Rife -name $m }

# Offical models
$offModels = Get-ChildItem "$root\Tools\officalrife\models" -Directory | Where-Object { (Test-Path "$($_.FullName)\flownet.pkl") -and ($_.Name -like "official_*" -or $_.Name -like "rpr_*") } | Select-Object -ExpandProperty Name
foreach ($m in $offModels) { Test-Offical -name $m }

$lines | Set-Content $result -Encoding UTF8
$pass = ($lines | Where-Object { $_ -match "\|PASS\|" }).Count
$fail = ($lines | Where-Object { $_ -match "\|FAIL\|" }).Count
Write-Output "TOTAL=$($lines.Count) PASS=$pass FAIL=$fail"
Write-Output "RESULT_FILE=$result"
