# JOULARIS Engine 監控與維護腳本
# 提供服務健康檢查、日誌管理、效能監控等功能

param(
    [Parameter(Mandatory=$false)]
    [string]$szAction = "monitor",
    
    [Parameter(Mandatory=$false)]
    [int]$nIntervalSeconds = 30,
    
    [Parameter(Mandatory=$false)]
    [string]$szLogPath = "C:\JOULARIS\Engine\App\Log"
)

$szServiceName = "JoularisEngine"

function Write-Log {
    param([string]$szMessage, [string]$szLevel = "INFO")
    $dtTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $szColoredMessage = "[$dtTimestamp] [$szLevel] $szMessage"
    
    switch($szLevel) {
        "ERROR" { Write-Host $szColoredMessage -ForegroundColor Red }
        "WARN" { Write-Host $szColoredMessage -ForegroundColor Yellow }
        "SUCCESS" { Write-Host $szColoredMessage -ForegroundColor Green }
        "INFO" { Write-Host $szColoredMessage -ForegroundColor Cyan }
        default { Write-Host $szColoredMessage -ForegroundColor White }
    }
}

function Get-ServiceHealthStatus {
    try {
        $service = Get-Service -Name $szServiceName -ErrorAction SilentlyContinue
        if (-not $service) {
            return @{
                IsHealthy = $false
                Status = "NotInstalled"
                Message = "服務未安裝"
                Details = $null
            }
        }
        
        $isRunning = $service.Status -eq "Running"
        $process = if ($isRunning) { 
            Get-Process -Id $service.ServicesDependedOn[0].ServiceHandle -ErrorAction SilentlyContinue 
        } else { 
            $null 
        }
        
        return @{
            IsHealthy = $isRunning
            Status = $service.Status
            Message = if ($isRunning) { "服務運行正常" } else { "服務已停止" }
            Details = @{
                StartType = $service.StartType
                ProcessId = if ($process) { $process.Id } else { $null }
                WorkingSet = if ($process) { [math]::Round($process.WorkingSet64 / 1MB, 2) } else { $null }
                CpuTime = if ($process) { $process.TotalProcessorTime.ToString("hh\:mm\:ss") } else { $null }
            }
        }
    } catch {
        return @{
            IsHealthy = $false
            Status = "Error"
            Message = "檢查服務狀態時發生錯誤: $($_.Exception.Message)"
            Details = $null
        }
    }
}

function Show-ServiceMonitor {
    Write-Log "開始監控 JOULARIS Engine (每 $nIntervalSeconds 秒更新一次)"
    Write-Log "按 Ctrl+C 停止監控"
    Write-Log "==============================================="
    
    $nCounter = 0
    while ($true) {
        try {
            $nCounter++
            Clear-Host
            
            Write-Host "JOULARIS Engine 監控面板" -ForegroundColor Green
            Write-Host "更新次數: $nCounter | 最後更新: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
            Write-Host "===============================================" -ForegroundColor Gray
            
            # 服務狀態檢查
            $healthStatus = Get-ServiceHealthStatus
            
            Write-Host "服務狀態: " -NoNewline
            if ($healthStatus.IsHealthy) {
                Write-Host $healthStatus.Status -ForegroundColor Green
            } else {
                Write-Host $healthStatus.Status -ForegroundColor Red
            }
            
            Write-Host "狀態描述: $($healthStatus.Message)"
            
            if ($healthStatus.Details) {
                Write-Host "啟動類型: $($healthStatus.Details.StartType)"
                if ($healthStatus.Details.ProcessId) {
                    Write-Host "行程 ID: $($healthStatus.Details.ProcessId)"
                    Write-Host "記憶體使用: $($healthStatus.Details.WorkingSet) MB"
                    Write-Host "CPU 時間: $($healthStatus.Details.CpuTime)"
                }
            }
            
            Write-Host ""
            
            # 檢查日誌檔案
            if (Test-Path $szLogPath) {
                Write-Host "日誌檔案狀態:" -ForegroundColor Yellow
                $logFiles = Get-ChildItem $szLogPath -Filter "*.log" | Sort-Object LastWriteTime -Descending
                
                if ($logFiles.Count -gt 0) {
                    $latestLog = $logFiles[0]
                    Write-Host "  最新日誌: $($latestLog.Name)"
                    Write-Host "  檔案大小: $([math]::Round($latestLog.Length / 1KB, 2)) KB"
                    Write-Host "  最後修改: $($latestLog.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
                    
                    # 顯示最近的錯誤日誌（如果有）
                    $errorLines = Get-Content $latestLog.FullName -Tail 50 | Where-Object { $_ -match "ERROR|FATAL" } | Select-Object -Last 3
                    if ($errorLines) {
                        Write-Host ""
                        Write-Host "最近錯誤:" -ForegroundColor Red
                        $errorLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
                    }
                } else {
                    Write-Host "  未找到日誌檔案" -ForegroundColor Yellow
                }
            } else {
                Write-Host "日誌目錄不存在: $szLogPath" -ForegroundColor Yellow
            }
            
            Write-Host ""
            Write-Host "===============================================" -ForegroundColor Gray
            Write-Host "下次更新倒數計時: " -NoNewline -ForegroundColor Gray
            
            # 倒數計時
            for ($i = $nIntervalSeconds; $i -gt 0; $i--) {
                Write-Host "`r下次更新倒數計時: $i 秒  " -NoNewline -ForegroundColor Gray
                Start-Sleep -Seconds 1
            }
            
        } catch {
            Write-Log "監控過程中發生錯誤: $($_.Exception.Message)" "ERROR"
            Start-Sleep -Seconds 5
        }
    }
}

