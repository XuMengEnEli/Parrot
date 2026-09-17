#!/usr/bin/env bash
# 下载 ECDICT 全量词表（约 77 万词 / 200MB）并导入本机 Parrot 数据库。
# 用法：bash scripts/fetch-ecdict.sh [已有的 csv 路径]
#   带参数时跳过下载，直接导入该文件。
# 源优先级：官方 raw → ghfast → ghproxy（直连 GitHub raw 不稳时兜底）。
# Windows 侧对应脚本：scripts/fetch-ecdict.ps1（WinRT OCR 的 TFM 不同，两份各管一头）。
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$repo_root/tools/ecdict.csv"
tfm="net10.0"
dll="$repo_root/src/Parrot.App/bin/Release/$tfm/Parrot.App.dll"

url_raw='https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv'

# 完整性判据：官方 csv 实测约 65MB；体积过小是错误页，末字节非换行则是中断的截断文件。
is_complete() {
  [ -f "$1" ] || return 1
  [ "$(wc -c <"$1")" -gt 40000000 ] || return 1
  [ "$(tail -c 1 "$1" | od -An -c | tr -d ' \n')" = '\n' ]
}

if [ $# -ge 1 ]; then
  out="$1"
  [ -f "$out" ] || { echo "文件不存在：$out" >&2; exit 1; }
  is_complete "$out" || { echo "文件不完整（应 >40MB 且整行收尾）：$out" >&2; exit 1; }
  echo "跳过下载，直接导入 $out"
else
  mkdir -p "$(dirname "$out")"
  ok=0
  for u in "$url_raw" "https://ghfast.top/$url_raw" "https://mirror.ghproxy.com/$url_raw"; do
    echo "尝试 $u"
    if curl -fL --connect-timeout 15 --max-time 1800 -o "$out" "$u" && is_complete "$out"; then
      ok=1
      break
    fi
    echo "  失败或文件不完整（疑似错误页/中断），换源"
  done
  if [ "$ok" -ne 1 ]; then
    echo "所有源均不可达。可在能直连的机器下载 ecdict.csv 后执行：bash scripts/fetch-ecdict.sh <该文件路径>" >&2
    echo "不导入也能用：应用内嵌 seed 词表（45 高频考研词）保证功能可用，只是生词命中率低。" >&2
    exit 1
  fi
fi

if [ ! -f "$dll" ]; then
  echo "先构建：dotnet build Parrot.slnx -c Release" >&2
  exit 1
fi

# 经 dotnet 主机跑 CLI（mac 上直接起 .app 里的可执行文件会带起 GUI 生命周期）
echo "导入中（77 万行单事务，约 1–2 分钟；这期间应用内的刷新会等锁，不会报错）…"
dotnet "$dll" --import-ecdict "$out"
