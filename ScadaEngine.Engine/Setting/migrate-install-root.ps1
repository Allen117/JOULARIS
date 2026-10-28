<#
.SYNOPSIS
    安裝根目錄遷移（預設 C:\SCADA → C:\JOULARIS）。由 Install.bat / InstallModbusServer.bat 自動呼叫，也可獨立重跑。

.DESCRIPTION
    程式本身不寫死安裝根目錄（設定檔 / Bridge / Algorithms / Python 皆以執行檔位置相對推算），
    所以「搬家」只需處理「機器上指向舊根的綁定」。依序：

      1. 服務：列出 binPath 在舊根下的所有 Windows 服務（新舊服務名、Gateway 皆含），記錄原狀態後停止；
         殘留進程（Path 在舊根下）一併結束
      2. SQL：本機每個執行中的 SQL 實例，查 sys.master_files 找資料檔在舊根下的 DB → SET OFFLINE
      3. Move-Item 舊根 → 新根（同磁碟 = rename，瞬間完成；任何檔案被鎖即失敗並自動復原 SQL / 服務）
      4. SQL：MODIFY FILE 改新路徑 → SET ONLINE（失敗則把資料夾搬回、改回路徑、ONLINE）
      5. 服務 binPath 改指新根（Win32_Service.Change，不刪服務 → 保留帳號 / 復原動作設定）
      6. 新根下設定檔（*.json / *.config）內的舊根字串改新根（DbMaintenanceSetting.json 的備份路徑等）
      7. 排程工作（COM 列舉，避開 Get-ScheduledTask 遇損壞任務整批拋例外）的 Execute / Arguments / WorkingDirectory
      8. 重新啟動原本在跑、且不在 -SkipRestart 清單內的服務（Install.bat 自己會啟動它管的服務）
      9. 殘留掃描：服務 / 排程 / 防火牆程式規則 / 機器環境變數仍含舊根者列出警告（防火牆不自動改）

    可重跑：舊根已不存在、新根存在時跳過搬移，只補做 SQL / 服務 / 設定檔 / 排程的改指（部分失敗後重跑用）。
    -WhatIf：只盤點、印出會做什麼，不動機器（SQL 僅唯讀查詢）。

.NOTES
    需以「系統管理員身分」執行；SQL 以 Integrated Security 連線（執行者需為 SQL sysadmin，同 install-db.ps1）。
    結果同時附加到 <根目錄>\install-log.txt。結束碼：0 = 成功或無需遷移、1 = 失敗（訊息說明機器狀態）。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$OldRoot = 'C:\SCADA',
    [string]$NewRoot = 'C:\JOULARIS',
    # 逗號分隔的服務名：遷移後「不」重新啟動（呼叫端 Install.bat 稍後會自行停 / 裝 / 啟動這些服務）
    [string]$SkipRestart = ''
)

