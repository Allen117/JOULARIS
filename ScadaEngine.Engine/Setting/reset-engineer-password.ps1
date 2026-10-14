<#
.SYNOPSIS
    重設（或建立）工程師模式 bootstrap 帳號 engineer 的密碼。

.DESCRIPTION
    以 dbSetting.json 的應用程式帳號（db_owner）連線 SQL Server，console 互動輸入新密碼
    （遮罩、兩次核對、至少 8 碼）後直接覆蓋 Users 表 engineer 帳號的 PasswordHash，
    並確保 Role=Engineer、IsActive=1 — 舊密碼立即失效。

    engineer 帳號不存在時自動建立（既有站點補建第一顆 Engineer 帳號也走本腳本）。
    Users 資料表不存在時報錯 — 請先啟動一次 ScadaEngine.Engine 讓資料表自動建立。

.NOTES
    本腳本為工程師隨身工具，放在 Release 包根目錄、**不會**被安裝到客戶伺服器
    （留在伺服器等於任何能碰檔案系統的人都能一鍵重設）。需要時由工程師以 USB 帶到現場執行。

    參數不給時自動尋找 dbSetting.json：環境變數 JOULARIS_DBSETTING → 伺服器共用檔 C:\JOULARIS\Shared →
    包根 Shared\ → 腳本同資料夾 → 包內 Engine\App\Setting → 舊佈局 C:\JOULARIS\Engine\App\Setting
    → 遷移前舊根 C:\SCADA 兩條（legacy fallback）。
    密碼雜湊與 Web 登入驗證一致：SHA256(UTF8) 小寫 hex。
#>
[CmdletBinding()]
param(
    [string]$ServerInstance,   # 預設讀 dbSetting.json 的 DatabaseAddress
    [string]$DatabaseName,     # 預設讀 dbSetting.json 的 DataBaseName
    [string]$AppLogin,         # 預設讀 dbSetting.json 的 DataBaseAccount
    [string]$AppPassword,      # 預設讀 dbSetting.json 的 DataBasePassword
    [string]$NewPassword       # engineer 新密碼（至少 8 碼）；不給則 console 互動輸入
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile([string]$path) {
    if (Test-Path $path) { return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    return $null
}

# ─── 1. 讀取設定檔補齊參數 ───────────────────────────────────────────────
# 依序尋找（與 DbSettingLocator 同精神，共用檔優先）：環境變數 → 伺服器共用檔 C:\JOULARIS\Shared →
# 包根目錄執行時的包內 Shared\ → 腳本同資料夾（開發 repo）→ 包內 Engine\App\Setting（舊包）→ 舊佈局伺服器路徑
# → 遷移前舊根（legacy）
$dbSetting = $null
foreach ($cand in @(
    $env:JOULARIS_DBSETTING,
    'C:\JOULARIS\Shared\dbSetting.json',
    (Join-Path $PSScriptRoot 'Shared\dbSetting.json'),
    (Join-Path $PSScriptRoot 'dbSetting.json'),
    (Join-Path $PSScriptRoot 'Engine\App\Setting\dbSetting.json'),
    'C:\JOULARIS\Engine\App\Setting\dbSetting.json',
    # legacy fallback：安裝根目錄遷移（C:\SCADA → C:\JOULARIS，26.6.0）前的舊機，刻意排最後
    'C:\SCADA\Shared\dbSetting.json',
    'C:\SCADA\Engine\App\Setting\dbSetting.json'
)) {
    if (-not $cand) { continue }
    $dbSetting = Read-JsonFile $cand
    if ($dbSetting) { Write-Host "連線設定: $cand" -ForegroundColor Gray; break }
}

if (-not $ServerInstance) { $ServerInstance = if ($dbSetting) { $dbSetting.DatabaseAddress } else { 'localhost' } }
if (-not $DatabaseName)   { $DatabaseName   = if ($dbSetting) { $dbSetting.DataBaseName }   else { 'wsnCsharp' } }
if (-not $AppLogin)       { $AppLogin       = if ($dbSetting) { $dbSetting.DataBaseAccount } else { 'wsn' } }
if (-not $AppPassword)    { $AppPassword    = if ($dbSetting) { $dbSetting.DataBasePassword } else { '' } }

if (-not $AppPassword) {
    Write-Host "錯誤: 無應用帳號密碼（dbSetting.json 無值且未帶 -AppPassword），無法連線" -ForegroundColor Red
    exit 1
}

Write-Host "=== 重設工程師模式 engineer 帳號密碼 ===" -ForegroundColor Cyan
Write-Host "SQL Server : $ServerInstance"
Write-Host "資料庫     : $DatabaseName"
Write-Host ""

# ─── 2. 取得新密碼 ──────────────────────────────────────────────────────
$szPlainPwd = $null
if ($NewPassword) {
    if ($NewPassword.Length -lt 8) {
        Write-Host "錯誤: -NewPassword 少於 8 碼" -ForegroundColor Red
        exit 1
    }
    $szPlainPwd = $NewPassword
} else {
    for ($nTry = 1; $nTry -le 3 -and -not $szPlainPwd; $nTry++) {
        $sec1 = Read-Host "請輸入 engineer 新密碼（至少 8 碼）" -AsSecureString
        $p1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec1))
        # 長度不足立即提醒重輸，不浪費一次「確認密碼」輸入
        if ($p1.Length -lt 8)   { Write-Host "密碼少於 8 碼，請重新輸入" -ForegroundColor Yellow; continue }
        $sec2 = Read-Host "再輸入一次確認" -AsSecureString
        $p2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec2))
        if ($p1 -cne $p2)       { Write-Host "兩次輸入不一致，請重新輸入" -ForegroundColor Yellow; continue }
        $szPlainPwd = $p1
    }
    if (-not $szPlainPwd) {
        Write-Host "錯誤: 密碼設定未完成" -ForegroundColor Red
        exit 1
    }
}

