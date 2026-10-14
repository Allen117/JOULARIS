# JOULARIS Modbus Gateway Deployment Script
# Encoding: UTF-8
# 仿 Engine Scripts\DeployService.ps1，精簡版（無 Python / LicenseBridge / DB setup）

param([string]$Action = "status")

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectPath = Split-Path -Parent $ScriptPath

# 目標部署路徑
$TargetPath = "C:\JOULARIS\ModbusServer\App"

# Service configuration
$ServiceName = "JoularisModbusGateway"
$ServiceDisplayName = "JOULARIS Modbus Gateway"
$ServiceDescription = "SCADA realtime data Modbus TCP gateway (FC4 input registers, float32)"

function Get-ConfiguredPorts {
    # 從部署目錄（優先）或專案目錄讀取埠設定，供防火牆規則使用
    $settingFile = Join-Path $TargetPath "Setting\ModbusServerSetting.json"
    if (-not (Test-Path $settingFile)) { $settingFile = Join-Path $ProjectPath "Setting\ModbusServerSetting.json" }
    $modbusPort = 502; $webPort = 5041
    if (Test-Path $settingFile) {
        try {
            $cfg = Get-Content $settingFile -Raw | ConvertFrom-Json
            if ($cfg.ModbusListenPort) { $modbusPort = [int]$cfg.ModbusListenPort }
            if ($cfg.WebPort) { $webPort = [int]$cfg.WebPort }
        } catch { Write-Host "Warning: cannot parse $settingFile, using default ports" -ForegroundColor Yellow }
    }
    return @($modbusPort, $webPort)
}

function Get-ServiceStatus {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host "Service Found: $ServiceDisplayName"
        Write-Host "Status: $($service.Status)"
        Write-Host "Start Type: $($service.StartType)"
        return $true
    } else {
        Write-Host "Service not found: $ServiceName"
        return $false
    }
}

# 只殺部署路徑下的 Gateway 進程 — 開發機上可能同時有 repo bin 的 console 測試實例，不可盲殺
function Stop-GatewayProcesses {
    $processes = Get-Process -Name "ScadaEngine.ModbusServer" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$TargetPath\*" }
    if ($processes) {
        Write-Host "Terminating deployed gateway processes..."
        $processes | ForEach-Object { try { $_.Kill(); Start-Sleep 1 } catch { } }
        Start-Sleep -Seconds 2
    }
}

function Publish-Project {
    Set-Location $ProjectPath
    if (Test-Path "$ProjectPath\bin\Release\Publish") {
        Remove-Item "$ProjectPath\bin\Release\Publish" -Recurse -Force
    }
    Write-Host "Publishing self-contained application (includes .NET 8 runtime)..."
    dotnet publish -c Release --self-contained true --runtime win-x64 -o "$ProjectPath\bin\Release\Publish"
    if ($LASTEXITCODE -ne 0) { Write-Host "Publish FAILED" -ForegroundColor Red; return $false }
    return $true
}

function Copy-Deployment {
    # publish 輸出整包複製（含 deps.json / runtimeconfig.json — 不可只挑 exe/dll）
    $publishPath = "$ProjectPath\bin\Release\Publish"
    if (-not (Test-Path $TargetPath)) { New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null }
    Copy-Item -Path "$publishPath\*" -Destination $TargetPath -Recurse -Force

    # 設定目錄（首次部署帶入；升級時站台設定與 AddressMap.json 已在 TargetPath、publish 輸出不含它們，
    # 但 Copy 仍會以專案版覆蓋 Setting/MqttSetting — 開發機 workflow 可接受；正式站台請走 InstallModbusServer.bat）
    foreach ($d in @("Setting", "MqttSetting", "Web")) {
        $src = Join-Path $ProjectPath $d
        $dst = Join-Path $TargetPath $d
        if (Test-Path $src) {
            if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
            Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force
            Write-Host "Copied: $d\"
        }
    }
    $logDir = Join-Path $TargetPath "Log"
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
}

function Add-FirewallRules {
    $ports = Get-ConfiguredPorts
    netsh advfirewall firewall delete rule name="SCADA Modbus Gateway TCP" | Out-Null
    netsh advfirewall firewall delete rule name="SCADA Modbus Gateway Web" | Out-Null
    netsh advfirewall firewall add rule name="SCADA Modbus Gateway TCP" dir=in action=allow protocol=TCP localport=$($ports[0]) | Out-Null
    netsh advfirewall firewall add rule name="SCADA Modbus Gateway Web" dir=in action=allow protocol=TCP localport=$($ports[1]) | Out-Null
    Write-Host "Firewall rules added: Modbus TCP $($ports[0]), Web $($ports[1])" -ForegroundColor Green
}

