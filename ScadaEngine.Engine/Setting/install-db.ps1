<#
.SYNOPSIS
    ScadaEngine 資料庫一次性安裝腳本（idempotent，可重複執行）。

.DESCRIPTION
    以「執行者的 Windows 帳號」透過 Integrated Security 連線 SQL Server（安裝 SQL Server 時
    安裝者帳號預設即為 sysadmin），全程不需 sa 密碼。執行內容：

      1. 建立資料夾 C:\Scada\Database、C:\Scada\Backup（路徑讀 DbMaintenanceSetting.json）
         並授予 SQL Server 服務帳號寫入權限（CREATE DATABASE / BACKUP 的檔案 I/O 由 SQL 服務進程執行）
      2. 資料庫不存在 → 建立於指定路徑 + 設 RECOVERY SIMPLE
         資料庫已存在 → 跳過建立；若復原模式為 FULL 印出警告（不自動更改既有 DB 狀態）
      3. 建立應用程式 SQL login（讀 dbSetting.json 的帳密）並授予目標 DB 的 db_owner
      4. 建立工程師模式 bootstrap 帳號 engineer（無任何 Engineer 角色帳號時）：
         密碼由 -EngineerPassword 參數或 console 互動輸入（遮罩、兩次核對、至少 8 碼），
         非互動且未帶參數時跳過並警告。忘記密碼用 Release 包根目錄的 reset-engineer-password.ps1
         重設（工程師隨身工具，不落地伺服器）。

    既有環境（DB 已手動建立）也應執行一次本腳本，以補齊備份資料夾與服務帳號權限。

.NOTES
    需以「系統管理員身分」執行，且執行者的 Windows 帳號需為 SQL Server sysadmin。
    參數不給時自動讀取同資料夾的 dbSetting.json / DbMaintenanceSetting.json。