# 與 Web 登入驗證一致：SHA256(UTF8) 小寫 hex（ValidateUserAsync）
$sha = [System.Security.Cryptography.SHA256]::Create()
$szHash = ([System.BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($szPlainPwd)))).Replace('-', '').ToLower()
$sha.Dispose()

# ─── 3. 連線並覆蓋 / 建立 ───────────────────────────────────────────────
$conn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=$ServerInstance;Database=$DatabaseName;User ID=$AppLogin;Password=$AppPassword;TrustServerCertificate=true")
$conn.Open()

try {
    $cmdChk = $conn.CreateCommand()
    $cmdChk.CommandText = "SELECT OBJECT_ID(N'[dbo].[Users]')"
    $usersTableId = $cmdChk.ExecuteScalar()
    if ($usersTableId -is [System.DBNull]) {
        Write-Host "錯誤: Users 資料表不存在 — 請先啟動一次 ScadaEngine.Engine 讓資料表自動建立後再執行本腳本" -ForegroundColor Red
        exit 1
    }

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
IF EXISTS (SELECT 1 FROM [dbo].[Users] WHERE [Username] = N'engineer')
BEGIN
    UPDATE [dbo].[Users]
    SET [PasswordHash] = @hash, [Role] = N'Engineer', [IsActive] = 1, [UpdatedAt] = GETDATE()
    WHERE [Username] = N'engineer';
    SELECT N'updated';
END
ELSE
BEGIN
    INSERT INTO [dbo].[Users] (Username, RealName, PasswordHash, Role, Department, IsActive, CreatedAt, UpdatedAt)
    VALUES (N'engineer', N'工程師', @hash, N'Engineer', N'', 1, GETDATE(), GETDATE());
    SELECT N'created';
END
"@
    [void]$cmd.Parameters.AddWithValue('@hash', $szHash)
    $szResult = $cmd.ExecuteScalar()

    if ($szResult -eq 'created') {
        Write-Host "engineer 帳號原不存在，已建立並設定密碼" -ForegroundColor Green
    } else {
        Write-Host "已覆蓋 engineer 帳號密碼（舊密碼立即失效）並確保帳號啟用" -ForegroundColor Green
    }
}
finally {
    $conn.Close()
}
