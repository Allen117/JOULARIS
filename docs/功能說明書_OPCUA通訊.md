# 功能說明書 — OPC UA 通訊

Engine 第三種資料來源：**OPC UA Client**。向外部 OPC UA Server 週期讀取點位，匯入既有 History / Latest / MQTT / 警報 / 計算點位 pipeline；可控點位（AO/DO）支援從 ScadaPage 寫回。與 Modbus（唯讀 JSON）/ DB 來源（Excel 巨集產生）最大差異：**所有欄位可在 Web 動態編輯，免重啟 Engine**。

實作計畫與決策記錄：`docs/plans/2026-07-04-opcua-protocol.md`（歸檔後見 `_archive/`）。

---

## 目錄

- [整體架構](#整體架構)
- [設定檔（OpcUaPoint/*.json）](#設定檔opcuapointjson)
- [SID 與 Seq 規則](#sid-與-seq-規則)
- [Engine 端行為](#engine-端行為)
- [Web 管理頁](#web-管理頁)
- [控制寫回](#控制寫回)
- [資料庫表](#資料庫表)
- [已知限制（Phase 2 候選）](#已知限制phase-2-候選)

---

## 整體架構

```
Web「OPC UA 來源」頁（新增/編輯/刪除 Server、Device、點位）
    ↓ 驗證 → 回寫 OpcUaPoint/{Name}.json → UPSERT DB → 發 SCADA/Sys/OpcUaCoordinator/Reload
Engine OpcUaReloadSubscriber → OpcUaCommunicationService.ReloadAsync()
    ↓ 重讀全部 JSON → UPSERT OpcUaCoordinator/OpcUaPoints → 重建 Session + polling loops（不需重啟）
每 Server 一條 polling loop：
    Session（SecurityPolicy None + 帳密/Anonymous）→ RegisterNodes → 分塊批次 Read
    → 工程值 = 原始值 × Ratio → 變化偵測 → HistoryData / LatestData / MQTT / 警報 / 計算點位
```

- **JSON 為 source of truth，DB 為執行期快照**（比照 DBPoint 線）。設定可攜：複製 `OpcUaPoint/*.json` 即可搬遷/備份。
- **Web 寫檔路徑由 appsettings.json 明定**（比照 Modbus 熱編輯線的 `EngineModbusConfig` 慣例，禁止猜測式 fallback）：

  ```json
  "EngineOpcUaConfig": {
    "WatchedFolder": "../../Engine/App/OpcUaPoint"   // Engine 實際讀取的部署資料夾（主寫入目標），相對 Web\App → <安裝根目錄>\Engine\App
  }
  ```

  相對路徑以 Web ContentRoot 為基準；`WatchedFolder` 未設定時存檔直接報錯（不亂寫）。鏡像寫回/刪檔失敗僅記 log，不影響主寫入。
  正式環境用相對路徑、不寫死安裝根目錄（26.6.0 起）；`appsettings.Development.json` 覆寫為 `../ScadaEngine.Engine/OpcUaPoint`（dev Engine `dotnet run` 讀 repo 這份），不碰生產 Engine 資料夾。`MirrorFolder`（可選）仍支援但兩者皆未設。

- **原子寫檔**：先寫 `*.json.tmp` 再 `File.Replace` 原子替換（留 `*.json.bak` 備份），Engine reload 任何瞬間讀到的都是完整舊檔或完整新檔。
- Web 與 Engine 需**同機部署**（分機部署需改走 DB 或 API）。

## 設定檔（OpcUaPoint/*.json）

一檔 = 一個 OPC UA Server（Coordinator），檔名 = Name。範本見 `ScadaEngine.Engine/OpcUaPoint/OpcUaExample.json.example`（`.example` 副檔名不會被載入，避免在正式環境產生假資料）。

```json
{
  "Name": "OpcUa1",
  "EndpointUrl": "opc.tcp://192.168.1.10:4840",
  "Username": "user1",
  "Password": "pass1",
  "PollingInterval": 1000,
  "ConnectTimeout": 5000,
  "MonitorEnabled": true,
  "MaxNodesPerRead": 0,
  "NextSeq": 4,
  "Devices": [
    {
      "Name": "D1",
      "Tags": [
        { "Seq": 1, "Name": "溫度", "TagName": "ns=2;s=D1.T", "ControlType": "", "Ratio": "1", "Unit": "°C", "Min": 0, "Max": 100 },
        { "Seq": 2, "Name": "頻率設定", "TagName": "ns=2;s=D1.Freq", "ControlType": "AO", "Ratio": "0.1", "Unit": "Hz" }
      ]
    },
    { "Name": "D2", "Tags": [ { "Seq": 3, "Name": "溫度", "TagName": "ns=2;s=D2.T", "ControlType": "", "Ratio": "1", "Unit": "°C" } ] }
  ]
}
```

| 欄位 | 說明 |
|---|---|
| `Name` | = 檔名 = MQTT topic 段 = DB UPSERT key。僅英數/`_`/`-`，**建立後不可改**（改名會產生新 Id → SID 漂移） |
| `Username` / `Password` | 帳密驗證；`Username` 空 = Anonymous。明文存放，比照 `dbSetting.json` 慣例 |
| `PollingInterval` | 掃描週期（ms），下限 200（服務內 clamp） |
| `ConnectTimeout` | 連線/Read/Write 操作逾時（ms），下限 1000 |
| `MaxNodesPerRead` | 分塊 fallback 覆寫；0 = 依 Server `OperationLimits.MaxNodesPerRead`（讀不到用 500） |
| `NextSeq` | Web 配號依據（單調遞增、刪除不回收）；Engine loader 忽略 |
| `Tags[].TagName` | 完整 OPC UA NodeId 字串（`ns=2;s=...` / `ns=3;i=1234` 皆可），Web 有「測試讀取」驗證 |
| `Tags[].ControlType` | `""` 唯讀 / `"AO"` 類比寫入 / `"DO"` 數位寫入（0/1） |
| `Tags[].Ratio` | 工程值 = 原始值 × Ratio；寫回原始值 = 輸入值 ÷ Ratio。字串或數字皆可（比照 Modbus 慣例用字串） |
| `Tags[].Min/Max` | 選填，UI 顯示範圍 / 趨勢圖用 |

點位數**不設上限**（單一 JSON 全部讀完，Engine 以 MaxNodesPerRead 分塊批次處理）。

## SID 與 Seq 規則

- SID = `OPC{CoordinatorId}-S{Seq}`，例 `OPC1-S5`。前綴 `OPC` 用於 Web 快取分流與 Engine 控制分流（比照 `DB` 前綴慣例）。
- `CoordinatorId` 由 DB UPSERT by Name 保持穩定；`Seq` 由 **Web 配號並持久化於 JSON**：
  - 新點位 Seq=0 送後端 → 後端取 `max(NextSeq, DB 最大 Seq + 1, 傳入最大 Seq + 1)` 起連續配號
  - 刪除不回收 — 即使刪掉最大號點位，新點位也不會重用舊 Seq（`NextSeq` 單調遞增）
  - 這與 DBPoint「依陣列順序自動編號」不同，是動態編輯的必要設計：SID 被 HistoryData / 警報規則 / 計算點位引用，插入/刪除點位不可位移其他點位的 SID
- 手寫 JSON 漏 `Seq` 時 loader 會自動補號（log warning）；重複 Seq 後者跳過。

## Engine 端行為

| 元件 | 檔案 | 職責 |
|---|---|---|
| `OpcUaConfigLoader` | `Services/OpcUaConfigLoader.cs` | 掃描 `OpcUaPoint/*.json` → UPSERT `OpcUaCoordinator`（by Name）→ DELETE+INSERT `OpcUaPoints` |
| `OpcUaCommunicationService` | `Services/OpcUaCommunicationService.cs` | 每 Server 一條 Session + polling loop；`ReloadAsync()` 熱重載；`WriteNodeAsync()` 控制寫回 |
| `OpcUaReloadSubscriber` | `Services/OpcUaReloadSubscriber.cs` | 訂閱 `SCADA/Sys/OpcUaCoordinator/Reload` → 呼叫 `ReloadAsync()` |
| `OpcUaClientHelper` | `Services/OpcUaClientHelper.cs` | ApplicationConfiguration / Session 建立 / 值型別轉換（Engine 與 Web 測試讀取共用） |

採集細節：

- **連線**：SecurityPolicy None + UserIdentity（帳密或 Anonymous）；Server 憑證一律接受（Phase 1）。Client 自簽憑證存 `BaseDirectory/OpcUaPki/`（首次自動產生，失敗不阻擋）。
- **RegisterNodes**：連線時將字串 NodeId 註冊為 Server 端 handle 降低每輪解析成本；Server 不支援時退回原始 NodeId。斷線重連後重新註冊。
- **分塊批次 Read**：塊大小 = Server `OperationLimits.MaxNodesPerRead`（讀不到 → JSON `MaxNodesPerRead` → 500）。分塊**對齊 Device 邊界** — 同一 Device 點位盡量同一塊，保留 Server 端快取效益。
- **品質**：讀取成功且值可轉數值 → Good；StatusCode 非 Good / 非法 NodeId / 連線失敗 → Bad（Value 沿用最近成功值）。Engine 啟動後從未讀成功過 → 失敗不寫歷史（避免灌 0）。
- **變化偵測 + 3 分鐘定期重發**：值/品質變化才走 MQTT / LatestData / 警報 / 計算；超過 3 分鐘未發布則強制重發（Web 端 STALE 門檻 5 分鐘）。HistoryData 不受變化偵測影響，每輪全點位餵入（分鐘 dedup）。
- **熱重載競態防護**：`ReloadAsync` 以 `SemaphoreSlim(1,1)` 序列化，先 Cancel 舊 loops（loop 自行關 Session）再載新設定。
- **授權**：`LicenseState.IsValid` 為 false 時跳過 polling（與 Modbus / DB 來源一致）。

## Web 管理頁

路由 `/OpcUaCoordinator`（導覽：系統設定 → 點位設定 → OPC UA 來源）。權限走 `PermissionService.ConfigurablePages`，可對非 Admin 帳號單獨授權。i18n zh-TW / en 完整。

| 動作 | Endpoint | 說明 |
|---|---|---|
| 顯示 | `GET /OpcUaCoordinator` | 從 DB 讀（快照）；API 不回傳明文密碼，僅 `hasPassword` 旗標 |
| 存 Server | `POST /OpcUaCoordinator/SaveServer` | 新增/編輯連線設定；密碼留空 = 不變更 |
| 刪 Server | `POST /OpcUaCoordinator/DeleteServer` | 刪 JSON 檔 + DB 列；LatestData/HistoryData 舊資料保留 |
| 存點位 | `POST /OpcUaCoordinator/SavePoints` | 全量覆寫 Devices+Tags；新點位配 Seq；驗證 NodeId / ControlType / Ratio / Min<Max |
| 測試讀取 | `POST /OpcUaCoordinator/TestRead` | 以表單目前的 Endpoint/帳密連線讀一次 NodeId，回原始值+工程值（不落地） |

每次存檔/刪除後自動：發 MQTT reload（Engine 熱套用）+ 同步 Web 快取（`SyncOpcUaPointCacheAsync` — 新增點位立即出現在即時數據頁、刪除點位立即剔除）。

OPC UA 點位在以下頁面與 Modbus / DB 來源同等待遇：即時數據（左側 OPC UA 分組，依 Coordinator 展開）、警報設定、計算點位、歷史趨勢、條件控制、畫面設計點位選擇器（群組名 `{Server}/{Device}`）。

## 控制寫回

```
ScadaPage 控制元件（AO 設值 / DO ON-OFF） → SCADA/Control/{SID}
    ↓ MqttControlSubscribeService：CID 前綴分流（DB→DBLatestData / OPC→OPC UA Write / 數字→Modbus）
OpcUaCommunicationService.WriteNodeAsync(sid, value)
    ├─ ControlType 空（唯讀）→ 拒絕 + log
    ├─ AO：寫入原始值 = 輸入值 ÷ Ratio，並依 Server 端現值型別轉換（Int16/Float/Double/...，避免 Bad_TypeMismatch）
    └─ DO：寫入 bool（value > 0.5）
```

使用採集共用的既有 Session；Server 未連線時寫入失敗（log warning）。

## 資料庫表

| 表 | 用途 |
|---|---|
| `OpcUaCoordinator` | Server 註冊表：Id(PK,Identity)、Name(UNIQUE)、EndpointUrl、Username、Password、PollingInterval、ConnectTimeout、MonitorEnabled |
| `OpcUaPoints` | 點位快照：SID(PK)、CoordinatorId、DeviceName、Sequence、Name、TagName、ControlType、Ratio、Unit、Min、Max |

即時/歷史值沿用 `LatestData` / `HistoryData`（與 Modbus 同 pipeline，不另建表）。兩表由 `DatabaseSchema.json` + `DatabaseInitializationService` 於 Engine/Web 啟動時自動建立。

## 已知限制（Phase 2 候選）

- **SecurityPolicy 僅 None**：帳密走 OPC UA UserIdentity，但傳輸未加密。憑證信任鏈 / SignAndEncrypt 列 Phase 2。
- **帳密明文存 JSON/DB**：與 `dbSetting.json` 慣例一致；Web 頁密碼欄遮罩、API 不回明文。
- **週期 Read，非 Subscription**：與既有 pipeline 同構、行為可預期；MonitoredItem/Subscription 列 Phase 2 優化。
- **NodeId 手填**：搭配「測試讀取」驗證；Server 位址空間 Browse 選點列 Phase 2。
- **同機部署前提**：Web 回寫 JSON 走 `EngineOpcUaConfig:WatchedFolder` 指定的本機路徑；分機部署需改走 DB 或 API。
- **非數值型別**（字串/陣列）點位：讀取 Quality=Bad（pipeline 僅支援數值）；測試讀取會顯示原始值提示。
- MQTT retained 訊息：刪除點位後 broker 上舊 retained 訊息仍在，Web 重啟後可能短暫重現快取（與 Modbus 既有行為相同；Engine+broker 全重啟可清）。
