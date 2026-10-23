#!/bin/zsh
# macOS 版 BuildRelease.bat：Finder 雙擊即可在 Mac 上打包 Windows 部署包（Release/SCADA_Release_*）。
# 需求：.NET 8 SDK（~/.dotnet）+ PowerShell 7（dotnet tool，~/.dotnet/tools/pwsh）；
#       沒裝時依 docs/功能說明書_部署與遠端更新.md §在 macOS 上建包 的指令安裝。
# 參數原樣轉給 BuildRelease.ps1，例：./BuildRelease.command -NoPython
# 注意：Mac 不能執行 Windows python.exe，無法現場生成 PythonRuntime；首裝完整包請見同一節說明。
set -e
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
# dotnet tool 版 pwsh 是 apphost，靠 DOTNET_ROOT 找 ~/.dotnet 的 runtime（沒設會報 libhostfxr.dylib not found）
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "[ERROR] dotnet not found. Install .NET 8 SDK: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0"
  exit 1
fi
if ! command -v pwsh >/dev/null 2>&1; then
  echo "[ERROR] pwsh not found. Install: dotnet tool install --global PowerShell"
  exit 1
fi

pwsh -NoProfile -File "./BuildRelease.ps1" "$@"
