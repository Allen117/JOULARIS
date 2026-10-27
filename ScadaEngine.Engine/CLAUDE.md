# ScadaEngine.Engine 規則

.NET 8 Worker Service：Modbus / DB 來源 / OPC UA 來源採集 → HistoryData / LatestData / MQTT。跨模組資料流見 docs/架構.md。

## Engine 專屬設定檔

共用設定（dbSetting / Line / Email / Sms）見根目錄 CLAUDE.md §Key Configuration Files。

| File | Purpose |
|------|---------|
| `MqttSetting/MqttSetting.json` | MQTT broker IP/port/topic/retain |
| `Modbus/Modbus.json` | Modbus device definitions (IP, port, tags)。由 Web「Modbus 來源」頁匯入 `Modbus範本.xlsx` 產生 |
| `DBPoint/*.json` | DB 來源 Coordinator 點位定義（由 Web「DB 來源」頁**匯入 Excel** 產生，範本 `DB來源範本.xlsx`；Name/Unit 可在頁面熱編輯回寫）。匯入共用 `ScadaEngine.Web/Services/SourceExcel/`。細節見 docs/架構.md §資料流 + docs/功能說明書_DB來源管理.md §8.4 / _Modbus來源管理.md §4.1 |
| `OpcUaPoint/*.json` | OPC UA 來源定義（一檔一 Server 含 Devices 分組；Web「OPC UA 來源」頁全欄位動態編輯回寫，免重啟；亦可同頁匯入 `OPCUA範本.xlsx`，點位以 Seq 欄比對不漂移、匯出不含密碼）。細節見 docs/功能說明書_OPCUA通訊.md |
| `appsettings.json` → `EquipmentAggregation` | 設備效率 hourly 聚合：`MinSamplesPerHour`(30) / `MaxStalenessHours`(2) / `CatchUpHours`(24) / `BackfillBatchSleepMs`(500)。設備與綁定在 DB（Web 設定頁維護，Engine 每小時重讀免重啟）→ docs/功能說明書_設備效率診斷.md §6 |
| `Setting/DbMaintenanceSetting.json` | 自動建 DB 路徑 + 每週備份排程（A/B 輪替、結果寫 EventLog）。同資料夾 `install-db.ps1` 為安裝腳本（idempotent，已綁入部署流程）→ docs/架構.md §資料庫自動建立與每週備份 |
| `Resources/notification.{zh-TW,en}.json` | 通知訊息字典（Line + Email + 簡訊共用，依群組/號碼 Language 切換） |

三種來源的網頁熱編輯在 JSON 記 `WebEditedAt`、匯出 Excel 記 `ScadaExportedAt`，拿舊 Excel 重匯會警告並要求勾選確認。

## 每小時聚合排程

| 時間 | 誰 | 做什麼 |
|------|----|--------|
| XX:03 | Engine | 天然氣表 `GasMeter*` 聚合 |
| XX:04 | Engine | 產氣量表 `AirMeter*` 聚合 |
| XX:05 | **Web** | 電費逐時計價 EnergyLeafHourly → ElectricityCostHourly |
| XX:06 | Engine | 設備效率 `EquipmentHourly` |

## Pitfalls

### MQTT Retain Flag
Engine publishes with `Retain=true`. When restarting Engine, old retained messages (without `name` field) remain on the broker. A full restart of both Engine and broker clears stale retained messages.

### IDataRepository (Scoped)
Defined in `ScadaEngine.Engine` but used by both Engine and Web. Web registers `SqlServerDataRepository` as Scoped. `MqttRealtimeSubscriberService` (Singleton) accesses it via `IServiceProvider.CreateScope()`.
