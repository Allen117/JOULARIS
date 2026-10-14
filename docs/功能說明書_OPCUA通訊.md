# 功能說明書 — OPC UA 通訊

Engine 第三種資料來源：**OPC UA Client**。向外部 OPC UA Server 週期讀取點位，匯入既有 History / Latest / MQTT / 警報 / 計算點位 pipeline；可控點位（AO/DO）支援從 ScadaPage 寫回。與 Modbus / DB 來源最大差異：**所有欄位可在 Web 動態編輯，免重啟 Engine**；另可用 Excel 範本／匯出／匯入批次維護（與 Modbus、DB 來源同一套工具列）。

實作計畫與決策記錄：`docs/plans/2026-07-04-opcua-protocol.md`、`2026-10-12-source-excel-sync-opcua.md`（歸檔後見 `_archive/`）。

---

## 目錄

- [整體架構](#整體架構)
- [設定檔（OpcUaPoint/*.json）](#設定檔opcuapointjson)
- [SID 與 Seq 規則](#sid-與-seq-規則)
- [Engine 端行為](#engine-端行為)
- [Web 管理頁](#web-管理頁)
- [Excel 範本／匯出／匯入](#excel-範本匯出匯入)
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

  正式環境用相對路徑、不寫死安裝根目錄（26.6.0 起）；`appsettings.Development.json` 覆寫為 `../ScadaEngine.Engine/OpcUaPoint`（dev Engine `dotnet run` 讀 repo 這份），不碰生產 Engine 資料夾。`MirrorFolder`（可選）仍支援但兩者皆未設。

  相對路徑以 Web ContentRoot 為基準；`WatchedFolder` 未設定時存檔直接報錯（不亂寫）。鏡像寫回/刪檔失敗僅記 log，不影響主寫入。
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
| `SourceWorkbook` / `WebEditedAt` | 選填。Excel 匯入寫入上傳檔名（「依檔案匯出」篩選用）；網頁 SaveServer / SavePoints 寫入修改時間（ISO 8601），匯入預覽據此警告「拿舊 Excel 會蓋掉網頁修改」，匯入後清除。Engine loader 忽略；Web DTO 以 `JsonExtensionData` 保留其他未知頂層欄位 |
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
| Excel 範本 | `GET /OpcUaCoordinator/ImportTemplate` | 下載空白 `OPCUA範本.xlsx` |
| Excel 匯出 | `GET /OpcUaCoordinator/ExportExcel?workbook=` | 匯出現行設定**下載到瀏覽器**（全部或依來源 Excel 檔）；**不含密碼**；活頁簿自訂屬性帶 `ScadaExportedAt` |
| Excel 匯出到資料夾 | `POST /OpcUaCoordinator/ExportToFolder?workbook=&force=` | 同上但寫進來源資料夾（全部 → `OPCUA設定.xlsx`、依來源 Excel → 原檔名；覆寫留 `.bak`）；Excel 開著 / 需確認覆寫回 409 |
| 資料夾 Excel 清單 | `GET /OpcUaCoordinator/FolderWorkbooks` | 來源資料夾內的 Excel 檔（含是否有未匯入修改） |
| 從資料夾匯入預覽 | `POST /OpcUaCoordinator/ImportPreviewFromFolder` | `{ fileName }`，之後同樣走 ImportCommit |
| Excel 預覽 | `POST /OpcUaCoordinator/ImportPreview` | 解析 + 驗證 + Seq 對帳 + 與既有 JSON 比對 → 預覽與 token（不寫檔） |
| Excel 提交 | `POST /OpcUaCoordinator/ImportCommit` | 寫 JSON → Web 依 JSON UPSERT DB（`SyncDbFromJsonAsync`）→ reload + 同步快取；預覽後檔案被改 → 409 |
| 匯入狀態 | `GET /OpcUaCoordinator/SourceStatus` | 前端輪詢用（DB 已載入名稱 + 資料夾 JSON 名稱） |

每次存檔/刪除後自動：發 MQTT reload（Engine 熱套用）+ 同步 Web 快取（`SyncOpcUaPointCacheAsync` — 新增點位立即出現在即時數據頁、刪除點位立即剔除）。網頁存檔同時在 JSON 頂層記 `WebEditedAt`。

OPC UA 點位在以下頁面與 Modbus / DB 來源同等待遇：即時數據（左側 OPC UA 分組，依 Coordinator 展開）、警報設定、計算點位、歷史趨勢、條件控制、畫面設計點位選擇器（群組名 `{Server}/{Device}`）。

## Excel 範本／匯出／匯入

與 Modbus、DB 來源共用同一套工具列與預覽 Modal（框架、token、提交前指紋重比對、覆寫網頁修改警告見 [功能說明書_Modbus來源管理.md §4.1](功能說明書_Modbus來源管理.md#41-excel-匯入匯出刪除取代-xlsm-巨集)）。**JSON 仍是唯一真相，Excel 只是檢視**：伺服器不保存上傳的 Excel，網頁修改不會回寫使用者電腦裡（下載）的檔案；要拿最新設定請按「匯出 Excel」。「匯出 Excel」主按鈕直接寫進 `OpcUaPoint` 來源資料夾、可「從來源資料夾匯入」，SaveServer / SavePoints / DeleteServer / 匯入成功後自動重產資料夾內受影響的 xlsx（規則見 [Modbus §4.1「來源資料夾內的 Excel」](功能說明書_Modbus來源管理.md#41-excel-匯入匯出刪除取代-xlsm-巨集)；匯出仍不含密碼）。OPC UA 特有規則：

| 項目 | OPC UA |
|------|--------|
| 範本版面 | 沿用舊 `OPCUA通訊檔案產生工具.xlsm`（已移除，舊巨集產的檔可直接上傳）。第 1 列標籤／值成對：`EndpointUrl`、`Username`、`Password`、`ConnectTimeout`、`PollingInterval`、`MonitorEnabled`、`MaxNodesPerRead`（以標籤掃描，找不到退回 B1/D1/F1/H1/J1/L1/N1）；第 2 列標題；第 3 列起 `A~H = Name / TagName(NodeId) / ControlType / Ratio / Unit / Min / Max / Device`，**`I = Seq`（灰底，勿改；新增留空）** |
| 一張工作表 | = 一個 Server = 一個 JSON（工作表名稱 = Server 名稱，限英數／`_`／`-`、1～50 字）；多個 Device 以 `Device` 欄分組，**Device 留空時從 NodeId 的 `s=` 之後、第一個 `.` 之前推得**（`ns=2;s=D1.T` → `D1`），推不出用工作表名稱 |
| 預設值 | ConnectTimeout 空白 → 5000（≥1000）；PollingInterval 空白 → 1000（≥200）；MonitorEnabled 空白 → TRUE；MaxNodesPerRead 空白 → 0；Ratio 空白 → 1（不可為 0）；Min/Max 選填（兩者都填需 Min<Max） |
| 驗證 | EndpointUrl 以 `opc.tcp://` 開頭；TagName 必填且能被 OPC UA SDK 解析；ControlType 留空／AO／DO；Seq 正整數且同表不重複 |
| **Seq 不漂移** | 點位**以 Seq 比對、不以位置比對**（與 Modbus/DB 的位置式不同，因此沒有「SID 位移」警告）。有 Seq 的列必須是既有 JSON 中存在的 Seq（否則報「未知的 Seq」；既有 JSON 不存在的新 Server 照單接受）；沒有 Seq 的列先以 TagName 對回既有點位（舊 xlsm 產出相容、誤清空 Seq 的保險，預覽附提示），對不到才從 `max(NextSeq, 最大 Seq+1)` 起配新號。刪中間的列、調換順序、插入新列，既有點位 Seq 都不變；Excel 少了的既有點位在預覽列為「將移除 N 個點位」（Seq 不回收） |
| 密碼 | 匯出 Password 欄一律留空；匯入時空白 = 保留原密碼，有填才覆寫（與網頁 SaveServer 一致）。預覽差異以 `********` 遮罩顯示 |
| 提交後 | Web 寫 JSON → `OpcUaCoordinatorService.SyncDbFromJsonAsync` 依 JSON UPSERT `OpcUaCoordinator` / `OpcUaPoints` → 發 reload → 同步 Web 點位快取，與網頁存檔效果相同（只發 reload 的話 Web 快取同步可能早於 Engine UPSERT，新點位會暫時不顯示） |
| 刪除 | **不支援由匯入流程刪除**（Excel 少了某個 Server 不會刪它、沒有「刪除候選」區）。OPC UA 的刪除是「刪 JSON + 刪 DB 列」，與 Modbus/DB「移到 `_deleted/`、保留 DB」語意不同，仍走頁面上的「刪除 Server」按鈕 |
| 未知頂層欄位 | 匯入時沿用既有 JSON 的其他頂層欄位（`WebEditedAt` 除外） |

Adapter：`ScadaEngine.Web/Services/SourceExcel/OpcUaExcelAdapter.cs`（`PointKeyField = "Seq"`、`SupportsDeleteCandidates = false`、`Reconcile` 做對帳與配號）；範本：`ScadaEngine.Engine/OpcUaPoint/OPCUA範本.xlsx`；測試：`ScadaEngine.Tests/SourceExcel/OpcUaExcelAdapterTests.cs`。

⚠️ 使用者若在 Excel 把某列的 Seq 改成另一個既有 Seq，兩個點位會互換身分 — 只能靠灰底、標題註記與預覽 diff（該 Seq 的 Name/TagName 會顯示變更）提醒，無法完全防止。

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
