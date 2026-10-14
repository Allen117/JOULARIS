# JOULARIS Web Deployment Script
# Encoding: UTF-8

param([string]$Action = "status")

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectPath = Split-Path -Parent $ScriptPath

# 目標部署路徑
$TargetPath = "C:\JOULARIS\Web\App"

# Service configuration
$ServiceName = "JoularisWeb"
$ServiceDisplayName = "JOULARIS Web"
$ServiceDescription = "SCADA Web Dashboard (http://0.0.0.0:5038)"

# Function: Check service status
function Get-ServiceStatus {
    try {
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
    } catch {
        Write-Host "Failed to get service status: $($_.Exception.Message)"
        return $false
    }
}

# Function: Install service
function Install-Service {
    Write-Host "Starting Web service installation..."
    try {
        Write-Host "Building project with self-contained deployment..."
        Set-Location $ProjectPath

        if (Test-Path "$ProjectPath\bin\Release\Publish") {
            Remove-Item "$ProjectPath\bin\Release\Publish" -Recurse -Force
        }

        Write-Host "Publishing self-contained application (includes .NET 8 runtime)..."
        dotnet publish -c Release --self-contained true --runtime win-x64 -o "$ProjectPath\bin\Release\Publish"

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Self-contained publish failed. Trying framework-dependent..." -ForegroundColor Yellow
            dotnet clean
            dotnet restore --force
            dotnet publish -c Release --self-contained false -o "$ProjectPath\bin\Release\Publish"
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Build failed. Exiting." -ForegroundColor Red
                return $false
            }
            Write-Host "WARNING: Framework-dependent build. .NET 8 Runtime must be installed on target machine." -ForegroundColor Yellow
        } else {
            Write-Host "Self-contained build successful. No .NET Runtime installation required." -ForegroundColor Green
        }

        # Create target directory
        Write-Host "Creating target directory: $TargetPath"
        if (-not (Test-Path $TargetPath)) {
            New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
        } else {
            Write-Host "Cleaning existing installation..."
            try {
                $processes = Get-Process -Name "ScadaEngine.Web*" -ErrorAction SilentlyContinue
                if ($processes) {
                    Write-Host "Terminating existing ScadaEngine.Web processes..."
                    $processes | ForEach-Object {
                        try { $_.Kill(); Start-Sleep 1 } catch { }
                    }
                }
                Start-Sleep -Seconds 3
                Get-ChildItem -Path $TargetPath -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                    try {
                        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
                    } catch {
                        Write-Host "Warning: Could not remove $($_.Name) (may be in use)" -ForegroundColor Yellow
                    }
                }
            } catch {
                Write-Host "Warning during cleanup: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

        # Create config subdirectories
        $SubDirs = @("Setting", "MqttSetting", "DatabaseSchema", "Log")
        foreach ($dir in $SubDirs) {
            $dirPath = Join-Path $TargetPath $dir
            if (-not (Test-Path $dirPath)) { New-Item -ItemType Directory -Path $dirPath -Force | Out-Null }
        }

        # Copy published files (includes wwwroot)
        Write-Host "Copying application files..."
        $publishPath = "$ProjectPath\bin\Release\Publish"
        Copy-Item -Path "$publishPath\*" -Destination $TargetPath -Recurse -Force
        Write-Host "Application files copied." -ForegroundColor Green

        # dbSetting.json is NOT copied here: Web reads the machine-wide C:\JOULARIS\Shared\dbSetting.json
        # (DbSettingLocator, same resolution as Engine / Gateway). Install.bat creates / migrates that file.
        $enginePath = Join-Path (Split-Path -Parent $ProjectPath) "ScadaEngine.Engine"

        # Copy Engine DatabaseSchema
        $engineSchema = Join-Path $enginePath "DatabaseSchema\DatabaseSchema.json"
        if (Test-Path $engineSchema) {
            Copy-Item -Path $engineSchema -Destination (Join-Path $TargetPath "DatabaseSchema\DatabaseSchema.json") -Force
            Write-Host "Copied: Engine DatabaseSchema.json"
        }

        # Copy Web MQTT setting
        $webMqttSetting = Join-Path $ProjectPath "MqttSetting\MqttSetting.json"
        if (Test-Path $webMqttSetting) {
            Copy-Item -Path $webMqttSetting -Destination (Join-Path $TargetPath "MqttSetting\MqttSetting.json") -Force
            Write-Host "Copied: MqttSetting.json"
        }

        $ExePath = Join-Path $TargetPath "ScadaEngine.Web.exe"
        if (-not (Test-Path $ExePath)) { Write-Host "Executable not found: $ExePath" -ForegroundColor Red; return $false }

        # Install Windows Service
        Write-Host "Installing Windows Service..."
        sc.exe create $ServiceName `
            binPath= "`"$ExePath`"" `
            DisplayName= "`"$ServiceDisplayName`"" `
            start= auto | Out-Null

        sc.exe description $ServiceName $ServiceDescription | Out-Null
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

        Write-Host ""
        Write-Host "========================================" -ForegroundColor Green
        Write-Host "  Web Service installed successfully!" -ForegroundColor Green
        Write-Host "  Path: $TargetPath" -ForegroundColor Green
        Write-Host "  URL:  http://0.0.0.0:5038" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Green
        Write-Host ""
        Write-Host "Configuration files:"
        Write-Host "  - ..\..\Shared\dbSetting.json  (DB connection, C:\JOULARIS\Shared - shared with Engine)"
        Write-Host "  - ..\..\Shared\EmailSetting.json / SmsSetting.json / LineSetting.json  (notify configs, C:\JOULARIS\Shared - Web UI writes, Engine hot-reloads)"
        Write-Host "  - MqttSetting\MqttSetting.json (MQTT broker)"
        Write-Host ""
        Write-Host "Next: Run '.\DeployWebService.ps1 start' to start the service"
        return $true

    } catch {
        Write-Host "Installation error: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function: Uninstall service
function Uninstall-Service {
    Write-Host "Starting service removal..."
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -eq 'Running') {
                Write-Host "Stopping service..."
                Stop-Service -Name $ServiceName -Force
                Start-Sleep -Seconds 5
            }
        }

        # Force kill processes
        $processes = Get-Process -Name "ScadaEngine.Web*" -ErrorAction SilentlyContinue
        if ($processes) {
            Write-Host "Terminating ScadaEngine.Web processes..."
            foreach ($proc in $processes) {
                try { $proc.Kill() } catch { }
            }
            Start-Sleep -Seconds 3
        }

        # Delete service
        Write-Host "Removing Windows service..."
        sc.exe delete $ServiceName | Out-Null

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Service removed successfully!" -ForegroundColor Green
        } else {
            Write-Host "Warning: Service deletion may have failed" -ForegroundColor Yellow
        }

        $response = Read-Host "Do you want to delete application files in $TargetPath? (y/N)"
        if ($response -eq 'y' -or $response -eq 'Y') {
            try {
                Remove-Item -Path $TargetPath -Recurse -Force
                Write-Host "Application files removed." -ForegroundColor Green
            } catch {
                Write-Host "Could not remove all files: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "Application files preserved in: $TargetPath"
        }
        return $true
    } catch {
        Write-Host "Removal error: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function: Start service
function Start-ServiceInstance {
    Write-Host "Starting service..."
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $service) {
            Write-Host "Service not found. Please install first." -ForegroundColor Red
            return $false
        }

        if ($service.Status -eq 'Running') {
            Write-Host "Service is already running." -ForegroundColor Green
            return $true
        }

        Start-Service -Name $ServiceName

        $timeout = 30
        $elapsed = 0
        do {
            Start-Sleep -Seconds 1
            $elapsed++
            $service = Get-Service -Name $ServiceName
            Write-Host "Waiting for service to start... ($elapsed/$timeout) - Status: $($service.Status)"
        } while ($service.Status -ne 'Running' -and $service.Status -ne 'Stopped' -and $elapsed -lt $timeout)

        if ($service.Status -eq 'Running') {
            Write-Host ""
            Write-Host "Service started successfully!" -ForegroundColor Green
            Write-Host "Access URL: http://localhost:5038" -ForegroundColor Cyan
            return $true
        } else {
            Write-Host "Service failed to start. Status: $($service.Status)" -ForegroundColor Red
            Write-Host "Check Event Viewer: eventvwr.msc -> Windows Logs -> System" -ForegroundColor Yellow
            Write-Host "Or test manually: cd `"$TargetPath`" && .\ScadaEngine.Web.exe" -ForegroundColor Yellow
            return $false
        }
    } catch {
        Write-Host "Start failed: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function: Stop service
function Stop-ServiceInstance {
    Write-Host "Stopping service..."
    try { Stop-Service -Name $ServiceName -Force; Start-Sleep 2; Get-ServiceStatus; return $true } catch { Write-Host "Stop failed: $($_.Exception.Message)"; return $false }
}

# Function: Update service
function Update-Service {
    Write-Host "Updating service files..."
    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $wasRunning = $false
        if ($service -and $service.Status -eq 'Running') {
            $wasRunning = $true
            Write-Host "Stopping service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep 5

            $processes = Get-Process -Name "ScadaEngine.Web*" -ErrorAction SilentlyContinue
            if ($processes) {
                Write-Host "Force killing remaining processes..."
                $processes | ForEach-Object { try { $_.Kill() } catch { } }
                Start-Sleep 3
            }
        }

        Set-Location $ProjectPath
        if (Test-Path "$ProjectPath\bin\Release\Publish") {
            Remove-Item "$ProjectPath\bin\Release\Publish" -Recurse -Force
        }
        Write-Host "Publishing self-contained application..."
        dotnet publish -c Release --self-contained true --runtime win-x64 -o "$ProjectPath\bin\Release\Publish"
        if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; return $false }

        # Update all files (includes wwwroot)
        $publishPath = "$ProjectPath\bin\Release\Publish"
        Write-Host "Copying updated files..."
        Copy-Item -Path "$publishPath\*" -Destination $TargetPath -Recurse -Force
        Write-Host "Files updated." -ForegroundColor Green

        # Sync config files
        $enginePath = Join-Path (Split-Path -Parent $ProjectPath) "ScadaEngine.Engine"
        # (dbSetting.json deliberately absent: Web reads C:\JOULARIS\Shared\dbSetting.json, never a local copy)
        $configsToCopy = @(
            @{ Src = (Join-Path $enginePath "DatabaseSchema\DatabaseSchema.json"); Dst = (Join-Path $TargetPath "DatabaseSchema\DatabaseSchema.json") },
            @{ Src = (Join-Path $ProjectPath "MqttSetting\MqttSetting.json"); Dst = (Join-Path $TargetPath "MqttSetting\MqttSetting.json") }
        )
        foreach ($cfg in $configsToCopy) {
            if (Test-Path $cfg.Src) {
                Copy-Item -Path $cfg.Src -Destination $cfg.Dst -Force
                Write-Host "Config: $(Split-Path -Leaf $cfg.Src)"
            }
        }

        if ($wasRunning) { Start-Service -Name $ServiceName; Start-Sleep 2 }

        Get-ServiceStatus
        Write-Host "Update completed!" -ForegroundColor Green
        return $true
    } catch { Write-Host "Update error: $($_.Exception.Message)" -ForegroundColor Red; return $false }
}

# Function: Show logs
function Show-Logs {
    $logPath = Join-Path $TargetPath "Log"
    if (Test-Path $logPath) {
        Write-Host "Log files in $logPath :"
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

# Function: Diagnose
function Diagnose-Service {
    Write-Host "=== JOULARIS Web Diagnostics ===" -ForegroundColor Cyan

    Write-Host "`n1. Service Status:" -ForegroundColor Yellow
    Get-ServiceStatus

    Write-Host "`n2. Application Files:" -ForegroundColor Yellow
    $ExePath = Join-Path $TargetPath "ScadaEngine.Web.exe"
    Write-Host "  Executable: $(if (Test-Path $ExePath) { 'OK' } else { 'MISSING' })"
    $wwwrootPath = Join-Path $TargetPath "wwwroot"
    Write-Host "  wwwroot: $(if (Test-Path $wwwrootPath) { 'OK' } else { 'MISSING' })"

    Write-Host "`n3. Configuration Files:" -ForegroundColor Yellow
    $configFiles = @('..\..\Shared\dbSetting.json', '..\..\Shared\EmailSetting.json', '..\..\Shared\SmsSetting.json', '..\..\Shared\LineSetting.json', 'MqttSetting\MqttSetting.json', 'DatabaseSchema\DatabaseSchema.json')
    foreach ($config in $configFiles) {
        $configPath = Join-Path $TargetPath $config
        $exists = Test-Path $configPath
        $color = if ($exists) { "Green" } else { "Red" }
        Write-Host "  $config`: $(if ($exists) { 'OK' } else { 'MISSING' })" -ForegroundColor $color
    }

    Write-Host "`n4. Port 5038:" -ForegroundColor Yellow
    $fwRule = Get-NetFirewallRule -DisplayName "ScadaEngine Web" -ErrorAction SilentlyContinue
    if ($fwRule) {
        Write-Host "  Firewall rule: OK (Enabled=$($fwRule.Enabled))" -ForegroundColor Green
    } else {
        Write-Host "  Firewall rule: NOT FOUND" -ForegroundColor Red
        Write-Host "  Run: New-NetFirewallRule -DisplayName 'ScadaEngine Web' -Direction Inbound -Port 5038 -Protocol TCP -Action Allow" -ForegroundColor Yellow
    }

    Write-Host "`n5. Recent Errors:" -ForegroundColor Yellow
    try {
        $events = Get-WinEvent -FilterHashtable @{LogName='System'; Level=2; StartTime=(Get-Date).AddHours(-1)} -ErrorAction SilentlyContinue |
                  Where-Object {$_.Message -like "*$ServiceName*" -or $_.Message -like "*ScadaEngine.Web*"} |
                  Select-Object -First 3
        if ($events) {
            foreach ($event in $events) { Write-Host "  [$($event.TimeCreated)] $($event.Message)" -ForegroundColor Red }
        } else {
            Write-Host "  No recent errors" -ForegroundColor Green
        }
    } catch { Write-Host "  Unable to check event log" -ForegroundColor Yellow }

    Write-Host "=========================================" -ForegroundColor Cyan
}

# Function: Force cleanup
function Force-Cleanup {
    Write-Host "=== FORCE CLEANUP ===" -ForegroundColor Red
    $confirm = Read-Host "Are you sure? (y/N)"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') { Write-Host "Cancelled."; return }

    try {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service) { try { Stop-Service -Name $ServiceName -Force } catch { }; sc.exe delete $ServiceName | Out-Null }
        Get-Process -Name "*ScadaEngine.Web*" -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
        Start-Sleep -Seconds 5
        if (Test-Path $TargetPath) {
            Get-ChildItem -Path $TargetPath -Recurse -Force | ForEach-Object { try { $_.Attributes = 'Normal' } catch { } }
            Remove-Item -Path $TargetPath -Recurse -Force
            Write-Host "Cleanup completed." -ForegroundColor Green
        }
    } catch { Write-Host "Cleanup error: $($_.Exception.Message)" -ForegroundColor Red }
}

