# SCADA 一鍵打包腳本 — 在開發機執行，產出可直接部署的資料夾
# 用法: .\BuildRelease.ps1            （完整包，含 Python 基礎包）
#       .\BuildRelease.ps1 -NoPython  （app-only 更新包，不含 Python；目標機沿用現有 Python）
# 產出: .\Release\SCADA_Release_yyyyMMdd_HHmm_vYY.A.B\
#       版號取自 repo 根目錄 Directory.Build.props <Version>（CLAUDE.md §版本號規則）
param([switch]$NoPython)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RootPath = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $RootPath) { $RootPath = (Get-Location).Path }
$DateTag = Get-Date -Format "yyyyMMdd_HHmm"
$BuildDate = Get-Date -Format "yyyy-MM-dd"

# 產品版號唯一真相來源：Directory.Build.props <Version>。同一份數字同時進 csproj（exe 檔案屬性、
# Web footer）與這裡（包名 + Install.bat 的 Windows 服務描述），改一處四處同步。
$propsPath = Join-Path $RootPath "Directory.Build.props"
[xml]$propsXml = Get-Content $propsPath -Raw -Encoding UTF8
$AppVersion = ($propsXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $AppVersion -or $AppVersion -notmatch '^\d{2}\.\d+\.\d+$') {
    Write-Host "Directory.Build.props <Version> missing or not YY.A.B (got '$AppVersion')" -ForegroundColor Red
    exit 1
}
# 包名保留 SCADA_Release_<日期> 前綴（QuickDeployAll.bat 以名稱倒序取最新，日期在前排序才正確），版號接尾
$ReleasePath = Join-Path $RootPath "Release\SCADA_Release_${DateTag}_v$AppVersion"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SCADA Release Builder" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Version: $AppVersion  (Directory.Build.props)"
Write-Host "Output:  $ReleasePath"
Write-Host ""

# Clean
if (Test-Path $ReleasePath) { Remove-Item $ReleasePath -Recurse -Force }
New-Item -ItemType Directory -Path $ReleasePath -Force | Out-Null

# ──────────────────────────────────────
# 1. Build Engine
# ──────────────────────────────────────
Write-Host "[1/6] Building Engine (framework-dependent, needs .NET 8 runtime on target)..." -ForegroundColor Yellow
$engineProject = Join-Path $RootPath "ScadaEngine.Engine"
dotnet publish $engineProject -c Release --self-contained false --runtime win-x64 -o "$ReleasePath\Engine\App" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "Engine build FAILED" -ForegroundColor Red; exit 1 }