function Show-ServiceLogs {
    param([int]$nTailLines = 100)
    
    if (-not (Test-Path $szLogPath)) {
        Write-Log "日誌目錄不存在: $szLogPath" "ERROR"
        return
    }
    
    $logFiles = Get-ChildItem $szLogPath -Filter "*.log" | Sort-Object LastWriteTime -Descending
    
    if ($logFiles.Count -eq 0) {
        Write-Log "未找到日誌檔案" "WARN"
        return
    }
    
    Write-Log "顯示最新日誌檔案的最後 $nTailLines 行:"
    Write-Log "檔案: $($logFiles[0].FullName)"
    Write-Log "==============================================="
    
    Get-Content $logFiles[0].FullName -Tail $nTailLines | ForEach-Object {
        $szLine = $_
        if ($szLine -match "ERROR|FATAL") {
            Write-Host $szLine -ForegroundColor Red
        } elseif ($szLine -match "WARN") {
            Write-Host $szLine -ForegroundColor Yellow
        } elseif ($szLine -match "INFO") {
            Write-Host $szLine -ForegroundColor Cyan
        } else {
            Write-Host $szLine
        }
    }
}

function Invoke-LogCleanup {
    param(
        [int]$nRetainDays = 30,
        [bool]$isConfirm = $true
    )
    
    if (-not (Test-Path $szLogPath)) {
        Write-Log "日誌目錄不存在: $szLogPath" "ERROR"
        return
    }
    
    $dtCutoffDate = (Get-Date).AddDays(-$nRetainDays)
    $oldLogFiles = Get-ChildItem $szLogPath -Filter "*.log" | Where-Object { $_.LastWriteTime -lt $dtCutoffDate }
    
    if ($oldLogFiles.Count -eq 0) {
        Write-Log "沒有需要清理的舊日誌檔案（保留 $nRetainDays 天）" "INFO"
        return
    }
    
    Write-Log "找到 $($oldLogFiles.Count) 個超過 $nRetainDays 天的日誌檔案:"
    $oldLogFiles | ForEach-Object {
        Write-Log "  $($_.Name) - $($_.LastWriteTime.ToString('yyyy-MM-dd')) - $([math]::Round($_.Length / 1KB, 2)) KB"
    }
    
    if ($isConfirm) {
        $szResponse = Read-Host "是否要刪除這些檔案？(y/N)"
        if ($szResponse.ToLower() -ne "y") {
            Write-Log "取消清理操作" "INFO"
            return
        }
    }
    
    $nDeletedCount = 0
    $nDeletedSize = 0
    
    $oldLogFiles | ForEach-Object {
        try {
            $nDeletedSize += $_.Length
            Remove-Item $_.FullName -Force
            $nDeletedCount++
            Write-Log "已刪除: $($_.Name)" "SUCCESS"
        } catch {
            Write-Log "刪除失敗: $($_.Name) - $($_.Exception.Message)" "ERROR"
        }
    }
    
    Write-Log "日誌清理完成：刪除 $nDeletedCount 個檔案，釋放 $([math]::Round($nDeletedSize / 1MB, 2)) MB 空間" "SUCCESS"
}

