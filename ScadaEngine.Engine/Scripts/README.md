# SCADA Engine Windows Service 部署指南

## 概述
JOULARIS Engine（Windows 服務名 `JoularisEngine`）是一個基於 .NET 8 的 Windows 服務，提供 Modbus/MQTT 通訊與資料採集功能。本服務設計為永續運行的系統服務，具備自動重啟、日誌記錄、效能監控等生產環境特性。

## 系統需求
- **作業系統**: Windows 10/11 或 Windows Server 2019/2022
- **框架**: .NET 8 Runtime（如使用自包含部署則無需預裝）
- **權限**: 管理員權限（用於服務安裝/卸載）
- **網路**: 支援 TCP/IP（Modbus TCP 和 MQTT 通訊）
- **磁碟空間**: 建議至少 500MB 可用空間

## 快速部署

### 方法一：使用快速部署工具（推薦）
1. 以管理員權限執行 `Scripts\QuickDeploy.bat`
2. 選擇選項 `1` 安裝服務
3. 等待安裝完成

### 方法二：使用 PowerShell 腳本
```powershell
# 以管理員權限執行 PowerShell
cd "C:\Users\A50388.ITRI\Desktop\ScadaEngine\ScadaEngine.Engine\Scripts"
.\DeployService.ps1 -Action install
```

### 方法三：手動部署
```powershell
# 1. 發佈應用程式
dotnet publish --configuration Release --self-contained true --runtime win-x64 --output "C:\SCADA\Engine\App"

# 2. 註冊 Windows Service
sc.exe create JoularisEngine binPath="C:\SCADA\Engine\App\ScadaEngine.Engine.exe" start=auto DisplayName="JOULARIS Engine"

# 3. 設定服務描述
sc.exe description JoularisEngine "JOULARIS Engine | Backend - data collection | vYY.A.B | yyyy-MM-dd"

# 4. 啟動服務
sc.exe start JoularisEngine
```

## 服務管理

### 基本操作
```powershell
# 啟動服務
.\DeployService.ps1 -Action start

# 停止服務
.\DeployService.ps1 -Action stop

# 重啟服務
.\DeployService.ps1 -Action restart

# 查看狀態
.\DeployService.ps1 -Action status

# 卸載服務
.\DeployService.ps1 -Action uninstall
```

### 進階操作
```powershell
# 設定自動恢復選項
.\ConfigureServiceRecovery.ps1

# 即時監控服務
.\MonitorService.ps1 -Action monitor

# 查看日誌
.\MonitorService.ps1 -Action logs

# 查看效能資訊
.\MonitorService.ps1 -Action performance

# 清理舊日誌
.\MonitorService.ps1 -Action cleanup
```

## 服務配置

### 自動重啟策略
服務配置了以下自動恢復機制：
- **第一次失敗**: 等待 30 秒後自動重啟
- **第二次失敗**: 等待 60 秒後自動重啟  
- **後續失敗**: 等待 120 秒後自動重啟
- **重置週期**: 24 小時重置失敗計數器

### 日誌配置
- **日誌位置**: `C:\SCADA\Engine\App\Log\`
- **日誌格式**: 每日滾動，保留 30 天
- **錯誤日誌**: 單獨記錄警告和錯誤，保留 90 天
- **檔案命名**: `ScadaEngine-YYYYMMDD.log`

### 設定檔位置
```
C:\SCADA\Engine\App\
├── Setting\
│   └── dbSetting.json          # (已移至 C:\SCADA\Shared\dbSetting.json，Engine/Web/Gateway 共用；開發讀 repo 這份)
├── Modbus\
│   ├── Modbus.json             # Modbus 設備配置
│   └── Modbus2.json            # 其他 Modbus 設備
├── MqttSetting\
│   └── MqttSetting.json        # MQTT Broker 設定
└── DatabaseSchema\
    └── DatabaseSchema.json     # 資料庫架構定義
```

## 監控與維護

### 即時監控
使用監控腳本可以即時查看：
- 服務運行狀態
- 記憶體使用情況
- CPU 使用時間
- 最新錯誤訊息
- 日誌檔案狀態

```powershell
.\MonitorService.ps1 -Action monitor
```

### 效能監控
定期檢查以下指標：
- **記憶體使用**: 正常範圍 50-200MB
- **CPU 使用**: 正常情況下應保持在 5% 以下
- **日誌檔案**: 注意錯誤和警告訊息
- **網路連線**: Modbus 和 MQTT 連接狀態

### 日誌分析
重要的日誌關鍵字：
- `ERROR` / `FATAL`: 嚴重錯誤，需要立即處理
- `WARN`: 警告訊息，建議關注
- `INFO`: 一般資訊，包含啟動/停止等事件
- `Modbus`: Modbus 通訊相關訊息
- `MQTT`: MQTT 連線與發布相關訊息

## 故障排除

### 常見問題

#### 服務無法啟動
1. 檢查管理員權限
2. 確認設定檔是否正確
3. 查看 Windows 事件檢視器
4. 檢查防火牆設定
5. 確認資料庫連線

```powershell
# 檢查詳細錯誤
.\MonitorService.ps1 -Action logs | Select-String "ERROR|FATAL"
```

#### 服務頻繁重啟
1. 檢查資料庫連線穩定性
2. 確認 Modbus 設備可達性
3. 檢查 MQTT Broker 連線
4. 監控記憶體使用情況
5. 查看詳細錯誤日誌

#### 記憶體洩漏
1. 使用效能監控功能
2. 重啟服務釋放記憶體
3. 檢查 Modbus 連線池設定
4. 分析日誌中的異常模式

### 除錯模式
如需詳細除錯，可暫時以 Console 模式執行：
```cmd
# 停止 Windows Service
sc.exe stop JoularisEngine

# 以 Console 模式執行
cd "C:\SCADA\Engine\App"
ScadaEngine.Engine.exe
```

## 安全性考量

### 檔案權限
- 確保服務執行帳戶對應用程式目錄有讀取權限
- 設定檔目錄應限制寫入權限
- 日誌目錄需要寫入權限

### 網路安全
- 配置防火牆規則允許 Modbus TCP (502) 和 MQTT (1883/8883) 通訊
- 使用 MQTT 認證機制
- 定期更新密碼和憑證

### 資料保護
- 敏感設定使用加密儲存
- 定期備份設定檔
- 限制對設定檔的存取權限

## 效能優化

### 系統調優
- 為服務設定適當的 CPU 親和性
- 調整 .NET GC 設定以適應長期運行
- 監控並調整 Modbus 輪詢間隔
- 優化 MQTT 發布頻率

### 資源配置
- **最小記憶體**: 256MB
- **建議記憶體**: 512MB
- **最小磁碟空間**: 1GB
- **建議磁碟空間**: 5GB（包含日誌儲存）

## 備份與恢復

### 備份內容
定期備份以下檔案：
- 所有設定檔（Setting/, Modbus/, Mqtt/, DatabaseSchema/）
- 應用程式執行檔
- 服務註冊資訊
- 重要日誌檔案

### 恢復程序
1. 停止現有服務
2. 恢復設定檔
3. 重新註冊服務（如需要）
4. 驗證服務啟動
5. 檢查功能正常

## 聯絡資訊
- **技術支援**: 工研院 SCADA 團隊
- **文檔版本**: v1.0
- **最後更新**: $(Get-Date -Format 'yyyy-MM-dd')

---
**注意**: 此服務涉及工業控制系統，請確保在測試環境中充分驗證後再部署至生產環境。