# 移除 Engine\App 內未使用的 HASP 原生 dll（~40MB）。
# 驗狗由 net48 的 LicenseBridge 做（自帶 hasp_net_windows.dll + 原生 dll，另裝 C:\SCADA\LicenseBridge\），
# Engine 只用 NamedPipeClientStream 呼叫 bridge：編譯碼零 HASP DllImport、Engine\App 無受管包裝
# hasp_net_windows.dll、csproj 無 HASP 受管參考 → 這些原生 dll 從不被載入，是 bridge 前身遺留的死重。
# 樣式 hasp_windows* 不會誤刪 hasp_net_windows.dll（此處本就沒有），LicenseBridge\App 的那份不受影響。
$engHaspRemoved = 0
foreach ($pat in @("hasp_windows*.dll", "haspvlib*.dll", "apidsp_windows*.dll")) {
    Get-ChildItem "$ReleasePath\Engine\App" -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object {
        $engHaspRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}
if ($engHaspRemoved -gt 0) {
    Write-Host ("  Removed unused HASP native dlls from Engine\App ({0:N1} MB, LicenseBridge owns HASP)" -f ($engHaspRemoved / 1MB)) -ForegroundColor Gray
}

# Copy Engine configs
# Modbus / DBPoint 不列入複製清單 — 它們與 OpcUaPoint 都是「現場資料」而非產品，稍後統一清空
$engineConfigs = @("Setting", "MqttSetting", "DatabaseSchema", "Algorithms")
foreach ($dir in $engineConfigs) {
    $src = Join-Path $engineProject $dir
    if (Test-Path $src) {
        $dst = "$ReleasePath\Engine\App\$dir"
        if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
        Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force
    }
}

# dbSetting.json → 包根 Shared\（Install.bat 鋪到 C:\SCADA\Shared\dbSetting.json，Engine / Web / Gateway 共用一份）。
# Engine\App\Setting 不再帶複本：DbSettingLocator 第 2 層（..\..\Shared）本就贏過第 3 層（本機 Setting），
# 留複本只會讓現場改到一份永遠不被讀的檔。見 docs/架構.md §dbSetting.json 解析順序
$sharedDir = "$ReleasePath\Shared"
if (-not (Test-Path $sharedDir)) { New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null }
$dbSettingSrc = Join-Path $engineProject "Setting\dbSetting.json"
if (-not (Test-Path $dbSettingSrc)) { Write-Host "Engine Setting\dbSetting.json missing (needed as Shared template)" -ForegroundColor Red; exit 1 }
Copy-Item $dbSettingSrc -Destination "$sharedDir\dbSetting.json" -Force
Remove-Item "$ReleasePath\Engine\App\Setting\dbSetting.json" -Force -ErrorAction SilentlyContinue
Write-Host "  dbSetting.json -> package root Shared\ (installs to C:\SCADA\Shared, shared by Engine/Web/Gateway)" -ForegroundColor Gray

# 現場資料夾（Modbus / DBPoint / OpcUaPoint）：dotnet publish 會依 Engine csproj 的
# CopyToOutputDirectory 把 repo 的 dev 裝置定義（*.json）與範例（*.json.example）帶進 Engine\App。
# 這些是各站台自有的現場資料，不該隨 release 外流到客戶伺服器。作法：清掉整個資料夾內容，
# 只回填 repo 內的範本 / 產生工具：
#   - Modbus / DBPoint：空白 Excel 範本（*.xlsx）— 現場在 Web「Modbus 來源」「DB 來源」頁匯入，不再用巨集
#   - OpcUaPoint：仍附 Excel 產生工具（*.xlsm）
# 資料夾（含範本）都在，Install.bat 靠「Modbus 資料夾是否存在」判斷升級的哨兵照常運作，
# 升級時現場既有設定也照樣被備份/還原。
$siteToolPatterns = @{ "Modbus" = "*.xlsx"; "DBPoint" = "*.xlsx"; "OpcUaPoint" = "*.xlsm" }
foreach ($siteDir in @("Modbus", "DBPoint", "OpcUaPoint")) {
    $sitePath = "$ReleasePath\Engine\App\$siteDir"
    if (Test-Path $sitePath) { Remove-Item $sitePath -Recurse -Force }
    New-Item -ItemType Directory -Path $sitePath -Force | Out-Null
    # 只回填範本 / 產生工具，不帶任何 dev 裝置 json / 範例 / 測試檔
    $pattern = $siteToolPatterns[$siteDir]
    $toolSrc = Join-Path $engineProject "$siteDir\$pattern"
    if (Test-Path $toolSrc) {
        Copy-Item -Path $toolSrc -Destination $sitePath -Force
        Write-Host "  $siteDir : kept template/tool ($pattern), stripped dev data" -ForegroundColor Gray
    } else {
        Write-Host "  $siteDir : emptied (no template/tool found)" -ForegroundColor Gray
    }
}

# Copy Engine scripts
Copy-Item -Path (Join-Path $engineProject "Scripts\DeployService.ps1") -Destination "$ReleasePath\Engine\" -Force
Copy-Item -Path (Join-Path $engineProject "Scripts\QuickDeploy.bat") -Destination "$ReleasePath\Engine\" -Force

# Copy Python setup files (get-pip.py + SetupPythonRuntime.ps1)
$getPip = Join-Path $engineProject "Scripts\get-pip.py"
if (Test-Path $getPip) { Copy-Item $getPip -Destination "$ReleasePath\Engine\" -Force }
$setupPy = Join-Path $engineProject "Scripts\SetupPythonRuntime.ps1"
if (Test-Path $setupPy) { Copy-Item $setupPy -Destination "$ReleasePath\Engine\" -Force }

# PythonRuntime → 獨立「基礎包」（放 package 根目錄，非 Engine\App），帶 VERSION 版號。
# 理由：Python 62MB 幾乎不變，抽出後 Install.bat 依 VERSION 比對，只在缺或版本變更才鋪，
#       常態 app 更新可用 -NoPython 產出不含 Python 的小包。Engine 執行期仍讀 App\PythonRuntime
#       （Worker.ResolvePythonExe 找 BaseDir\PythonRuntime\python.exe），故 Install.bat 會鋪到該處。
if ($NoPython) {
    Write-Host "  PythonRuntime SKIPPED (-NoPython): app-only package, target keeps its existing Python" -ForegroundColor Yellow
} else {
    $pythonRuntime = Join-Path $engineProject "PythonRuntime"
    if (-not (Test-Path (Join-Path $pythonRuntime "python.exe"))) {
        Write-Host "  PythonRuntime not found - running SetupPythonRuntime.ps1 (requires internet)..." -ForegroundColor Yellow
        & (Join-Path $engineProject "Scripts\SetupPythonRuntime.ps1")
        if (-not (Test-Path (Join-Path $pythonRuntime "python.exe"))) {
            Write-Host "PythonRuntime setup FAILED - LogicFlow algorithm nodes will not work on target machines" -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "  Copying PythonRuntime as base bundle (package root)..." -ForegroundColor Gray
    $pyDst = "$ReleasePath\PythonRuntime"
    if (-not (Test-Path $pyDst)) { New-Item -ItemType Directory -Path $pyDst -Force | Out-Null }
    Copy-Item -Path "$pythonRuntime\*" -Destination $pyDst -Recurse -Force
    # 版號檔（VERSION）：pyver + 檔數 + 總位元組。改 Python 或 pip 套件 → 三者任一變 → 版號變，
    # Install.bat 觸發重鋪；否則升級時跳過那 62MB copy。VERSION 於計數後才寫入，故不計入自身。
    $pyExe = Join-Path $pyDst "python.exe"
    $pyVer = (Get-Item $pyExe).VersionInfo.ProductVersion
    $pyFiles = Get-ChildItem $pyDst -Recurse -File
    $pyCount = $pyFiles.Count
    $pyBytes = ($pyFiles | Measure-Object -Property Length -Sum).Sum
    $pyVerStr = "$pyVer" + "_" + $pyCount + "_" + $pyBytes
    $pyVerStr | Set-Content -Path "$pyDst\VERSION" -Encoding ASCII -NoNewline
    Write-Host "  PythonRuntime base bundle OK (VERSION=$pyVerStr)" -ForegroundColor Green
}

# LineSetting.json：正式檔含 token 不進 git，打包時若缺就用範本補上（placeholder token 會被程式視為未設定、安全停用）
$lineSettingDst = "$ReleasePath\Engine\App\Setting\LineSetting.json"
$lineSettingExample = Join-Path $engineProject "Setting\LineSetting.example.json"
if (-not (Test-Path $lineSettingDst) -and (Test-Path $lineSettingExample)) {
    Copy-Item $lineSettingExample -Destination $lineSettingDst -Force
    Write-Host "  LineSetting.json created from example (fill token on-site to enable Line notify)" -ForegroundColor Gray
}

Write-Host "  Engine OK" -ForegroundColor Green

# ──────────────────────────────────────
# 2. Build Web
# ──────────────────────────────────────
Write-Host "[2/6] Building Web (framework-dependent, needs ASP.NET Core 8 runtime on target)..." -ForegroundColor Yellow
$webProject = Join-Path $RootPath "ScadaEngine.Web"
dotnet publish $webProject -c Release --self-contained false --runtime win-x64 -o "$ReleasePath\Web\App" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "Web build FAILED" -ForegroundColor Red; exit 1 }

# 移除 Web publish 遞移帶入的 Engine 設定資料夾（死檔，避免工程師誤改）
# Web ProjectReference 到 Engine，Engine .csproj 把 Modbus/DBPoint/OpcUaPoint 的 *.json 標了
# CopyToOutputDirectory，SDK 會沿引用把它們複製進 Web 輸出。但 Web 實際讀的是
# appsettings.json 的 WatchedFolder（= C:\SCADA\Engine\App\...），Web\App 下這幾份是死的，
# 留著只會讓現場工程師誤以為要改這裡。刪掉，保持 Web\App 乾淨。
foreach ($deadDir in @("Modbus", "DBPoint", "OpcUaPoint")) {
    $deadPath = "$ReleasePath\Web\App\$deadDir"
    if (Test-Path $deadPath) {
        Remove-Item $deadPath -Recurse -Force
        Write-Host "  Removed dead $deadDir folder from Web\App (Engine owns it)" -ForegroundColor Gray
    }
}

# 移除 Web publish 經 Engine ProjectReference 拖進來的 HASP 原生 dll（~32MB）。
# Web 不驗加密狗（那是 Engine 透過 LicenseBridge Named Pipe 做的），這些 x86/x64 × 3 種狗版本
# 對 Web 是純死重、且是原生 dll（只被 Engine license 路徑的 P/Invoke 載入，Web 從不呼叫），安全可刪。
$webHaspRemoved = 0
foreach ($pat in @("hasp_windows*.dll", "haspvlib*.dll", "apidsp_windows*.dll")) {
    Get-ChildItem "$ReleasePath\Web\App" -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object {
        $webHaspRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}
if ($webHaspRemoved -gt 0) {
    Write-Host ("  Removed HASP native dlls from Web\App ({0:N1} MB, Engine-only)" -f ($webHaspRemoved / 1MB)) -ForegroundColor Gray
}

# Copy Web configs
$webMqtt = Join-Path $webProject "MqttSetting\MqttSetting.json"
$webMqttDst = "$ReleasePath\Web\App\MqttSetting"
if (-not (Test-Path $webMqttDst)) { New-Item -ItemType Directory -Path $webMqttDst -Force | Out-Null }
if (Test-Path $webMqtt) { Copy-Item $webMqtt -Destination "$webMqttDst\MqttSetting.json" -Force }

# dbSetting.json 不進 Web\App\Setting — Web 改讀 C:\SCADA\Shared\dbSetting.json（與 Engine 同一份，見上方 Engine 段）。
# 以前這裡從 Engine 複製一份給 Web，升級時兩份各自備份/還原，一旦分家就永久分家（2026-10-06 現場即如此）。
Remove-Item "$ReleasePath\Web\App\Setting\dbSetting.json" -Force -ErrorAction SilentlyContinue

# Copy DatabaseSchema from Engine
$schemaFile = Join-Path $engineProject "DatabaseSchema\DatabaseSchema.json"
$webSchemaDir = "$ReleasePath\Web\App\DatabaseSchema"
if (-not (Test-Path $webSchemaDir)) { New-Item -ItemType Directory -Path $webSchemaDir -Force | Out-Null }
if (Test-Path $schemaFile) { Copy-Item $schemaFile -Destination "$webSchemaDir\DatabaseSchema.json" -Force }

# Copy Algorithms from Engine（Web LogicFlow 需要讀取演算法清單）
$algoSrc = Join-Path $engineProject "Algorithms"
if (Test-Path $algoSrc) {
    $algoDst = "$ReleasePath\Web\App\Algorithms"
    if (-not (Test-Path $algoDst)) { New-Item -ItemType Directory -Path $algoDst -Force | Out-Null }
    Copy-Item -Path "$algoSrc\*" -Destination $algoDst -Recurse -Force
    Write-Host "  Algorithms copied to Web" -ForegroundColor Gray
}

# Copy Web scripts
Copy-Item -Path (Join-Path $webProject "Scripts\DeployWebService.ps1") -Destination "$ReleasePath\Web\" -Force
Copy-Item -Path (Join-Path $webProject "Scripts\QuickDeploy.bat") -Destination "$ReleasePath\Web\" -Force

# 內網 HTTPS 憑證腳本 → Web\App\certs\（Program.cs 由 ContentRoot\certs 讀 pfx，故腳本須落在 App\certs）
# 只帶「腳本」，不帶任何 pfx/crt（憑證由部署機現產；升級時 xcopy 不刪除既有憑證故保留）。詳見 docs/功能說明書_內網HTTPS部署.md
$certScriptSrc = Join-Path $webProject "certs"
$certScriptDst = "$ReleasePath\Web\App\certs"
if (Test-Path $certScriptSrc) {
    if (-not (Test-Path $certScriptDst)) { New-Item -ItemType Directory -Path $certScriptDst -Force | Out-Null }
    Copy-Item -Path "$certScriptSrc\*.ps1" -Destination $certScriptDst -Force
    Write-Host "  HTTPS cert scripts copied to Web\App\certs (no pfx/crt bundled)" -ForegroundColor Gray
}

Write-Host "  Web OK" -ForegroundColor Green

# 工程師工具不落地客戶伺服器：reset-engineer-password.ps1 移到包根目錄（隨工程師 USB 攜帶使用），
# 並自兩個 App\Setting 移除 — 留在伺服器上等於任何能碰檔案系統的人一鍵重設 engineer 密碼
$resetTool = "$ReleasePath\Engine\App\Setting\reset-engineer-password.ps1"
if (Test-Path $resetTool) { Copy-Item $resetTool -Destination "$ReleasePath\reset-engineer-password.ps1" -Force }
Remove-Item "$ReleasePath\Engine\App\Setting\reset-engineer-password.ps1", "$ReleasePath\Web\App\Setting\reset-engineer-password.ps1" -Force -ErrorAction SilentlyContinue
Write-Host "  reset-engineer-password.ps1 moved to package root (engineer tool, not installed on server)" -ForegroundColor Gray

# ──────────────────────────────────────
# 3. Build ModbusServer (optional gateway, NOT installed by main Install.bat)
# ──────────────────────────────────────
Write-Host "[3/6] Building ModbusServer gateway (framework-dependent, needs ASP.NET Core 8 runtime on target)..." -ForegroundColor Yellow
$modbusProject = Join-Path $RootPath "ScadaEngine.ModbusServer"
dotnet publish $modbusProject -c Release --self-contained false --runtime win-x64 -o "$ReleasePath\ModbusServer\App" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "ModbusServer build FAILED" -ForegroundColor Red; exit 1 }

# Copy ModbusServer configs (publish already carries them, copy again to be explicit like Engine)
foreach ($dir in @("Setting", "MqttSetting", "Web")) {
    $src = Join-Path $modbusProject $dir
    if (Test-Path $src) {
        $dst = "$ReleasePath\ModbusServer\App\$dir"
        if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
        Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force
    }
}

# Gateway 包內只帶 dbSetting.example.json：同機安裝讀 C:\SCADA\Shared\，異機安裝由 InstallModbusServer.bat 從 example 鋪本機 Setting\dbSetting.json
Remove-Item "$ReleasePath\ModbusServer\App\Setting\dbSetting.json" -Force -ErrorAction SilentlyContinue

# Copy ModbusServer scripts
Copy-Item -Path (Join-Path $modbusProject "Scripts\DeployModbusServer.ps1") -Destination "$ReleasePath\ModbusServer\" -Force
Copy-Item -Path (Join-Path $modbusProject "Scripts\QuickDeploy.bat") -Destination "$ReleasePath\ModbusServer\" -Force

Write-Host "  ModbusServer OK" -ForegroundColor Green

# ──────────────────────────────────────
# 4. Build LicenseBridge (net48 HASP bridge — installed by Install.bat)
# ──────────────────────────────────────
# net48 x86 單一目標，不支援 --self-contained / --runtime（靠目標機內建 .NET Framework 4.8）。
# HASP runtime dll 由 csproj 的 CopyToOutputDirectory 帶入輸出目錄，無需外網。
Write-Host "[4/6] Building LicenseBridge (net48 HASP bridge)..." -ForegroundColor Yellow
$bridgeProject = Join-Path $RootPath "ScadaEngine.LicenseBridge"
dotnet publish $bridgeProject -c Release -o "$ReleasePath\LicenseBridge\App" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "LicenseBridge build FAILED" -ForegroundColor Red; exit 1 }

# 驗證 HASP runtime dll 確實落在輸出（少了狗就驗不了證）
$haspProbe = "$ReleasePath\LicenseBridge\App\hasp_net_windows.dll"
if (-not (Test-Path $haspProbe)) { Write-Host "LicenseBridge missing HASP runtime dll" -ForegroundColor Red; exit 1 }
Write-Host "  LicenseBridge OK (net48 + HASP runtime)" -ForegroundColor Green

# ──────────────────────────────────────
# 5. Create install script for on-site
# ──────────────────────────────────────
Write-Host "[5/6] Creating install scripts..." -ForegroundColor Yellow

# On-site install script (simplified, no build needed)
@"
@echo off
echo ========================================
echo   JOULARIS On-Site Installer  v$AppVersion
echo ========================================
echo.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo [ERROR] Please run as Administrator
    pause
    exit /b 1
)

:: ── Prerequisite: .NET Framework 4.8 (required by License Bridge) ──
:: Engine/Web/ModbusServer are self-contained .NET 8 and carry their own runtime,
:: but the HASP License Bridge is net48 and relies on the OS having 4.8 (Release>=528040).
:: Win11 / Server 2022 ship it; Server 2016/2019 / old Win10 do NOT. 4.8 is a ~120MB MS
:: redistributable that can't be xcopy'd, so we DETECT and STOP (not bundle it) — abort
:: before touching anything so the machine stays untouched until 4.8 is installed.
set "_REL="
for /f "tokens=3" %%r in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find "Release"') do set /a _REL=%%r
if not defined _REL goto NET48_MISSING
if %_REL% GEQ 528040 goto NET48_OK
:NET48_MISSING
echo ========================================
echo   [ERROR] .NET Framework 4.8 is NOT installed
echo ========================================
echo   The HASP License Bridge (net48) needs .NET Framework 4.8, and without a
echo   working License Bridge the Engine PAUSES Modbus data collection.
echo   (Engine/Web are self-contained .NET 8 and do NOT need it - only License does.)
echo.
echo   FIX: install .NET Framework 4.8 (or newer), reboot, then re-run Install.bat.
echo   Offline installer (put on the USB for air-gapped sites):
echo     https://dotnet.microsoft.com/download/dotnet-framework/net48
echo.
echo   Nothing was installed. Aborting.
echo.
pause
exit /b 1
:NET48_OK
echo [OK] .NET Framework 4.8+ detected (Release=%_REL%).
echo.

:: ── Prerequisite: .NET 8 shared runtime (Engine/Web/ModbusServer are framework-dependent) ──
:: To keep the package small the three .NET apps ship WITHOUT their own runtime, so the
:: target must have it installed. ONE package covers all three: "ASP.NET Core Runtime 8.0.x"
:: bundles the base .NET runtime AND the ASP.NET Core shared framework. Roll-forward is
:: same-major only, so we require 8.x specifically (a box with only .NET 9 will NOT run these).
:: Detect and STOP before touching anything.
where dotnet >nul 2>&1
if errorlevel 1 goto DOTNET_MISSING
dotnet --list-runtimes 2>nul | find "Microsoft.AspNetCore.App 8." >nul
if errorlevel 1 goto DOTNET_MISSING
dotnet --list-runtimes 2>nul | find "Microsoft.NETCore.App 8." >nul
if errorlevel 1 goto DOTNET_MISSING
goto DOTNET_OK
:DOTNET_MISSING
echo ========================================
echo   [ERROR] .NET 8 runtime is NOT installed
echo ========================================
echo   Engine/Web/ModbusServer are framework-dependent .NET 8 apps and need the shared
echo   runtime on this machine. Install ONE package that covers all three:
echo.
echo     ASP.NET Core Runtime 8.0.x (x64)   ^<-- includes the base .NET runtime too
echo     https://dotnet.microsoft.com/download/dotnet/8.0  (pick "ASP.NET Core Runtime", Windows x64)
echo.
echo   Offline installer (put on the USB for air-gapped sites): same page, "Hosting Bundle" or
echo   "ASP.NET Core Runtime" x64 installer.
echo   NOTE: a machine with only .NET 9 will NOT work - 8.x is required (no cross-major roll-forward).
echo.
echo   Nothing was installed. Aborting.
echo.
pause
exit /b 1
:DOTNET_OK
echo [OK] .NET 8 runtime detected (base + ASP.NET Core).
echo.

:: ── Detect upgrade: backup site-specific configs ──
set "_BACKUP=C:\SCADA\_ConfigBackup"
set "_IS_UPGRADE=0"

if exist "C:\SCADA\Engine\App\Modbus" (
    set "_IS_UPGRADE=1"
    echo [INFO] Detected existing installation — preserving site config...
    echo.
    if not exist "%_BACKUP%" mkdir "%_BACKUP%"
    xcopy /E /I /Y "C:\SCADA\Engine\App\Modbus"      "%_BACKUP%\Engine\Modbus"      >nul
    xcopy /E /I /Y "C:\SCADA\Engine\App\Setting"      "%_BACKUP%\Engine\Setting"      >nul
    xcopy /E /I /Y "C:\SCADA\Engine\App\MqttSetting"  "%_BACKUP%\Engine\MqttSetting"  >nul
    xcopy /E /I /Y "C:\SCADA\Engine\App\DBPoint"      "%_BACKUP%\Engine\DBPoint"      >nul
    if exist "C:\SCADA\Engine\App\OpcUaPoint" xcopy /E /I /Y "C:\SCADA\Engine\App\OpcUaPoint" "%_BACKUP%\Engine\OpcUaPoint" >nul
)
if exist "C:\SCADA\Web\App\Setting" (
    if not exist "%_BACKUP%" mkdir "%_BACKUP%"
    xcopy /E /I /Y "C:\SCADA\Web\App\Setting"         "%_BACKUP%\Web\Setting"         >nul
    xcopy /E /I /Y "C:\SCADA\Web\App\MqttSetting"     "%_BACKUP%\Web\MqttSetting"     >nul
)

echo [1/8] Stopping existing services (if any)...
echo.
net stop JoularisEngine >nul 2>&1
net stop JoularisWeb >nul 2>&1
net stop JoularisLicense >nul 2>&1

:: -- Migrate legacy service names (pre-v26.1.0: ScadaEngineService / ScadaWebService / ScadaEngineLicense) --
:: Windows cannot rename a service: stop + delete the old registration, the new-name create below
:: re-registers the same exe. Without this the old and new service would BOTH point at the same exe
:: and both auto-start -> Web fights over port 5038, Engine collects twice.
:: NOTE: if services.msc is open, "sc delete" only MARKS the old service for deletion (it disappears
:: after reboot); the new-name service is still created and started fine.
for %%S in (ScadaEngineService ScadaWebService ScadaEngineLicense) do (
    sc query %%S >nul 2>&1
    if not errorlevel 1 (
        echo [MIGRATE] Removing legacy service %%S ^(replaced by JOULARIS service names^)...
        net stop %%S >nul 2>&1
        sc delete %%S >nul 2>&1
    )
)

echo [2/8] Installing Engine...
echo.
:: -- Clean stale root files before deploy (root cause of self-contained -> framework-dependent breakage) --
:: Old self-contained installs left the whole .NET runtime (coreclr.dll, hostpolicy.dll, System.*.dll)
:: in the app root; xcopy overlay does NOT remove them, and they make the new framework-dependent exe
:: fail to start. Delete only root-level FILES here (subfolders are preserved: PythonRuntime, Setting,
:: Modbus, DBPoint, OpcUaPoint, MqttSetting, Algorithms). The xcopy below re-supplies all app files;
:: site config was already backed up above and is restored later.
if exist "C:\SCADA\Engine\App" for %%F in ("C:\SCADA\Engine\App\*") do del /Q "%%F" >nul 2>&1
xcopy /E /I /Y "%~dp0Engine\App" "C:\SCADA\Engine\App"

:: ── Python runtime base bundle (version-gated) ──
:: Python is a separate 62MB base bundle at the package root (not inside Engine\App).
:: Engine reads it at C:\SCADA\Engine\App\PythonRuntime (Worker.ResolvePythonExe). The
:: Engine xcopy above does NOT carry or purge it, so we (re)deploy here only when the
:: bundle's VERSION differs from what's installed. -NoPython packages omit the bundle:
:: we then keep the existing Python and only warn if none is present.
set "_PYSRC=%~dp0PythonRuntime"
set "_PYDST=C:\SCADA\Engine\App\PythonRuntime"
if not exist "%_PYSRC%\VERSION" goto PY_NOBUNDLE
if not exist "%_PYDST%\VERSION" goto PY_DEPLOY
fc /b "%_PYSRC%\VERSION" "%_PYDST%\VERSION" >nul 2>&1
if errorlevel 1 goto PY_DEPLOY
echo   Python runtime unchanged - skipped.
goto PY_DONE
:PY_DEPLOY
echo   Deploying Python runtime base bundle (62MB, first install or version change)...
if exist "%_PYDST%" rmdir /S /Q "%_PYDST%"
xcopy /E /I /Y "%_PYSRC%" "%_PYDST%" >nul
goto PY_DONE
:PY_NOBUNDLE
if exist "%_PYDST%\python.exe" (
    echo   [INFO] App-only package - keeping existing Python runtime.
) else (
    echo   [WARN] No Python bundle in package and none installed - LogicFlow Python nodes will not work.
    echo          Re-run a full build ^(BuildRelease.ps1 without -NoPython^) to include it.
)
:PY_DONE
echo.
:: Service name = machine identifier (stable, no spaces). DisplayName / description = for humans:
:: role (Backend / Frontend), product version (Directory.Build.props) and package build date.
sc create JoularisEngine binPath= "\"C:\SCADA\Engine\App\ScadaEngine.Engine.exe\"" DisplayName= "JOULARIS Engine" start= auto
:: sc create is a no-op when the service already exists (upgrade); force start type back
:: to auto so a previously disabled service doesn't survive the reinstall and block net start
sc config JoularisEngine start= auto >nul
sc description JoularisEngine "JOULARIS Engine | Backend - data collection (Modbus/DB/OPC UA -> MQTT/SQL) | v$AppVersion | $BuildDate"
sc failure JoularisEngine reset= 86400 actions= restart/5000/restart/10000/restart/30000
echo Engine installed.
echo.

echo [3/8] Installing Web...
echo.
:: -- Clean stale root files before deploy (same reason as Engine above) --
if exist "C:\SCADA\Web\App" for %%F in ("C:\SCADA\Web\App\*") do del /Q "%%F" >nul 2>&1
xcopy /E /I /Y "%~dp0Web\App" "C:\SCADA\Web\App"
sc create JoularisWeb binPath= "\"C:\SCADA\Web\App\ScadaEngine.Web.exe\"" DisplayName= "JOULARIS Web" start= auto
sc config JoularisWeb start= auto >nul
sc description JoularisWeb "JOULARIS Web | Frontend - web dashboard (http://0.0.0.0:5038, https 7189) | v$AppVersion | $BuildDate"
sc failure JoularisWeb reset= 86400 actions= restart/5000/restart/10000/restart/30000
echo Web installed.
echo.

echo [4/8] Installing License Bridge (HASP)...
echo.
:: net48 x86 bridge; exe path is hard-coded in Engine as C:\SCADA\LicenseBridge\ (no \App subfolder)
xcopy /E /I /Y "%~dp0LicenseBridge\App" "C:\SCADA\LicenseBridge"
sc create JoularisLicense binPath= "\"C:\SCADA\LicenseBridge\ScadaEngine.LicenseBridge.exe\"" DisplayName= "JOULARIS License Bridge" start= auto
sc config JoularisLicense start= auto >nul
sc description JoularisLicense "JOULARIS License Bridge | Backend - 32-bit HASP verification (Named Pipe, Engine only) | v$AppVersion | $BuildDate"
sc failure JoularisLicense reset= 86400 actions= restart/5000/restart/10000/restart/30000
echo License Bridge installed.
echo   NOTE: still needs the HASP USB dongle plugged in + Sentinel runtime driver on this server.
echo.

:: ── Restore site-specific configs ──
if "%_IS_UPGRADE%"=="1" (
    echo [INFO] Restoring site config...
    xcopy /E /I /Y "%_BACKUP%\Engine\Modbus"      "C:\SCADA\Engine\App\Modbus"      >nul
    xcopy /E /I /Y "%_BACKUP%\Engine\Setting"      "C:\SCADA\Engine\App\Setting"      >nul
    xcopy /E /I /Y "%_BACKUP%\Engine\MqttSetting"  "C:\SCADA\Engine\App\MqttSetting"  >nul
    if exist "%_BACKUP%\Engine\DBPoint" xcopy /E /I /Y "%_BACKUP%\Engine\DBPoint" "C:\SCADA\Engine\App\DBPoint" >nul
    if exist "%_BACKUP%\Engine\OpcUaPoint" xcopy /E /I /Y "%_BACKUP%\Engine\OpcUaPoint" "C:\SCADA\Engine\App\OpcUaPoint" >nul
    if exist "%_BACKUP%\Web\Setting" (
        xcopy /E /I /Y "%_BACKUP%\Web\Setting"     "C:\SCADA\Web\App\Setting"         >nul
        xcopy /E /I /Y "%_BACKUP%\Web\MqttSetting" "C:\SCADA\Web\App\MqttSetting"     >nul
    )
    :: Setting restore is for site JSON configs only; *.ps1 scripts are part of the app,
    :: always take the package version (otherwise old scripts get restored over new ones)
    copy /Y "%~dp0Engine\App\Setting\*.ps1" "C:\SCADA\Engine\App\Setting\" >nul
    if exist "C:\SCADA\Web\App\Setting" copy /Y "%~dp0Web\App\Setting\*.ps1" "C:\SCADA\Web\App\Setting\" >nul
    rmdir /S /Q "%_BACKUP%"
    :: Engineer tool must never live on the server (anyone with file access could reset the engineer password)
    del /Q "C:\SCADA\Engine\App\Setting\reset-engineer-password.ps1" 2>nul
    del /Q "C:\SCADA\Web\App\Setting\reset-engineer-password.ps1" 2>nul
    echo [INFO] Site config restored successfully.
    echo.
)

:: -- dbSetting.json: ONE machine-wide copy at C:\SCADA\Shared (read by Engine, Web and Modbus Gateway) --
:: All three apps resolve it the same way (DbSettingLocator): env JOULARIS_DBSETTING -> <App>\..\..\Shared
:: -> <App>\Setting -> dev repo. Shared lives OUTSIDE every <App>\App folder, so upgrades never touch it
:: and it no longer needs the backup/restore dance.
::   upgrade from the old layout : MOVE the site's Engine copy (just restored above) to Shared
::   fresh install               : write the package template
::   Shared already present      : leave it alone
:: Afterwards delete the per-app copies so nobody edits a file that is no longer read.
if not exist "C:\SCADA\Shared" mkdir "C:\SCADA\Shared"
if exist "C:\SCADA\Shared\dbSetting.json" goto DBSETTING_DONE
if exist "C:\SCADA\Engine\App\Setting\dbSetting.json" (
    echo [MIGRATE] Moving Engine\App\Setting\dbSetting.json to C:\SCADA\Shared\dbSetting.json ...
    move /Y "C:\SCADA\Engine\App\Setting\dbSetting.json" "C:\SCADA\Shared\dbSetting.json" >nul
    goto DBSETTING_DONE
)
echo [INFO] First install - writing DB connection template to C:\SCADA\Shared\dbSetting.json
copy /Y "%~dp0Shared\dbSetting.json" "C:\SCADA\Shared\dbSetting.json" >nul
:DBSETTING_DONE
if exist "C:\SCADA\Engine\App\Setting\dbSetting.json" del /Q "C:\SCADA\Engine\App\Setting\dbSetting.json"
if exist "C:\SCADA\Web\App\Setting\dbSetting.json" del /Q "C:\SCADA\Web\App\Setting\dbSetting.json"
echo [OK] DB connection file: C:\SCADA\Shared\dbSetting.json
echo.

:: ── Database setup: create DB if missing + backup folder ACL + app login ──
:: idempotent; runs AFTER the dbSetting migration above so it reads C:\SCADA\Shared\dbSetting.json
echo [5/8] Database setup...
powershell -ExecutionPolicy Bypass -File "C:\SCADA\Engine\App\Setting\install-db.ps1"
if %errorLevel% NEQ 0 (
    echo [WARN] Database setup reported errors. Engine startup has a fallback,
    echo        but weekly backup needs the folders. Re-run manually if needed:
    echo        powershell -ExecutionPolicy Bypass -File C:\SCADA\Engine\App\Setting\install-db.ps1
)
echo.

echo [6/8] Generating internal HTTPS certificate (CA + server cert)...
echo.
if exist "C:\SCADA\Web\App\certs\scada-web.pfx" (
    echo [INFO] Server certificate already exists - reusing.
    echo        Re-run generate-https-cert.ps1 manually if the server IP changed.
) else (
    powershell -ExecutionPolicy Bypass -File "C:\SCADA\Web\App\certs\generate-https-cert.ps1"
    if errorlevel 1 (
        echo [WARN] Certificate generation failed - Web will fall back to HTTP-only on 5038.
        echo        Re-run manually: powershell -ExecutionPolicy Bypass -File C:\SCADA\Web\App\certs\generate-https-cert.ps1
    )
)
:: Assemble client CA installer bundle for easy distribution to each browsing PC
if exist "C:\SCADA\Web\App\certs\ScadaEngine-CA.crt" (
    if not exist "C:\SCADA\ClientCA_Installer" mkdir "C:\SCADA\ClientCA_Installer"
    copy /Y "C:\SCADA\Web\App\certs\ScadaEngine-CA.crt"       "C:\SCADA\ClientCA_Installer\" >nul
    copy /Y "C:\SCADA\Web\App\certs\install-ca-on-client.ps1" "C:\SCADA\ClientCA_Installer\" >nul
    echo [OK] Client CA installer ready: C:\SCADA\ClientCA_Installer
    echo      Copy that folder to each client PC, run install-ca-on-client.ps1 as Admin.
)
echo.

echo [7/8] Opening firewall ports 5038 (HTTP) and 7189 (HTTPS)...
:: rule names renamed to JOULARIS (pre-v26.1.0: "ScadaEngine Web*"); delete old + any duplicate of the new before adding
netsh advfirewall firewall delete rule name="ScadaEngine Web" >nul 2>&1
netsh advfirewall firewall delete rule name="ScadaEngine Web HTTPS" >nul 2>&1
:: also the names the old HTTPS doc told people to add by hand
netsh advfirewall firewall delete rule name="ScadaEngine Web 5038" >nul 2>&1
netsh advfirewall firewall delete rule name="ScadaEngine Web 7189" >nul 2>&1
netsh advfirewall firewall delete rule name="JOULARIS Web" >nul 2>&1
netsh advfirewall firewall delete rule name="JOULARIS Web HTTPS" >nul 2>&1
netsh advfirewall firewall add rule name="JOULARIS Web" dir=in action=allow protocol=TCP localport=5038
netsh advfirewall firewall add rule name="JOULARIS Web HTTPS" dir=in action=allow protocol=TCP localport=7189
echo.

echo [8/8] Starting services...
net start JoularisLicense
net start JoularisEngine
net start JoularisWeb
echo.

echo ========================================
echo   Installation Complete!  JOULARIS v$AppVersion
echo   Web URL: http://localhost:5038
echo   First login: use the engineer account set during install, or open
echo   http://localhost:5038 on this server to create the first Admin.
echo ========================================
echo.
if "%_IS_UPGRADE%"=="1" (
    echo [OK] Site config was PRESERVED ^(Modbus, Setting, MqttSetting, DBPoint, OpcUaPoint^).
    echo      DB connection file is C:\SCADA\Shared\dbSetting.json ^(shared by Engine/Web/Gateway^).
) else (
    echo [NOTE] First install — edit config files as needed:
    echo   C:\SCADA\Shared\dbSetting.json                  ^(DB connection - ONE file for Engine/Web/Gateway^)
    echo   C:\SCADA\Engine\App\Modbus\*.json                ^(Modbus devices^)
    echo   C:\SCADA\Engine\App\MqttSetting\MqttSetting.json ^(MQTT broker^)
    echo   C:\SCADA\Engine\App\DBPoint\*.json               ^(DB source points^)
    echo   C:\SCADA\Engine\App\Setting\DbMaintenanceSetting.json ^(weekly DB backup schedule^)
)
echo.
echo [HTTPS] HTTPS is now ENABLED. http://server-ip:5038 auto-redirects to https://server-ip:7189.
echo   - Until each client installs the CA, browsers show a certificate warning (click-through still works).
echo   - To remove the warning: copy the C:\SCADA\ClientCA_Installer folder to each client PC,
echo     run install-ca-on-client.ps1 as Administrator, then fully restart the browser.
echo   - Connect using the server IP printed in the certs output above (must match the certificate SAN).
echo   - To disable HTTPS (HTTP-only on 5038): delete C:\SCADA\Web\App\certs\scada-web.pfx and restart JoularisWeb.
echo.
echo ========================================
echo   Service Status  (RUNNING = OK)
echo ========================================
echo [Engine ] JoularisEngine
sc query JoularisEngine  | find "STATE"
echo [Web    ] JoularisWeb
sc query JoularisWeb     | find "STATE"
echo [License] JoularisLicense
sc query JoularisLicense | find "STATE"
echo.
echo If a line is blank or shows STOPPED, that service did not start -
echo check its log and re-run this installer. (License also needs the HASP dongle.)
echo.

:: -- Explicit verdict + durable log (so success/fail does NOT depend on catching this window) --
:: Engine + Web are the hard success condition. License legitimately stays STOPPED without the
:: HASP dongle, so it is reported as a NOTE, not a failure. Result is also appended (with a
:: timestamp) to C:\SCADA\install-log.txt - open that file any time to see how the last run went.
set "_LOG=C:\SCADA\install-log.txt"
set "_FAIL="
sc query JoularisEngine  | find "RUNNING" >nul || set "_FAIL=1"
sc query JoularisWeb     | find "RUNNING" >nul || set "_FAIL=1"
set "_LICOK=1"
sc query JoularisLicense | find "RUNNING" >nul || set "_LICOK="
echo ========================================
if defined _FAIL (
    echo   [RESULT] FAILED - Engine and/or Web is NOT running. See the states above.
) else (
    echo   [RESULT] SUCCESS - Engine + Web are RUNNING.
    if not defined _LICOK echo   [NOTE] License not running yet - plug the HASP dongle then: net start JoularisLicense
)
echo   This result was also saved to: %_LOG%
echo ========================================
>>"%_LOG%" echo [%DATE% %TIME%] ---- Install finished (JOULARIS v$AppVersion) ----
sc query JoularisEngine  | find "STATE" >>"%_LOG%"
sc query JoularisWeb     | find "STATE" >>"%_LOG%"
sc query JoularisLicense | find "STATE" >>"%_LOG%"
if defined _FAIL (>>"%_LOG%" echo [%DATE% %TIME%] RESULT: FAILED ^(Engine/Web not running^)) else (>>"%_LOG%" echo [%DATE% %TIME%] RESULT: SUCCESS)
echo.
echo ----------------------------------------
echo This window stays open so you can read the result above.
echo Press any key to close it.
echo ----------------------------------------
pause
"@ | Set-Content -Path "$ReleasePath\Install.bat" -Encoding ASCII

Write-Host "  Install.bat created" -ForegroundColor Green

# ModbusServer standalone installer — deliberately NOT part of Install.bat（安裝解耦，見 docs/plans）
# 單引號 herestring：內容含 PowerShell 變數（$c/$p/$j），不可被 build 腳本插值；bat 內容須全 ASCII（Set-Content -Encoding ASCII）
# 版號 / 日期以 __APPVERSION__ / __BUILDDATE__ 占位，寫檔前 -replace 代入
@'
@echo off
setlocal
echo ========================================
echo   JOULARIS Modbus Gateway Installer  v__APPVERSION__
echo   (standalone - NOT installed by Install.bat)
echo ========================================
echo.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo [ERROR] Please run as Administrator
    pause
    exit /b 1
)

set "_APP=C:\SCADA\ModbusServer\App"
set "_BACKUP=C:\SCADA\_ModbusGatewayBackup"
set "_SVC=JoularisModbusGateway"
set "_OLDSVC=ScadaModbusGatewayService"
set "_IS_UPGRADE=0"

:: -- Detect upgrade: backup site config + AddressMap.json (address assignments must survive) --
if exist "%_APP%\Setting" (
    set "_IS_UPGRADE=1"
    echo [INFO] Detected existing installation - preserving site config + AddressMap.json...
    if not exist "%_BACKUP%" mkdir "%_BACKUP%"
    xcopy /E /I /Y "%_APP%\Setting"     "%_BACKUP%\Setting"     >nul
    xcopy /E /I /Y "%_APP%\MqttSetting" "%_BACKUP%\MqttSetting" >nul
    if exist "%_APP%\AddressMap.json" copy /Y "%_APP%\AddressMap.json" "%_BACKUP%\AddressMap.json" >nul
)

echo [1/6] Stopping existing service (if any)...
net stop %_SVC% >nul 2>&1
:: -- Migrate legacy service name (pre-v26.1.0): Windows cannot rename, so stop + delete the old one --
sc query %_OLDSVC% >nul 2>&1
if not errorlevel 1 (
    echo [MIGRATE] Removing legacy service %_OLDSVC% ^(replaced by %_SVC%^)...
    net stop %_OLDSVC% >nul 2>&1
    sc delete %_OLDSVC% >nul 2>&1
)

echo [2/6] Copying files...
xcopy /E /I /Y "%~dp0ModbusServer\App" "%_APP%" >nul

:: -- Restore site config --
if "%_IS_UPGRADE%"=="1" (
    echo [INFO] Restoring site config...
    xcopy /E /I /Y "%_BACKUP%\Setting"     "%_APP%\Setting"     >nul
    xcopy /E /I /Y "%_BACKUP%\MqttSetting" "%_APP%\MqttSetting" >nul
    if exist "%_BACKUP%\AddressMap.json" copy /Y "%_BACKUP%\AddressMap.json" "%_APP%\AddressMap.json" >nul
    rmdir /S /Q "%_BACKUP%"
)

:: -- dbSetting.json: same box as Engine/Web -> read C:\SCADA\Shared\dbSetting.json (DbSettingLocator layer 2
::    always beats a local copy, so delete the local one to avoid a file nobody reads);
::    standalone box (no Shared) -> keep the site copy, or seed Setting\dbSetting.json from the example --
if exist "C:\SCADA\Shared\dbSetting.json" (
    if exist "%_APP%\Setting\dbSetting.json" del /Q "%_APP%\Setting\dbSetting.json"
    echo [INFO] Using shared DB connection: C:\SCADA\Shared\dbSetting.json
) else (
    if not exist "%_APP%\Setting\dbSetting.json" (
        copy /Y "%_APP%\Setting\dbSetting.example.json" "%_APP%\Setting\dbSetting.json" >nul
        echo [NOTE] Standalone gateway: edit %_APP%\Setting\dbSetting.json ^(SQL Server IP / account^)
    )
)

:: -- Read configured ports from the (site) setting file --
for /f "usebackq" %%p in (`powershell -NoProfile -Command "(Get-Content '%_APP%\Setting\ModbusServerSetting.json' -Raw | ConvertFrom-Json).ModbusListenPort"`) do set MODBUS_PORT=%%p
for /f "usebackq" %%p in (`powershell -NoProfile -Command "(Get-Content '%_APP%\Setting\ModbusServerSetting.json' -Raw | ConvertFrom-Json).WebPort"`) do set WEB_PORT=%%p
if "%MODBUS_PORT%"=="" set MODBUS_PORT=502
if "%WEB_PORT%"=="" set WEB_PORT=5041

echo [3/6] Checking port availability (Modbus %MODBUS_PORT% / Web %WEB_PORT%)...

:CHECK_MODBUS
call :PORTCHECK %MODBUS_PORT%
if "%PORT_FREE%"=="1" goto MODBUS_OK
echo.
echo [WARN] Modbus TCP port %MODBUS_PORT% is already in use by: %PORT_OWNER%
set "NEWPORT="
set /p NEWPORT="Enter alternative Modbus port (suggest 1502, Enter = re-test %MODBUS_PORT%): "
if not "%NEWPORT%"=="" set MODBUS_PORT=%NEWPORT%
goto CHECK_MODBUS
:MODBUS_OK
echo   Modbus port %MODBUS_PORT% OK

:CHECK_WEB
call :PORTCHECK %WEB_PORT%
if "%PORT_FREE%"=="1" goto WEB_OK
echo.
echo [WARN] Web port %WEB_PORT% is already in use by: %PORT_OWNER%
set "NEWPORT="
set /p NEWPORT="Enter alternative Web port (suggest 5042, Enter = re-test %WEB_PORT%): "
if not "%NEWPORT%"=="" set WEB_PORT=%NEWPORT%
goto CHECK_WEB
:WEB_OK
echo   Web port %WEB_PORT% OK

:: -- Persist selected ports into ModbusServerSetting.json --
powershell -NoProfile -Command "$f='%_APP%\Setting\ModbusServerSetting.json'; $j=Get-Content $f -Raw | ConvertFrom-Json; $j.ModbusListenPort=[int]%MODBUS_PORT%; $j.WebPort=[int]%WEB_PORT%; $j | ConvertTo-Json -Depth 10 | Set-Content $f -Encoding UTF8"

echo [4/6] Registering Windows service...
sc query %_SVC% >nul 2>&1
if %errorLevel% NEQ 0 (
    sc create %_SVC% binPath= "\"%_APP%\ScadaEngine.ModbusServer.exe\"" DisplayName= "JOULARIS Modbus Gateway" start= auto
    sc failure %_SVC% reset= 86400 actions= restart/5000/restart/10000/restart/30000
)
:: upgrade path skips the create block above; force start type back to auto so a
:: previously disabled service doesn't block the net start below
sc config %_SVC% start= auto >nul
:: description carries version + build date, so refresh it on every install (not only on create)
sc description %_SVC% "JOULARIS Modbus Gateway | Backend - Modbus TCP gateway (FC4 input registers, float32) | v__APPVERSION__ | __BUILDDATE__"

echo [5/6] Opening firewall for ports %MODBUS_PORT% (Modbus) and %WEB_PORT% (Web)...
:: rule names renamed to JOULARIS (pre-v26.1.0: "SCADA Modbus Gateway *"); delete both old and new before adding
netsh advfirewall firewall delete rule name="SCADA Modbus Gateway TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="SCADA Modbus Gateway Web" >nul 2>&1
netsh advfirewall firewall delete rule name="JOULARIS Modbus Gateway TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="JOULARIS Modbus Gateway Web" >nul 2>&1
netsh advfirewall firewall add rule name="JOULARIS Modbus Gateway TCP" dir=in action=allow protocol=TCP localport=%MODBUS_PORT%
netsh advfirewall firewall add rule name="JOULARIS Modbus Gateway Web" dir=in action=allow protocol=TCP localport=%WEB_PORT%

echo [6/6] Starting service...
net start %_SVC%
echo.
echo ========================================
echo   JOULARIS Modbus Gateway installed!  v__APPVERSION__
echo   Modbus TCP : port %MODBUS_PORT% (FC4, float32)
echo   Address map: http://localhost:%WEB_PORT%
echo ========================================
echo.
if "%_IS_UPGRADE%"=="1" (
    echo [OK] Site config + AddressMap.json were PRESERVED.
) else (
    echo [NOTE] First install - edit config as needed:
    echo   %_APP%\Setting\ModbusServerSetting.json  ^(ports / word order / segments^)
    if exist "C:\SCADA\Shared\dbSetting.json" (
        echo   C:\SCADA\Shared\dbSetting.json           ^(SQL connection - shared with Engine/Web^)
    ) else (
        echo   %_APP%\Setting\dbSetting.json            ^(SQL connection - standalone box^)
    )
    echo   %_APP%\MqttSetting\MqttSetting.json      ^(MQTT broker^)
)
echo.
pause
exit /b 0

:: -- Subroutine: check if TCP port %1 is free --
:: Occupation by our own service (ScadaEngine.ModbusServer) is treated as free
:: (upgrade scenario - the service was just stopped / will restart with the same port)
:PORTCHECK
set PORT_FREE=1
set "PORT_OWNER="
for /f "usebackq tokens=*" %%o in (`powershell -NoProfile -Command "$c=Get-NetTCPConnection -State Listen -LocalPort %1 -ErrorAction SilentlyContinue | Select-Object -First 1; if($c){$p=Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue; if($p -and $p.ProcessName -ne 'ScadaEngine.ModbusServer'){Write-Output ($p.ProcessName + ' (PID ' + $c.OwningProcess + ')')} elseif(-not $p){Write-Output ('PID ' + $c.OwningProcess)}}"`) do (
    set PORT_FREE=0
    set "PORT_OWNER=%%o"
)
exit /b
'@ -replace '__APPVERSION__', $AppVersion -replace '__BUILDDATE__', $BuildDate | Set-Content -Path "$ReleasePath\InstallModbusServer.bat" -Encoding ASCII

Write-Host "  InstallModbusServer.bat created (standalone gateway installer)" -ForegroundColor Green

# ──────────────────────────────────────
# 6. Summary
# ──────────────────────────────────────
Write-Host "[6/6] Calculating size..." -ForegroundColor Yellow
$totalSize = [math]::Round(((Get-ChildItem $ReleasePath -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Version: $AppVersion" -ForegroundColor Cyan
Write-Host "Output:  $ReleasePath" -ForegroundColor Cyan
Write-Host "Size:    ${totalSize} MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "Folder structure:"
Write-Host "  SCADA_Release_${DateTag}_v$AppVersion\"
Write-Host "  +-- Install.bat              <- On-site: right-click Run as Admin (Engine + Web only)"
Write-Host "  +-- InstallModbusServer.bat  <- Optional Modbus gateway (standalone, run separately)"
Write-Host "  +-- Shared\                  <- dbSetting.json template -> C:\SCADA\Shared (ONE DB config for Engine/Web/Gateway)"
if (-not $NoPython) {
Write-Host "  +-- PythonRuntime\           <- Python base bundle (version-gated; Install.bat deploys to Engine\App)"
}
Write-Host "  +-- Engine\"
Write-Host "  |   +-- App\                 <- Engine executable + configs"
Write-Host "  |   +-- QuickDeploy.bat      <- Engine service manager"
Write-Host "  |   +-- DeployService.ps1"
Write-Host "  +-- Web\"
Write-Host "  |   +-- App\                 <- Web executable + wwwroot + configs"
Write-Host "  |   +-- QuickDeploy.bat      <- Web service manager"
Write-Host "  |   +-- DeployWebService.ps1"
Write-Host "  +-- LicenseBridge\"
Write-Host "  |   +-- App\                 <- net48 HASP bridge (installed by Install.bat; needs USB dongle)"
Write-Host "  +-- ModbusServer\"
Write-Host "      +-- App\                 <- Modbus TCP gateway (FC4 float32) + browse page"
Write-Host "      +-- QuickDeploy.bat      <- Gateway service manager"
Write-Host "      +-- DeployModbusServer.ps1"
Write-Host ""
Write-Host "On-site deployment:"
Write-Host "  1. Copy entire folder to USB"
Write-Host "  2. On target server: right-click Install.bat -> Run as Administrator"
Write-Host "  3. Done! Open http://server-ip:5038"
Write-Host ""
Write-Host "PREREQUISITE (framework-dependent build):" -ForegroundColor Yellow
Write-Host "  Target needs 'ASP.NET Core Runtime 8.0.x (x64)' installed (covers all 3 apps)." -ForegroundColor Yellow
Write-Host "  Install.bat detects it and STOPS with instructions if missing." -ForegroundColor Yellow
if ($NoPython) {
Write-Host "  This is an -NoPython app-only package: target must already have a matching Python bundle." -ForegroundColor Yellow
}
Write-Host ""