$ErrorActionPreference = 'Stop'
# 先顯式載入：-WhatIf 時模組自動載入會把每個 alias 設定都印成 "What if: Set Alias"，淹沒預演報告
$savedWhatIf = $WhatIfPreference; $WhatIfPreference = $false
Import-Module CimCmdlets
$WhatIfPreference = $savedWhatIf
$OldRoot = $OldRoot.TrimEnd('\')
$NewRoot = $NewRoot.TrimEnd('\')
$isDryRun = [bool]$WhatIfPreference
$script:logLines = New-Object System.Collections.Generic.List[string]

function Write-Log([string]$szMsg, [string]$szColor = 'Gray') {
    Write-Host $szMsg -ForegroundColor $szColor
    $script:logLines.Add("[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $szMsg")
}

function Save-Log {
    $szDir = if (Test-Path $NewRoot) { $NewRoot } elseif (Test-Path $OldRoot) { $OldRoot } else { $null }
    if (-not $szDir -or $isDryRun) { return }
    # UTF-8 帶 BOM：Add-Content 只在新建檔時寫 BOM，舊檔缺 BOM 先補，否則編輯器會誤判成 ANSI 亂碼
    try {
        $szLog = Join-Path $szDir 'install-log.txt'
        $bom = [byte[]](0xEF, 0xBB, 0xBF)
        if (Test-Path $szLog) {
            $data = [IO.File]::ReadAllBytes($szLog)
            if ($data.Length -lt 3 -or $data[0] -ne 0xEF -or $data[1] -ne 0xBB -or $data[2] -ne 0xBF) {
                [IO.File]::WriteAllBytes($szLog, [byte[]]($bom + $data))
            }
        }
        Add-Content -Path $szLog -Value $script:logLines -Encoding UTF8 -WhatIf:$false
    } catch { }
}

function Test-UnderRoot([string]$szText, [string]$szRoot) {
    if (-not $szText) { return $false }
    return $szText.IndexOf($szRoot + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
           $szText.TrimEnd('"', ' ').Equals($szRoot, [StringComparison]::OrdinalIgnoreCase)
}

# 不分大小寫把舊根替換成新根（只換「舊根\」或字尾的完整路徑段，避免 C:\SCADAEngine 之類誤換）
function Convert-RootPath([string]$szText, [string]$szFrom, [string]$szTo) {
    if (-not $szText) { return $szText }
    return [regex]::Replace($szText, [regex]::Escape($szFrom) + '(?=\\|/|"|''|\s|$)', $szTo.Replace('$', '$$'),
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

$skipNames = @($SkipRestart -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

Write-Log "=== 安裝根目錄遷移 $OldRoot -> $NewRoot $(if ($isDryRun) { '(WhatIf 預演：不動機器)' }) ===" 'Cyan'

# ─── 0. 判斷狀態 ─────────────────────────────────────────────────────────
$hasOld = Test-Path $OldRoot
$hasNew = Test-Path $NewRoot
if ($hasOld -and $hasNew) {
    Write-Log "[ERROR] $OldRoot 與 $NewRoot 同時存在，無法判斷哪個是現役安裝 — 請人工確認後移除其中一個再重跑。" 'Red'
    Save-Log; exit 1
}
if (-not $hasOld -and -not $hasNew) {
    Write-Log "[INFO] $OldRoot 與 $NewRoot 都不存在 — 全新機器，無需遷移。" 'Green'
    exit 0
}
$needMove = $hasOld
if (-not $needMove) { Write-Log "[INFO] $OldRoot 已不存在、$NewRoot 存在 — 跳過搬移，只補做綁定改指（重跑模式）。" 'Yellow' }

# ─── 1. 盤點並停止舊根下的服務 ───────────────────────────────────────────
$services = @(Get-CimInstance Win32_Service | Where-Object { (Test-UnderRoot $_.PathName $OldRoot) })
$wasRunning = @{}
foreach ($svc in $services) { $wasRunning[$svc.Name] = ($svc.State -eq 'Running') }
Write-Log "[1] 服務（binPath 在 $OldRoot 下）：$(if ($services.Count) { '' } else { '無' })"
foreach ($svc in $services) { Write-Log "    $($svc.Name)  [$($svc.State)]  $($svc.PathName)" }

if ($needMove) {
    foreach ($svc in $services | Where-Object { $_.State -ne 'Stopped' }) {
        if ($PSCmdlet.ShouldProcess($svc.Name, '停止服務')) {
            try { Stop-Service -Name $svc.Name -Force -ErrorAction Stop; Write-Log "    已停止 $($svc.Name)" }
            catch { Write-Log "    [WARN] 停止 $($svc.Name) 失敗：$($_.Exception.Message)" 'Yellow' }
        }
    }
    $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Test-UnderRoot $_.Path $OldRoot) })
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess("$($p.ProcessName) (PID $($p.Id)) $($p.Path)", '結束殘留進程')) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Log "    已結束殘留進程 $($p.ProcessName) (PID $($p.Id))" }
            catch { Write-Log "    [WARN] 結束 $($p.ProcessName) 失敗：$($_.Exception.Message)" 'Yellow' }
        }
    }
    if (-not $isDryRun) { Start-Sleep -Seconds 2 }   # 讓服務進程釋放檔案 handle
}