function Install-GatewayService {
    Write-Host "Starting service installation..."
    try {
        if (-not (Publish-Project)) { return $false }
        Stop-GatewayProcesses
        Copy-Deployment

        $ExePath = Join-Path $TargetPath "ScadaEngine.ModbusServer.exe"
        if (-not (Test-Path $ExePath)) { Write-Host "Executable not found: $ExePath"; return $false }

        Write-Host "Installing Windows Service..."
        sc.exe create $ServiceName `
            binPath= "`"$ExePath`"" `
            DisplayName= "`"$ServiceDisplayName`"" `
            start= auto | Out-Null
        sc.exe description $ServiceName $ServiceDescription | Out-Null
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

        Add-FirewallRules

        Write-Host "Service installed to: $TargetPath" -ForegroundColor Green
        Write-Host "Config files:"
        Write-Host '  - Setting\ModbusServerSetting.json  (ports / word order / segments)'
        Write-Host '  - ..\..\Shared\dbSetting.json       (SQL connection; same box as Engine/Web -> C:\JOULARIS\Shared)'
        Write-Host '  - Setting\dbSetting.json            (SQL connection; standalone box only, seeded from dbSetting.example.json)'
        Write-Host '  - MqttSetting\MqttSetting.json      (MQTT broker)'
        Write-Host '  - AddressMap.json                   (auto-generated, DO NOT delete: address assignments)'
        return $true
    } catch {
        Write-Host "Installation error: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Uninstall-GatewayService {
    Write-Host "Starting service removal..."
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-Host "Stopping service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 5
        }
        Stop-GatewayProcesses

        Write-Host "Removing Windows service..."
        sc.exe delete $ServiceName | Out-Null

        Write-Host "Removing firewall rules..."
        netsh advfirewall firewall delete rule name="SCADA Modbus Gateway TCP" | Out-Null
        netsh advfirewall firewall delete rule name="SCADA Modbus Gateway Web" | Out-Null

        $response = Read-Host "Delete application files in $TargetPath ? AddressMap.json will be lost! (y/N)"
        if ($response -eq 'y' -or $response -eq 'Y') {
            Remove-Item -Path $TargetPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "Application files removed."
        } else {
            Write-Host "Application files preserved in: $TargetPath (AddressMap.json kept)"
        }
        Write-Host "Service removed." -ForegroundColor Green
        return $true
    } catch {
        Write-Host "Removal error: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Update-GatewayService {
    Write-Host "Updating service files..."
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $wasRunning = $false
        if ($service -and $service.Status -eq 'Running') {
            $wasRunning = $true
            Stop-Service -Name $ServiceName -Force
            Start-Sleep 5
        }
        Stop-GatewayProcesses

        if (-not (Publish-Project)) { return $false }
        Copy-Deployment

        if ($wasRunning) { Start-Service -Name $ServiceName; Start-Sleep 2 }
        Get-ServiceStatus
        Write-Host "Update completed!" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "Update error: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Start-GatewayService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { Write-Host "Service not found. Install first." -ForegroundColor Red; return $false }
    if ($service.Status -eq 'Running') { Write-Host "Service already running." -ForegroundColor Green; return $true }
    Start-Service -Name $ServiceName
    Start-Sleep 3
    Get-ServiceStatus
    $ports = Get-ConfiguredPorts
    Write-Host "Browse the address map at: http://localhost:$($ports[1])" -ForegroundColor Cyan
    return $true
}

function Stop-GatewayService {
    try { Stop-Service -Name $ServiceName -Force; Start-Sleep 2; Get-ServiceStatus; return $true }
    catch { Write-Host "Stop failed: $($_.Exception.Message)"; return $false }
}

function Show-Logs {
    $logPath = Join-Path $TargetPath "Log"
    if (Test-Path $logPath) {
        Get-ChildItem -Path $logPath -File | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | ForEach-Object {
            Write-Host "  $($_.Name) - $($_.LastWriteTime)"
        }
        $latestLog = Get-ChildItem -Path $logPath -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latestLog) {
            Write-Host "`nLatest log content ($($latestLog.Name)):"
            Write-Host "=================================="
            Get-Content -Path $latestLog.FullName -Tail 20
        }
    } else { Write-Host "Log directory not found: $logPath" }
}

switch ($Action.ToLower()) {
    "install" { if (Get-ServiceStatus) { Write-Host "Service already installed. Use 'uninstall' first or 'update'." } else { Install-GatewayService } }
    "uninstall" { if (Get-ServiceStatus) { Uninstall-GatewayService } else { Write-Host "Service not found. Nothing to uninstall." } }
    "start" { Start-GatewayService }
    "stop" { Stop-GatewayService }
    "restart" { Stop-GatewayService; Start-Sleep 2; Start-GatewayService }
    "update" { Update-GatewayService }
    "logs" { Show-Logs }
    "status" { Get-ServiceStatus }
    default {
        Write-Host "Available actions: install, uninstall, start, stop, restart, update, logs, status"
        Write-Host ""
        Write-Host "Examples:"
        Write-Host "  .\DeployModbusServer.ps1 install   - Install service to $TargetPath"
        Write-Host "  .\DeployModbusServer.ps1 update    - Stop + Build + Deploy + Start"
        Write-Host "  .\DeployModbusServer.ps1 logs      - Show recent log files"
    }
}
