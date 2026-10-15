# SetupPythonRuntime.ps1
# 在開發機（有網路）執行一次，準備可攜式 Python 環境。
# 產出的 PythonRuntime/ 資料夾會隨 Engine 部署，目標機器不需安裝 Python。
#
# 用法：
#   .\SetupPythonRuntime.ps1                   # 自動下載 Python 3.11.9 embeddable
#   .\SetupPythonRuntime.ps1 -PythonZip "C:\Downloads\python-3.11.9-embed-amd64.zip"  # 使用本地 zip

param(
    [string]$PythonZip = "",
    [string]$PythonVersion = "3.11.9"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectDir = Split-Path -Parent $ScriptDir
$TargetDir = Join-Path $ProjectDir "PythonRuntime"
$RequirementsFile = Join-Path $ProjectDir "Algorithms\requirements.txt"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SCADA Engine - Python Runtime Setup"
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ─── Step 1: 取得 Python Embeddable ───
if (-not $PythonZip) {
    $zipName = "python-$PythonVersion-embed-amd64.zip"
    $downloadUrl = "https://www.python.org/ftp/python/$PythonVersion/$zipName"
    $PythonZip = Join-Path $env:TEMP $zipName

    if (-not (Test-Path $PythonZip)) {
        Write-Host "[1/5] Downloading Python $PythonVersion embeddable..." -ForegroundColor Yellow
        Invoke-WebRequest -Uri $downloadUrl -OutFile $PythonZip -UseBasicParsing
        Write-Host "  Downloaded: $PythonZip" -ForegroundColor Green
    } else {
        Write-Host "[1/5] Using cached: $PythonZip" -ForegroundColor Green
    }
} else {
    Write-Host "[1/5] Using local zip: $PythonZip" -ForegroundColor Green
}

if (-not (Test-Path $PythonZip)) {
    Write-Host "ERROR: Python zip not found: $PythonZip" -ForegroundColor Red
    exit 1
}

# ─── Step 2: 解壓縮 ───
Write-Host "[2/5] Extracting to $TargetDir ..." -ForegroundColor Yellow
if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force }
Expand-Archive -Path $PythonZip -DestinationPath $TargetDir -Force
Write-Host "  Extracted." -ForegroundColor Green

# ─── Step 3: 啟用 import site（讓 pip/site-packages 生效）───
Write-Host "[3/5] Enabling import site in ._pth file..." -ForegroundColor Yellow
$pthFile = Get-ChildItem -Path $TargetDir -Filter "python*._pth" | Select-Object -First 1
if ($pthFile) {
    $lines = Get-Content $pthFile.FullName
    $lines = $lines | ForEach-Object { if ($_ -eq '#import site') { 'import site' } else { $_ } }
    $lines | Set-Content -Path $pthFile.FullName -Encoding ASCII
    Write-Host "  Enabled: $($pthFile.Name)" -ForegroundColor Green
} else {
    Write-Host "  WARNING: ._pth file not found, pip may not work." -ForegroundColor Yellow
}

# ─── Step 4: 安裝 pip ───
Write-Host "[4/5] Installing pip..." -ForegroundColor Yellow
$pythonExe = Join-Path $TargetDir "python.exe"
$getPipUrl = "https://bootstrap.pypa.io/get-pip.py"
$getPipPath = Join-Path $env:TEMP "get-pip.py"

if (-not (Test-Path $getPipPath)) {
    Invoke-WebRequest -Uri $getPipUrl -OutFile $getPipPath -UseBasicParsing
}
& $pythonExe $getPipPath --no-warn-script-location 2>&1 | Out-Null
Write-Host "  pip installed." -ForegroundColor Green
# VUL-2026-005（2026-10-15 弱掃）：get-pip 帶的 pip/setuptools 可能落後安全修補，裝完立即升到最新
& $pythonExe -m pip install --upgrade pip setuptools wheel --no-warn-script-location --quiet
Write-Host "  pip/setuptools/wheel upgraded to latest." -ForegroundColor Green

# ─── Step 5: 安裝演算法依賴（fastapi, uvicorn）───
Write-Host "[5/5] Installing algorithm dependencies..." -ForegroundColor Yellow
if (Test-Path $RequirementsFile) {
    & $pythonExe -m pip install -r $RequirementsFile --no-warn-script-location --quiet
    Write-Host "  Dependencies installed from requirements.txt" -ForegroundColor Green
} else {
    & $pythonExe -m pip install fastapi uvicorn --no-warn-script-location --quiet
    Write-Host "  Installed fastapi + uvicorn" -ForegroundColor Green
}

# ─── 完成 ───
$totalSize = [math]::Round(((Get-ChildItem $TargetDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Setup complete!" -ForegroundColor Green
Write-Host "  Location: $TargetDir"
Write-Host "  Size: ${totalSize} MB"
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This folder will be deployed with Engine." -ForegroundColor Gray
Write-Host "Target machines do NOT need Python installed." -ForegroundColor Gray