#>
[CmdletBinding()]
param(
    [string]$ServerInstance,   # 例: "localhost" 或 "localhost\SQLEXPRESS"，預設讀 dbSetting.json 的 DatabaseAddress
    [string]$DatabaseName,     # 預設讀 dbSetting.json 的 DataBaseName
    [string]$AppLogin,         # 預設讀 dbSetting.json 的 DataBaseAccount
    [string]$AppPassword,      # 預設讀 dbSetting.json 的 DataBasePassword
    [string]$DataFolder,       # 預設讀 DbMaintenanceSetting.json 的 DataFileFolder
    [string]$BackupFolder,     # 預設讀 DbMaintenanceSetting.json 的 BackupFolder
    [string]$EngineerPassword  # 工程師模式 bootstrap 帳號 engineer 的密碼（至少 8 碼）；不給則 console 互動輸入
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile([string]$path) {
    if (Test-Path $path) { return (Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    return $null
}

# ─── 1. 讀取設定檔補齊參數 ───────────────────────────────────────────────
$dbSetting    = Read-JsonFile (Join-Path $PSScriptRoot 'dbSetting.json')
$maintSetting = Read-JsonFile (Join-Path $PSScriptRoot 'DbMaintenanceSetting.json')

if (-not $ServerInstance) { $ServerInstance = if ($dbSetting) { $dbSetting.DatabaseAddress } else { 'localhost' } }
if (-not $DatabaseName)   { $DatabaseName   = if ($dbSetting) { $dbSetting.DataBaseName }   else { 'wsnCsharp' } }
if (-not $AppLogin)       { $AppLogin       = if ($dbSetting) { $dbSetting.DataBaseAccount } else { 'wsn' } }
if (-not $AppPassword)    { $AppPassword    = if ($dbSetting) { $dbSetting.DataBasePassword } else { '' } }
if (-not $DataFolder)     { $DataFolder     = if ($maintSetting) { $maintSetting.DataFileFolder } else { 'C:\Scada\Database' } }
if (-not $BackupFolder)   { $BackupFolder   = if ($maintSetting) { $maintSetting.BackupFolder }   else { 'C:\Scada\Backup' } }

Write-Host "=== ScadaEngine 資料庫安裝 ===" -ForegroundColor Cyan
Write-Host "SQL Server : $ServerInstance"
Write-Host "資料庫     : $DatabaseName"
Write-Host "應用帳號   : $AppLogin"
Write-Host "資料檔路徑 : $DataFolder"
Write-Host "備份路徑   : $BackupFolder"
Write-Host ""

# ─── 2. 偵測 SQL Server 服務帳號並授權資料夾 ────────────────────────────
# 預設實例服務名 MSSQLSERVER；具名實例（如 SQLEXPRESS）為 MSSQL$SQLEXPRESS
$instanceName = $null
if ($ServerInstance -match '\\(.+)$') { $instanceName = $Matches[1] }
$svcName = if ($instanceName) { "MSSQL`$$instanceName" } else { 'MSSQLSERVER' }

$svc = Get-CimInstance Win32_Service -Filter "Name='$($svcName.Replace("'","''"))'" -ErrorAction SilentlyContinue
if (-not $svc) {
    # 指定服務名找不到時，掃描本機所有 SQL 實例服務
    $candidates = @(Get-CimInstance Win32_Service | Where-Object { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' })
    if ($candidates.Count -eq 1) {
        $svc = $candidates[0]
        Write-Host "找不到服務 $svcName，自動改用本機唯一 SQL 實例服務: $($svc.Name)" -ForegroundColor Yellow
    }
}

if ($svc) {
    $svcAccount = $svc.StartName   # 例: NT Service\MSSQLSERVER
    Write-Host "SQL 服務帳號: $svcAccount ($($svc.Name))"

    foreach ($folder in @($DataFolder, $BackupFolder)) {
        if (-not (Test-Path $folder)) {
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            Write-Host "已建立資料夾: $folder" -ForegroundColor Green
        } else {
            Write-Host "資料夾已存在: $folder"
        }
        # (OI)(CI)M = 資料夾 + 子檔案繼承的「修改」權限
        icacls $folder /grant "${svcAccount}:(OI)(CI)M" | Out-Null
        Write-Host "已授予 $svcAccount 寫入權限: $folder" -ForegroundColor Green
    }
} else {
    Write-Host "警告: 本機找不到 SQL Server 服務（$svcName）— 若 SQL Server 在遠端主機，資料夾與權限請於該主機自行建立" -ForegroundColor Yellow
}

# ─── 2.5 確保本機 SQL Server 已開啟 TCP/IP（127.0.0.1 走 TCP 才連得上）──────
# runtime（Engine/Web）以 SQL 驗證連 dbSetting.json 的 127.0.0.1，IP 位址會強制走 TCP；
# 但 SQL Server（尤其 Express）預設 TCP/IP 協定為「關」→ App 連不上。此段開 TCP + 固定 1433
# + 清動態埠，並在有變更時重啟服務套用。idempotent（值已正確則不動、不重啟）。
# 127.0.0.1 為 loopback、不受 Windows 防火牆管制，故不需加防火牆規則。
# 僅在偵測到本機實例服務（$svc）時執行；遠端 SQL 請於該主機自行開啟 TCP/IP。
if ($svc) {
    try {
        # 由服務名反推登錄檔實例名：MSSQLSERVER=預設實例、MSSQL$XYZ→XYZ（與 SSPI 段一致）
        $regInstanceName = if ($svc.Name -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { $svc.Name.Substring(6) }
        $instMapPath = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
        $instId = $null
        if (Test-Path $instMapPath) {
            $instId = (Get-ItemProperty $instMapPath -ErrorAction SilentlyContinue).$regInstanceName
        }
        if (-not $instId) {
            Write-Host "警告: 找不到 SQL 實例 $regInstanceName 的登錄檔節點，略過 TCP/IP 自動啟用（請手動於 SQL Server 組態管理員開啟並重啟服務）" -ForegroundColor Yellow
        } else {
            $tcpPath   = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instId\MSSQLServer\SuperSocketNetLib\Tcp"
            $ipAllPath = Join-Path $tcpPath 'IPAll'
            if (-not (Test-Path $ipAllPath)) {
                Write-Host "警告: 找不到 TCP 設定節點（$tcpPath），略過 TCP/IP 自動啟用" -ForegroundColor Yellow
            } else {
                $tcpChanged = $false

                $curEnabled = (Get-ItemProperty $tcpPath -Name Enabled -ErrorAction SilentlyContinue).Enabled
                if ($curEnabled -ne 1) {
                    Set-ItemProperty $tcpPath -Name Enabled -Value 1 -Type DWord
                    $tcpChanged = $true
                    Write-Host "已啟用 TCP/IP 協定" -ForegroundColor Green
                }

                $curPort = (Get-ItemProperty $ipAllPath -Name TcpPort -ErrorAction SilentlyContinue).TcpPort
                if ($curPort -ne '1433') {
                    Set-ItemProperty $ipAllPath -Name TcpPort -Value '1433' -Type String
                    $tcpChanged = $true
                    Write-Host "已固定 TCP 埠為 1433" -ForegroundColor Green
                }

                $curDyn = (Get-ItemProperty $ipAllPath -Name TcpDynamicPorts -ErrorAction SilentlyContinue).TcpDynamicPorts
                if ($curDyn -ne '') {
                    Set-ItemProperty $ipAllPath -Name TcpDynamicPorts -Value '' -Type String
                    $tcpChanged = $true
                    Write-Host "已清除動態埠設定" -ForegroundColor Green
                }

                if ($tcpChanged) {
                    Write-Host "重啟 SQL 服務以套用 TCP/IP 設定: $($svc.Name)" -ForegroundColor Yellow
                    Restart-Service $svc.Name -Force
                    Write-Host "SQL 服務已重啟，TCP/IP 生效" -ForegroundColor Green
                } else {
                    Write-Host "TCP/IP 已啟用且埠為 1433，無需變更"
                }
            }
        }
    } catch {
        Write-Host "警告: 自動啟用 TCP/IP 時發生錯誤（$($_.Exception.Message)）— 請手動於 SQL Server 組態管理員開啟 TCP/IP 並重啟服務" -ForegroundColor Yellow
    }
}

# ─── 3. SSPI 連線 master ────────────────────────────────────────────────
# 整合式驗證改走本機實例名（shared memory）：位址是 IP 時 SSPI 查不到 SPN 退 NTLM，
# 工作群組環境會報「登入來自未信任的網域」。服務名格式為安裝程式強制規格
# （預設實例=MSSQLSERVER、具名=MSSQL$實例名），反推實例名是確定性的。
# SQL 驗證（應用帳號）不經此協商，dbSetting.json 的 IP 位址不需要改。
$sspiTarget = $ServerInstance
$addressPart = ($ServerInstance -split '\\')[0]
$isLocalAddress = $addressPart -in @('127.0.0.1', 'localhost', '.', $env:COMPUTERNAME)
if ($svc -and $isLocalAddress) {
    if ($svc.Name -eq 'MSSQLSERVER') { $sspiTarget = '.' }
    else { $sspiTarget = '.\' + $svc.Name.Substring(6) }   # MSSQL$XYZ → .\XYZ
}
Write-Host "SSPI 連線目標: $sspiTarget"
$conn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=$sspiTarget;Database=master;Integrated Security=SSPI;TrustServerCertificate=true")
$conn.Open()

function Invoke-Sql([string]$sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
    [void]$cmd.ExecuteNonQuery()
}
function Get-SqlScalar([string]$sql) {
    $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
    return $cmd.ExecuteScalar()
}

try {
    $safeDb   = $DatabaseName.Replace(']', ']]')
    $quotedDb = $DatabaseName.Replace("'", "''")

    # ─── 4. 建立資料庫（不存在時）────────────────────────────────────────
    $dbId = Get-SqlScalar "SELECT DB_ID(N'$quotedDb')"
    if ($dbId -is [System.DBNull]) {
        $mdf = (Join-Path $DataFolder "$DatabaseName.mdf").Replace("'", "''")
        $ldf = (Join-Path $DataFolder ($DatabaseName + '_log.ldf')).Replace("'", "''")
        Invoke-Sql "CREATE DATABASE [$safeDb] ON (NAME = N'$safeDb', FILENAME = N'$mdf') LOG ON (NAME = N'${safeDb}_log', FILENAME = N'$ldf')"
        Invoke-Sql "ALTER DATABASE [$safeDb] SET RECOVERY SIMPLE"
        Write-Host "已建立資料庫 $DatabaseName（RECOVERY SIMPLE，路徑: $DataFolder）" -ForegroundColor Green
    } else {
        Write-Host "資料庫 $DatabaseName 已存在，跳過建立"
        $recovery = Get-SqlScalar "SELECT recovery_model_desc FROM sys.databases WHERE name = N'$quotedDb'"
        if ($recovery -eq 'FULL') {
            Write-Host "警告: 既有資料庫復原模式為 FULL — 本系統僅做每週完整備份、不做交易記錄備份，FULL 模式下交易記錄檔將持續成長。" -ForegroundColor Yellow
            Write-Host "      建議由 DBA 評估後手動切換: ALTER DATABASE [$safeDb] SET RECOVERY SIMPLE" -ForegroundColor Yellow
        } else {
            Write-Host "復原模式: $recovery"
        }
    }

    # ─── 5. 建立應用程式 login / user / db_owner ─────────────────────────
    if (-not $AppPassword) {
        Write-Host "警告: 未提供應用帳號密碼（dbSetting.json 無值），跳過 login 建立" -ForegroundColor Yellow
    } else {
        $safeLogin   = $AppLogin.Replace(']', ']]')
        $quotedLogin = $AppLogin.Replace("'", "''")
        $quotedPwd   = $AppPassword.Replace("'", "''")

        $loginExists = Get-SqlScalar "SELECT COUNT(*) FROM sys.server_principals WHERE name = N'$quotedLogin'"
        if ([int]$loginExists -eq 0) {
            Invoke-Sql "CREATE LOGIN [$safeLogin] WITH PASSWORD = N'$quotedPwd', CHECK_POLICY = OFF, DEFAULT_DATABASE = [$safeDb]"
            Write-Host "已建立 SQL login: $AppLogin" -ForegroundColor Green
        } else {
            Write-Host "SQL login $AppLogin 已存在，跳過建立"
        }

        # 以 SID 判斷 login 是否已對應到庫內使用者（名稱可能不同）：
        # 若 DB 由此 login 建立/擁有，它在庫內就是 dbo，CREATE USER 會報 Msg 15063
        # 「登入已經具有一個不同使用者名稱之下的帳戶」；dbo 權限已是最高，直接略過。
        $mappedUser = Get-SqlScalar @"
SELECT dp.name FROM [$safeDb].sys.database_principals dp
WHERE dp.sid = SUSER_SID(N'$quotedLogin')
"@
        if ($mappedUser -is [System.DBNull] -or [string]::IsNullOrEmpty($mappedUser)) {
            Invoke-Sql @"
USE [$safeDb];
CREATE USER [$safeLogin] FOR LOGIN [$safeLogin];
ALTER ROLE db_owner ADD MEMBER [$safeLogin];
"@
            Write-Host "已建立使用者 $AppLogin 並加入 $DatabaseName 的 db_owner" -ForegroundColor Green
        } elseif ($mappedUser -eq 'dbo') {
            Write-Host "login $AppLogin 已是 $DatabaseName 的 dbo（資料庫擁有者），略過使用者/角色設定"
        } else {
            $safeUser = ([string]$mappedUser).Replace(']', ']]')
            Invoke-Sql @"
USE [$safeDb];
IF IS_ROLEMEMBER('db_owner', N'$(([string]$mappedUser).Replace("'", "''"))') <> 1
    ALTER ROLE db_owner ADD MEMBER [$safeUser];
"@
            Write-Host "已確認 $AppLogin（庫內使用者 $mappedUser）為 $DatabaseName 的 db_owner" -ForegroundColor Green
        }
    }

    # ─── 6. 工程師模式 bootstrap 帳號 engineer ──────────────────────────
    # Users 表通常由 Engine 首次啟動依 DatabaseSchema.json 建立；裝機時尚未存在則先以
    # 相同欄位定義預建（Engine 欄位同步只補缺不重建，預建表相容）。
    # 只要 DB 內已有任何 Engineer 角色帳號（含已停用）即跳過 — 支援「建好個人帳號後停用/刪除
    # bootstrap」的 SOP，重跑 install 不會重新長出 bootstrap 帳號。
    $usersTableId = Get-SqlScalar "SELECT OBJECT_ID(N'[$quotedDb].[dbo].[Users]')"
    if ($usersTableId -is [System.DBNull]) {
        Invoke-Sql @"
USE [$safeDb];
CREATE TABLE [Users] (
    [UserID] int IDENTITY(1,1) NOT NULL,
    [Username] nvarchar(50) NOT NULL,
    [RealName] nvarchar(100) NULL,
    [PasswordHash] nvarchar(MAX) NOT NULL,
    [Role] nvarchar(20) NOT NULL,
    [Department] nvarchar(50) NULL,
    [IsActive] bit NULL DEFAULT 1,
    [LastLoginAt] datetime NULL,
    [CreatedAt] datetime NULL DEFAULT GETDATE(),
    [UpdatedAt] datetime NULL DEFAULT GETDATE()
    ,CONSTRAINT [PK_Users] PRIMARY KEY NONCLUSTERED ([UserID])
);
"@
        Write-Host "Users 資料表尚未存在，已依 DatabaseSchema.json 定義預建" -ForegroundColor Green
    }

    $engineerCount = Get-SqlScalar "SELECT COUNT(*) FROM [$safeDb].[dbo].[Users] WHERE [Role] = N'Engineer'"
    if ([int]$engineerCount -gt 0) {
        Write-Host "已存在 Engineer 角色帳號，跳過 engineer bootstrap 帳號建立"
    } else {
        $szPlainPwd = $null
        if ($EngineerPassword) {
            if ($EngineerPassword.Length -lt 8) {
                Write-Host "警告: -EngineerPassword 少於 8 碼，拒絕使用 — 跳過 engineer 帳號建立" -ForegroundColor Yellow
            } else {
                $szPlainPwd = $EngineerPassword
            }
        } else {
            $isInteractive = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
            if (-not $isInteractive) {
                Write-Host "警告: 非互動環境且未帶 -EngineerPassword — 跳過 engineer 帳號建立，之後可執行 reset-engineer-password.ps1 建立" -ForegroundColor Yellow
            } else {
                Write-Host ""
                Write-Host "── 設定工程師模式 bootstrap 帳號（帳號名固定: engineer）──" -ForegroundColor Cyan
                for ($nTry = 1; $nTry -le 3 -and -not $szPlainPwd; $nTry++) {
                    try {
                        $sec1 = Read-Host "請自訂 engineer 密碼（至少 8 碼）" -AsSecureString
                    } catch {
                        Write-Host "警告: 無法互動輸入 — 跳過 engineer 帳號建立，之後可執行 reset-engineer-password.ps1 建立" -ForegroundColor Yellow
                        break
                    }
                    $p1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec1))
                    # 長度不足立即提醒重輸，不浪費一次「確認密碼」輸入
                    if ($p1.Length -lt 8)   { Write-Host "密碼少於 8 碼，請重新輸入" -ForegroundColor Yellow; continue }
                    try {
                        $sec2 = Read-Host "再輸入一次確認" -AsSecureString
                    } catch {
                        Write-Host "警告: 無法互動輸入 — 跳過 engineer 帳號建立，之後可執行 reset-engineer-password.ps1 建立" -ForegroundColor Yellow
                        break
                    }
                    $p2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec2))
                    if ($p1 -cne $p2)       { Write-Host "兩次輸入不一致，請重新輸入" -ForegroundColor Yellow; continue }
                    $szPlainPwd = $p1
                }
                if (-not $szPlainPwd) {
                    Write-Host "警告: 密碼設定未完成 — 跳過 engineer 帳號建立，之後可執行 reset-engineer-password.ps1 建立" -ForegroundColor Yellow
                }
            }
        }

        if ($szPlainPwd) {
            # 與 Web 登入驗證一致：SHA256(UTF8) 小寫 hex（ValidateUserAsync）
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $szHash = ([System.BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($szPlainPwd)))).Replace('-', '').ToLower()
            $sha.Dispose()

            $cmdIns = $conn.CreateCommand()
            $cmdIns.CommandText = "INSERT INTO [$safeDb].[dbo].[Users] (Username, RealName, PasswordHash, Role, Department, IsActive, CreatedAt, UpdatedAt) VALUES (N'engineer', N'工程師', @hash, N'Engineer', N'', 1, GETDATE(), GETDATE())"
            [void]$cmdIns.Parameters.AddWithValue('@hash', $szHash)
            [void]$cmdIns.ExecuteNonQuery()
            Write-Host "已建立工程師模式 bootstrap 帳號 engineer（建議首次登入後建立個人具名 Engineer 帳號並停用 bootstrap）" -ForegroundColor Green
        }
    }

    Write-Host ""
    Write-Host "=== 安裝完成 ===" -ForegroundColor Cyan
    Write-Host "後續: 啟動 ScadaEngine.Engine 會自動建表與補欄位；每週備份依 DbMaintenanceSetting.json 排程執行。"
}
finally {
    $conn.Close()
}
