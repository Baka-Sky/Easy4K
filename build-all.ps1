# Easy4K 一键编译：Debug + Release 全量编译，并把 SFDP 动态库同步到 lib\Easy4K.SFDP
# 用法：双击仓库根目录的「一键编译.bat」，或
#       powershell -ExecutionPolicy Bypass -File build-all.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$app = Join-Path $root 'src\Easy4K\Easy4K.csproj'
$sfdp = Join-Path $root 'src\Easy4K.SFDP\Easy4K.SFDP.csproj'
$libDir = Join-Path $root 'lib\Easy4K.SFDP'

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "[失败] $msg" -ForegroundColor Red
    exit 1
}

# 正在运行的 Easy4K 会锁住 exe/dll，编译会报 MSB3021，先拦下来给个明确提示
if (Get-Process -Name Easy4K -ErrorAction SilentlyContinue) {
    Write-Host "[注意] Easy4K 正在运行，exe/dll 被占用，无法编译。" -ForegroundColor Yellow
    Write-Host "       请先关闭 Easy4K，再重新运行本工具。" -ForegroundColor Yellow
    exit 1
}

$configs = @('Debug', 'Release')
foreach ($cfg in $configs) {
    Write-Host ""
    Write-Host "== 编译 Easy4K.SFDP ($cfg) ==" -ForegroundColor Cyan
    dotnet build $sfdp -c $cfg -p:Platform=x64 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { Fail "Easy4K.SFDP $cfg 编译失败" }

    Write-Host "== 编译 Easy4K ($cfg) ==" -ForegroundColor Cyan
    dotnet build $app -c $cfg -p:Platform=x64 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { Fail "Easy4K $cfg 编译失败" }
}

# 把 Release 版动态库同步到 lib\Easy4K.SFDP（GitHub 上供别人直接下载的那份）
$dll = Get-ChildItem (Join-Path $root 'src\Easy4K.SFDP\bin') -Recurse -Filter 'Easy4K.SFDP.dll' |
    Where-Object { $_.FullName -match '\\Release\\' } | Select-Object -First 1
if (-not $dll) { Fail '没找到 Release 版 Easy4K.SFDP.dll（检查 src\Easy4K.SFDP 是否构建成功）' }
New-Item -ItemType Directory -Force -Path $libDir | Out-Null
Copy-Item $dll.FullName (Join-Path $libDir 'Easy4K.SFDP.dll') -Force

Write-Host ""
Write-Host "== 完成 ==" -ForegroundColor Green
foreach ($cfg in $configs) {
    $exe = Get-ChildItem (Join-Path $root 'src\Easy4K\bin') -Recurse -Filter 'Easy4K.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\$cfg\\" } | Select-Object -First 1
    if ($exe) {
        Write-Host ("  主程序 {0,-7} {1}" -f $cfg, $exe.FullName)
    }
}
$libDll = Join-Path $libDir 'Easy4K.SFDP.dll'
Write-Host ("  动态库          {0}  ({1:N1} KB)" -f $libDll, ((Get-Item $libDll).Length / 1KB))