# ─── 2. SQL：資料檔在舊根下的 DB ─────────────────────────────────────────
$sqlTargets = @(Get-CimInstance Win32_Service | Where-Object { ($_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*') -and $_.State -eq 'Running' } |
    ForEach-Object { if ($_.Name -eq 'MSSQLSERVER') { '.' } else { '.\' + $_.Name.Substring(6) } })

$sqlConns = @{}      # 實例 → 已開啟的連線
$dbFiles  = @()      # @{ Instance; Db; Logical; Path; State }

function Invoke-SqlOn([string]$szInstance, [string]$szSql) {
    $cmd = $sqlConns[$szInstance].CreateCommand(); $cmd.CommandText = $szSql; $cmd.CommandTimeout = 300
    [void]$cmd.ExecuteNonQuery()
}

foreach ($szInst in $sqlTargets) {
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection("Server=$szInst;Database=master;Integrated Security=SSPI;TrustServerCertificate=true")
        $conn.Open()
        $sqlConns[$szInst] = $conn
        $cmd = $conn.CreateCommand()
        # 新舊根都查：重跑模式下資料檔可能已在新根（ONLINE 未完成）或仍登記舊根
        $cmd.CommandText = @"
SELECT d.name, mf.name, mf.physical_name, d.state_desc
FROM sys.master_files mf JOIN sys.databases d ON d.database_id = mf.database_id
WHERE LEFT(mf.physical_name, LEN(@old) + 1) = @old + N'\'
"@
        [void]$cmd.Parameters.AddWithValue('@old', $OldRoot)
        $rd = $cmd.ExecuteReader()
        while ($rd.Read()) {
            $dbFiles += [pscustomobject]@{ Instance = $szInst; Db = $rd.GetString(0); Logical = $rd.GetString(1); Path = $rd.GetString(2); State = $rd.GetString(3) }
        }
        $rd.Close()
    } catch {
        Write-Log "[ERROR] 無法以 Windows 驗證連線 SQL 實例 $szInst（需 sysadmin）：$($_.Exception.Message)" 'Red'
        Write-Log "        無法確認是否有資料庫檔案位於 $OldRoot，為避免把線上資料庫搬壞，中止遷移（機器未變動，服務已停止）。" 'Red'
        Write-Log "        請以 SQL sysadmin 的 Windows 帳號、系統管理員身分重跑 Install.bat。" 'Red'
        foreach ($svc in $services | Where-Object { $wasRunning[$_.Name] }) { if (-not $isDryRun) { Start-Service $svc.Name -ErrorAction SilentlyContinue } }
        Save-Log; exit 1
    }
}
$dbNames = @($dbFiles | Select-Object -ExpandProperty Db -Unique)
Write-Log "[2] SQL 資料檔在 $OldRoot 下的資料庫：$(if ($dbNames.Count) { '' } else { '無' })"
foreach ($f in $dbFiles) { Write-Log "    [$($f.Instance)] $($f.Db) ($($f.State))  $($f.Logical) = $($f.Path)" }

function Set-DbState([string]$szState) {
    foreach ($g in $dbFiles | Group-Object Instance, Db) {
        $f0 = $g.Group[0]
        $szSafe = $f0.Db.Replace(']', ']]')
        if ($PSCmdlet.ShouldProcess("[$($f0.Instance)] $($f0.Db)", "SET $szState")) {
            $szSql = if ($szState -eq 'OFFLINE') { "ALTER DATABASE [$szSafe] SET OFFLINE WITH ROLLBACK IMMEDIATE" } else { "ALTER DATABASE [$szSafe] SET ONLINE" }
            Invoke-SqlOn $f0.Instance $szSql
            Write-Log "    $($f0.Db) → $szState"
        }
    }
}
function Set-DbFilePaths([string]$szFrom, [string]$szTo) {
    foreach ($f in $dbFiles) {
        $szNewPath = Convert-RootPath $f.Path $szFrom $szTo
        if ($PSCmdlet.ShouldProcess("[$($f.Instance)] $($f.Db).$($f.Logical)", "MODIFY FILE → $szNewPath")) {
            Invoke-SqlOn $f.Instance ("ALTER DATABASE [$($f.Db.Replace(']', ']]'))] MODIFY FILE (NAME = N'$($f.Logical.Replace("'", "''"))', FILENAME = N'$($szNewPath.Replace("'", "''"))')")
            Write-Log "    $($f.Db).$($f.Logical) → $szNewPath"
        }
    }
}

if ($dbFiles.Count -and -not $needMove) {
    # 重跑模式：檔案已在新根、登記仍在舊根 → DB 多半是 RECOVERY_PENDING，先 OFFLINE 再改路徑（失敗不擋）
    try { Set-DbState 'OFFLINE' } catch { Write-Log "    [INFO] OFFLINE 略過：$($_.Exception.Message)" }
}
if ($dbFiles.Count -and $needMove) {
    try { Set-DbState 'OFFLINE' }
    catch {
        Write-Log "[ERROR] 資料庫 OFFLINE 失敗：$($_.Exception.Message) — 中止（尚未搬移），還原 ONLINE 與服務。" 'Red'
        try { Set-DbState 'ONLINE' } catch { }
        foreach ($svc in $services | Where-Object { $wasRunning[$_.Name] }) { Start-Service $svc.Name -ErrorAction SilentlyContinue }
        Save-Log; exit 1
    }
}

# ─── 3. 搬移資料夾 ───────────────────────────────────────────────────────
if ($needMove) {
    Write-Log "[3] 搬移 $OldRoot → $NewRoot"
    if ($PSCmdlet.ShouldProcess($OldRoot, "Move-Item → $NewRoot")) {
        try {
            Move-Item -LiteralPath $OldRoot -Destination $NewRoot -ErrorAction Stop
            Write-Log "    完成" 'Green'
        } catch {
            Write-Log "[ERROR] 搬移失敗：$($_.Exception.Message)" 'Red'
            Write-Log "        常見原因：檔案總管 / 編輯器 / 命令列視窗開在 $OldRoot 底下、log 檔被開著。關掉後重跑即可。" 'Red'
            $lockers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Test-UnderRoot $_.Path $OldRoot) })
            foreach ($p in $lockers) { Write-Log "        仍在執行：$($p.ProcessName) (PID $($p.Id)) $($p.Path)" 'Red' }
            Write-Log "        機器已還原：資料庫 ONLINE、服務恢復原狀態（仍在 $OldRoot）。" 'Yellow'
            if ($dbFiles.Count) { try { Set-DbState 'ONLINE' } catch { Write-Log "        [WARN] 資料庫 ONLINE 失敗：$($_.Exception.Message)" 'Yellow' } }
            foreach ($svc in $services | Where-Object { $wasRunning[$_.Name] }) { Start-Service $svc.Name -ErrorAction SilentlyContinue }
            Save-Log; exit 1
        }
    }
}

# ─── 4. SQL：改路徑 + ONLINE ─────────────────────────────────────────────
if ($dbFiles.Count) {
    Write-Log "[4] SQL 資料檔改指 $NewRoot 並 ONLINE"
    try {
        Set-DbFilePaths $OldRoot $NewRoot
        Set-DbState 'ONLINE'
    } catch {
        Write-Log "[ERROR] 資料庫改路徑 / ONLINE 失敗：$($_.Exception.Message)" 'Red'
        if ($needMove -and -not $isDryRun) {
            Write-Log "        回滾：搬回 $OldRoot、改回路徑、ONLINE..." 'Yellow'
            try {
                try { Set-DbState 'OFFLINE' } catch { }
                Move-Item -LiteralPath $NewRoot -Destination $OldRoot -ErrorAction Stop
                Set-DbFilePaths $NewRoot $OldRoot
                Set-DbState 'ONLINE'
                foreach ($svc in $services | Where-Object { $wasRunning[$_.Name] }) { Start-Service $svc.Name -ErrorAction SilentlyContinue }
                Write-Log "        回滾完成，機器維持在 $OldRoot。" 'Yellow'
            } catch {
                Write-Log "        [ERROR] 回滾失敗：$($_.Exception.Message)" 'Red'
                Write-Log "        請手動：Move-Item 回原位置、ALTER DATABASE ... MODIFY FILE 改回路徑、SET ONLINE（見 docs 部署說明書 §安裝根目錄遷移）。" 'Red'
            }
        }
        Save-Log; exit 1
    }
}

# 從這裡開始操作都在新根（WhatIf 時新根尚不存在，用舊根盤點）
$scanRoot = if ($isDryRun -and $needMove) { $OldRoot } else { $NewRoot }

# ─── 5. 服務 binPath 改指新根 ────────────────────────────────────────────
Write-Log "[5] 服務 binPath 改指 $NewRoot"
foreach ($svc in $services) {
    $szNewBin = Convert-RootPath $svc.PathName $OldRoot $NewRoot
    if ($PSCmdlet.ShouldProcess($svc.Name, "binPath → $szNewBin")) {
        $r = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ PathName = $szNewBin }
        if ($r.ReturnValue -eq 0) { Write-Log "    $($svc.Name) → $szNewBin" }
        else { Write-Log "    [WARN] $($svc.Name) binPath 更新失敗（Win32 回傳 $($r.ReturnValue)），請手動 sc config $($svc.Name) binPath= `"$szNewBin`"" 'Yellow' }
    }
}

# ─── 6. 新根下設定檔內的舊根字串 ─────────────────────────────────────────
# 只掃設定類小檔；排除 Python 執行環境 / log / 資料庫 / 備份 / 刪除備份夾（量大且不含設定）
Write-Log "[6] 設定檔內的 $OldRoot 字串"
$excludeDirs = @('PythonRuntime', 'Log', 'Logs', 'Database', 'Backup', '_deleted', '_StaleRuntimeBackup', 'wwwroot')
$jsonOld = $OldRoot.Replace('\', '\\')
$jsonNew = $NewRoot.Replace('\', '\\')
$slashOld = $OldRoot.Replace('\', '/')
$slashNew = $NewRoot.Replace('\', '/')
# 副檔名自行過濾：-LiteralPath 搭 -Include 不生效（會把 .pyc / .ps1 都列進來）
$configFiles = @(Get-ChildItem -LiteralPath $scanRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.json', '.config' } |
    Where-Object { $p = $_.FullName.Substring($scanRoot.Length); -not ($excludeDirs | Where-Object { $p -match "\\$([regex]::Escape($_))\\" }) })
$nChanged = 0
foreach ($file in $configFiles) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [Text.Encoding]::UTF8.GetString($bytes, $(if ($hasBom) { 3 } else { 0 }), $bytes.Length - $(if ($hasBom) { 3 } else { 0 }))
    $newText = Convert-RootPath (Convert-RootPath (Convert-RootPath $text $jsonOld $jsonNew) $slashOld $slashNew) $OldRoot $NewRoot
    if ($newText -ne $text) {
        $nChanged++
        if ($PSCmdlet.ShouldProcess($file.FullName, '舊根字串 → 新根')) {
            [IO.File]::WriteAllText($file.FullName, $newText, (New-Object Text.UTF8Encoding($hasBom)))
            Write-Log "    已更新 $($file.FullName)"
        }
    }
}
if (-not $nChanged) { Write-Log '    無' }

# ─── 7. 排程工作 ─────────────────────────────────────────────────────────
Write-Log "[7] 排程工作（動作含 $OldRoot）"
$taskHits = 0
try {
    $sched = New-Object -ComObject Schedule.Service
    $sched.Connect()
    $folders = New-Object System.Collections.Generic.Queue[object]
    $folders.Enqueue($sched.GetFolder('\'))
    while ($folders.Count) {
        $folder = $folders.Dequeue()
        try { foreach ($sub in $folder.GetFolders(0)) { $folders.Enqueue($sub) } } catch { }
        $tasks = @(); try { $tasks = @($folder.GetTasks(1)) } catch { Write-Log "    [WARN] 無法列舉 $($folder.Path)：$($_.Exception.Message)" 'Yellow' }
        foreach ($task in $tasks) {
            try {
                $def = $task.Definition
                $isHit = $false
                foreach ($a in $def.Actions) {
                    if ($a.Type -ne 0) { continue }   # 0 = TASK_ACTION_EXEC
                    if ((Test-UnderRoot $a.Path $OldRoot) -or (Test-UnderRoot $a.Arguments $OldRoot) -or (Test-UnderRoot $a.WorkingDirectory $OldRoot)) {
                        $isHit = $true
                        $a.Path = Convert-RootPath $a.Path $OldRoot $NewRoot
                        if ($a.Arguments) { $a.Arguments = Convert-RootPath $a.Arguments $OldRoot $NewRoot }
                        if ($a.WorkingDirectory) { $a.WorkingDirectory = Convert-RootPath $a.WorkingDirectory $OldRoot $NewRoot }
                    }
                }
                if (-not $isHit) { continue }
                $taskHits++
                $nLogon = $def.Principal.LogonType
                if ($nLogon -eq 1 -or $nLogon -eq 6) {
                    Write-Log "    [WARN] $($task.Path) 以帳號密碼執行，無法免密碼更新 — 請在工作排程器手動把動作路徑改為 $NewRoot" 'Yellow'
                    continue
                }
                if ($PSCmdlet.ShouldProcess($task.Path, "動作路徑 → $NewRoot")) {
                    # 4 = TASK_UPDATE；沿用原 principal（SYSTEM / 服務帳號 / 互動 token 皆不需密碼）
                    [void]$folder.RegisterTaskDefinition($task.Name, $def, 4, $def.Principal.UserId, $null, $nLogon)
                    Write-Log "    已更新 $($task.Path)"
                }
            } catch {
                Write-Log "    [WARN] 排程 $($task.Path) 處理失敗：$($_.Exception.Message)" 'Yellow'
            }
        }
    }
} catch {
    Write-Log "    [WARN] 無法連線工作排程器：$($_.Exception.Message)" 'Yellow'
}
if (-not $taskHits) { Write-Log '    無' }

# ─── 8. 重啟原本在跑的服務（排除呼叫端自管者）────────────────────────────
$toRestart = @($services | Where-Object { $wasRunning[$_.Name] -and ($skipNames -notcontains $_.Name) })
if ($toRestart.Count) {
    Write-Log "[8] 重新啟動原本執行中的服務"
    foreach ($svc in $toRestart) {
        if ($PSCmdlet.ShouldProcess($svc.Name, '啟動服務')) {
            try { Start-Service $svc.Name -ErrorAction Stop; Write-Log "    已啟動 $($svc.Name)" }
            catch { Write-Log "    [WARN] 啟動 $($svc.Name) 失敗：$($_.Exception.Message)" 'Yellow' }
        }
    }
}

# ─── 9. 殘留掃描 ─────────────────────────────────────────────────────────
Write-Log "[9] 殘留掃描（仍指向 $OldRoot 者）"
$nResidual = 0
if (-not $isDryRun) {
    foreach ($svc in Get-CimInstance Win32_Service | Where-Object { Test-UnderRoot $_.PathName $OldRoot }) {
        $nResidual++; Write-Log "    [WARN] 服務 $($svc.Name)：$($svc.PathName)" 'Yellow'
    }
}
try {
    foreach ($fw in Get-NetFirewallApplicationFilter -ErrorAction Stop | Where-Object { Test-UnderRoot $_.Program $OldRoot }) {
        $rule = $fw | Get-NetFirewallRule -ErrorAction SilentlyContinue
        $nResidual++; Write-Log "    [WARN] 防火牆規則「$($rule.DisplayName)」程式路徑 $($fw.Program)（未自動修改，請確認是否仍需要）" 'Yellow'
    }
} catch { }
foreach ($scope in 'Machine', 'User') {
    foreach ($kv in [Environment]::GetEnvironmentVariables($scope).GetEnumerator()) {
        if ($kv.Key -ne 'Path' -and (Test-UnderRoot "$($kv.Value)" $OldRoot)) {
            $nResidual++; Write-Log "    [WARN] 環境變數（$scope）$($kv.Key) = $($kv.Value)" 'Yellow'
        }
    }
}
if (-not $nResidual) { Write-Log '    無' }

foreach ($c in $sqlConns.Values) { try { $c.Close() } catch { } }
Write-Log "=== 遷移$(if ($isDryRun) { '預演' })完成：$NewRoot ===" 'Green'
Save-Log
exit 0