function Show-ServicePerformance {
    $healthStatus = Get-ServiceHealthStatus
    
    if (-not $healthStatus.IsHealthy) {
        Write-Log "服務未運行，無法取得效能資訊" "WARN"
        return
    }
    
    if (-not $healthStatus.Details.ProcessId) {
        Write-Log "無法取得行程資訊" "WARN"
        return
    }
    
    try {
        $process = Get-Process -Id $healthStatus.Details.ProcessId
        
        Write-Log "JOULARIS Engine 效能資訊:"
        Write-Log "==============================================="
        Write-Log "行程名稱: $($process.ProcessName)"
        Write-Log "行程 ID: $($process.Id)"
        Write-Log "啟動時間: $($process.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))"
        Write-Log "執行時間: $(((Get-Date) - $process.StartTime).ToString('dd\.hh\:mm\:ss'))"
        Write-Log "記憶體使用: $([math]::Round($process.WorkingSet64 / 1MB, 2)) MB"
        Write-Log "虛擬記憶體: $([math]::Round($process.VirtualMemorySize64 / 1MB, 2)) MB"
        Write-Log "CPU 時間: $($process.TotalProcessorTime.ToString('hh\:mm\:ss'))"
        Write-Log "執行緒數: $($process.Threads.Count)"
        Write-Log "控制代碼數: $($process.HandleCount)"
        
    } catch {
        Write-Log "取得效能資訊時發生錯誤: $($_.Exception.Message)" "ERROR"
    }
}

# 主程式邏輯
Write-Log "==============================================="
Write-Log "JOULARIS Engine 監控與維護工具"
Write-Log "==============================================="

switch ($szAction.ToLower()) {
    "monitor" {
        Show-ServiceMonitor
    }
    "status" {
        $healthStatus = Get-ServiceHealthStatus
        Write-Log "服務狀態: $($healthStatus.Status)"
        Write-Log "描述: $($healthStatus.Message)"
        if ($healthStatus.Details) {
            Write-Log "詳細資訊:"
            $healthStatus.Details.GetEnumerator() | ForEach-Object {
                Write-Log "  $($_.Key): $($_.Value)"
            }
        }
    }
    "logs" {
        Show-ServiceLogs -nTailLines 100
    }
    "performance" {
        Show-ServicePerformance
    }
    "cleanup" {
        Invoke-LogCleanup -nRetainDays 30 -isConfirm $true
    }
    default {
        Write-Log "用法: MonitorService.ps1 [-Action] <monitor|status|logs|performance|cleanup> [-IntervalSeconds <秒數>] [-LogPath <日誌路徑>]"
        Write-Log ""
        Write-Log "動作說明:"
        Write-Log "  monitor      - 即時監控服務狀態（預設）"
        Write-Log "  status       - 顯示服務狀態"
        Write-Log "  logs         - 顯示最新日誌"
        Write-Log "  performance  - 顯示效能資訊"
        Write-Log "  cleanup      - 清理舊日誌檔案"
        Write-Log ""
        Write-Log "範例:"
        Write-Log "  .\MonitorService.ps1                           # 開始即時監控"
        Write-Log "  .\MonitorService.ps1 -Action status           # 查看狀態"
        Write-Log "  .\MonitorService.ps1 -Action logs             # 查看日誌"
        Write-Log "  .\MonitorService.ps1 -Action performance      # 查看效能"
        Write-Log "  .\MonitorService.ps1 -Action cleanup          # 清理日誌"
    }
}