# Main execution
switch ($Action.ToLower()) {
    "install"   { if (Get-ServiceStatus) { Write-Host "Service already installed. Use 'uninstall' first or 'update'." } else { Install-Service } }
    "uninstall" { if (Get-ServiceStatus) { Uninstall-Service } else { Write-Host "Service not found." } }
    "start"     { if (Get-ServiceStatus) { Start-ServiceInstance } else { Write-Host "Service not installed." } }
    "stop"      { if (Get-ServiceStatus) { Stop-ServiceInstance } else { Write-Host "Service not installed." } }
    "restart"   { if (Get-ServiceStatus) { Stop-ServiceInstance; Start-Sleep 2; Start-ServiceInstance } else { Write-Host "Service not installed." } }
    "update"    { Update-Service }
    "logs"      { Show-Logs }
    "status"    { Get-ServiceStatus }
    "diagnose"  { Diagnose-Service }
    "cleanup"   { Force-Cleanup }
    default {
        Write-Host "JOULARIS Web Deployment Tool" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Available actions:"
        Write-Host "  .\DeployWebService.ps1 install   - Install service to $TargetPath"
        Write-Host "  .\DeployWebService.ps1 uninstall - Remove service"
        Write-Host "  .\DeployWebService.ps1 start     - Start service"
        Write-Host "  .\DeployWebService.ps1 stop      - Stop service"
        Write-Host "  .\DeployWebService.ps1 restart   - Restart service"
        Write-Host "  .\DeployWebService.ps1 update    - Rebuild and update files"
        Write-Host "  .\DeployWebService.ps1 logs      - Show recent logs"
        Write-Host "  .\DeployWebService.ps1 status    - Check service status"
        Write-Host "  .\DeployWebService.ps1 diagnose  - Run diagnostics"
        Write-Host "  .\DeployWebService.ps1 cleanup   - Force cleanup"
    }
}
