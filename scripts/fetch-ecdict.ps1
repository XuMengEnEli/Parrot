# 下载 ECDICT 全量词表并导入本机 Parrot 数据库。
# 用法：powershell -File scripts\fetch-ecdict.ps1
# 源优先级：官方 raw → ghproxy 加速镜像（部分网络直连 GitHub raw 不稳定）。约 65MB，需要几分钟。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repoRoot 'tools\ecdict.csv'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# 完整性判据：体积过小是错误页，末字节非换行则是中断的截断文件。
function Test-EcdictCsv([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    if ((Get-Item $path).Length -lt 40MB) { return $false }
    $fs = [System.IO.File]::OpenRead($path)
    try { $fs.Seek(-1, [System.IO.SeekOrigin]::End) | Out-Null; $fs.ReadByte() -eq 10 }
    finally { $fs.Dispose() }
}

$candidates = @(
    'https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv',
    'https://ghfast.top/https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv',
    'https://mirror.ghproxy.com/https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv'
)

$ok = $false
foreach ($u in $candidates) {
    Write-Host "尝试 $u"
    try {
        Invoke-WebRequest -Uri $u -OutFile $out -TimeoutSec 600
        if (Test-EcdictCsv $out) { $ok = $true; break }
        Write-Host "  文件不完整（疑似错误页或中断），换源"
    } catch { Write-Host "  失败：$($_.Exception.Message)" }
}
if (-not $ok) {
    Write-Host "所有源均不可达。可在能翻墙/直连的机器下载后手动放到 $out，再运行本脚本。" -ForegroundColor Red
    Write-Host "不下载也可以：应用内嵌 seed 词表（45 高频考研词）已保证功能可用。"
    exit 1
}

# Windows 上 App 使用带 WinRT 的专用 TFM（系统 OCR），输出目录随之变化
$tfm = if ($env:OS -eq 'Windows_NT') { 'net10.0-windows10.0.19041.0' } else { 'net10.0' }
$dll = Join-Path $repoRoot "src\Parrot.App\bin\Release\$tfm\Parrot.App.dll"
if (-not (Test-Path $dll)) {
    Write-Host "先发布/构建：dotnet build Parrot.slnx -c Release"
    exit 1
}
# 经 dotnet 主机跑 CLI（WinExe 直接起会脱离控制台、看不到进度输出）
& dotnet $dll --import-ecdict $out
