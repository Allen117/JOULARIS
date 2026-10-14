# 來源設定 Excel 一致性：DB 熱編輯 bug、舊 Excel 覆寫防護、OPC UA Excel 匯入匯出

**狀態**: 進行中（實作完成，等使用者驗證） <!-- 進行中 | 已完成 | 廢棄 -->
**建立**: 2026-10-12
**最後更新**: 2026-10-13
**相關 commit**: <!-- 完成後回填 commit hash -->

---

## 目標 & 背景

使用者回報：Modbus 設定可用 Excel 匯入產生 JSON，但在網頁上改點位時，只改到 JSON，Excel 沒跟著改；OPC UA 也要檢查。

**第一性原理拆解**

- 伺服器**沒有存** Excel。上傳後只在記憶體解析成 JSON（`ModbusCoordinatorController.cs:206`），JSON 頂層僅留檔名 `SourceWorkbook`。所謂「Excel 沒更新」，指的是使用者電腦裡那份副本，伺服器無從修改。
- 若改成同時維護兩份真相（JSON + 伺服器存的 Excel），兩份必然分歧。**JSON 仍是唯一真相**（沿用 2026-10-06 plan 決策 1），Excel 是隨時可重新產生的檢視；「匯出現行設定」本來就現讀 JSON，網頁的修改匯出時都會帶上。
- 因此真正要解的不是「同步 Excel」，而是以下三個問題：

| 代號 | 問題 | 現況 |
|---|---|---|
| A | DB 來源熱編輯 Name/Unit 後，JSON 的 `SourceWorkbook` 被弄丟 | `DbPointJsonFile` DTO 沒有此欄位，反序列化再序列化時遺失（`DbPointConfigFileService.cs`）。之後「依檔案匯出」篩不到它，同檔重匯時也不會列為刪除候選。Modbus 用 JsonNode 讀寫，沒有這個問題 |
| B | 拿網頁修改前的舊 Excel 重新匯入，會默默蓋掉網頁上的修改 | 預覽只標「覆寫」，沒說明蓋掉的是網頁上的修改 |
| C | OPC UA 沒有網頁版 Excel | 網頁可完整新增、修改、刪除，但沒有範本、匯出、匯入；只剩離線的 `OPCUA通訊檔案產生工具.xlsm` 巨集 |

## 驗收條件

### A：DB 熱編輯保留未知欄位
- [x] DB 來源在網頁改 Name/Unit 後，JSON 的 `SourceWorkbook`（以及任何非 DTO 欄位）原樣保留
- [x] 改完後「依檔案匯出」的下拉仍能篩到該 Coordinator

### B：舊 Excel 覆寫網頁修改的防護
- [x] Modbus / DB / OPC UA 三種來源的網頁熱編輯，會在 JSON 頂層記錄 `WebEditedAt`（ISO 8601 本地時間）
- [x] 匯出的 Excel 帶有匯出時間（活頁簿自訂屬性 `ScadaExportedAt`）
- [x] 匯入預覽中，若某張工作表屬於「覆寫」且有實際差異，並且 JSON 的 `WebEditedAt` 晚於 Excel 的匯出時間（或 Excel 沒有匯出時間，例如範本或手工建立的檔案），該列顯示警告：「此設定於 {時間} 在網頁修改過，匯入會覆寫，建議先匯出最新版再編輯」
- [x] 有這種警告時，提交前需另外勾選確認（比照 SID 位移的確認方式）
- [x] 匯入寫入的 JSON 不帶 `WebEditedAt`，代表 Excel 與 JSON 已重新一致
- [x] 先匯出最新版、在 Excel 修改、再匯入：不出現警告

### C：OPC UA Excel 範本、匯出、匯入
- [x] OPC UA 來源頁出現與 Modbus、DB 相同的 Excel 工具列：下載範本、匯出現行設定、依檔案匯出、匯入（含預覽）
- [x] 一張工作表 = 一個 Server = 一個 JSON；多個 Device 以「Device」欄分組
- [x] **SID 不漂移**：匯出 → 原樣匯入，所有點位的 Seq 不變；刪中間的列、調換順序、插入新列，既有點位的 Seq 都不變；新列配 `NextSeq` 起算的新號
- [x] 舊 `.xlsm` 產出的檔案（沒有 Seq 欄）匯入既有 Server 時，以 NodeId（TagName）對回既有 Seq，不會整批換號
- [x] 匯出不含明文密碼；匯入時密碼欄空白 = 保留原密碼
- [x] 提交後 DB（`OpcUaCoordinator` / `OpcUaPoints`）同步 UPSERT、發 MQTT reload、同步 Web 端點位快取，與網頁存檔的效果相同
- [x] 匯入預覽列出「將移除的點位」（Excel 少了的既有 Seq）

### 共通
- [x] 中英文 i18n 同步
- [x] `dotnet test` 全綠

## 檔案異動清單

### 新增

- [x] `ScadaEngine.Web/Services/SourceExcel/OpcUaExcelAdapter.cs` — OPC UA 版面解析、驗證、JSON 序列化、範本版面
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceJsonStamp.cs` — `WebEditedAt` 讀寫小工具（JsonNode 上設定、清除）
- [x] `ScadaEngine.Tests/SourceExcel/OpcUaExcelAdapterTests.cs`
- [x] `ScadaEngine.Tests/SourceExcel/WebEditedWarningTests.cs`
- [x] `ScadaEngine.Tests/SourceExcel/DbPointHotEditPreserveTests.cs`（或併入既有測試檔）
- [x] `ScadaEngine.Engine/OpcUaPoint/OPCUA範本.xlsx` — 靜態範本，比照 Modbus、DB 隨 Release 出貨

### 修改

**A**
- [x] `ScadaEngine.Web/Services/DbPointConfigFileService.cs` — 改用 JsonNode 原地修改 `Points[i].Name/Unit`，比照 `ModbusConfigFileService`

**B**
- [x] `ScadaEngine.Web/Services/ModbusConfigFileService.cs` — 熱編輯寫入 `WebEditedAt`
- [x] `ScadaEngine.Web/Services/DbPointConfigFileService.cs` — 熱編輯寫入 `WebEditedAt`
- [x] `ScadaEngine.Web/Services/OpcUaCoordinatorService.cs` — SaveServer / SavePoints 寫入 `WebEditedAt`（`OpcUaJsonFile` 加欄位）
- [x] `ScadaEngine.Web/Features/Shared/Models/SourceExcelModels.cs` — `ParsedSourceSheet` 加 `WebEditedAt`
- [x] `ScadaEngine.Web/Features/Shared/Models/SourceExcelPreviewDtos.cs` — 預覽列加 `WebEditedAt`、`HasWebEditConflict`；確認請求加 `AcknowledgeWebEdit`
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceExcelTemplateWriter.cs` — 匯出時寫入活頁簿自訂屬性 `ScadaExportedAt`
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceExcelImportCoordinator.cs` — 預覽時讀取 `ScadaExportedAt`、比對；提交時檢查確認勾選
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceExcelDiffBuilder.cs` — 計算 `HasWebEditConflict`
- [x] `ScadaEngine.Web/Services/SourceExcel/ModbusExcelAdapter.cs`、`DbPointExcelAdapter.cs` — `ParseJson` 讀出 `WebEditedAt`
- [x] `ScadaEngine.Web/wwwroot/js/common/source-excel-import.js` — 警告標示 + 確認勾選
- [x] `ScadaEngine.Web/Features/Shared/Views/_SourceExcelImport.cshtml` — 如需加確認區塊

**C**
- [x] `ScadaEngine.Web/Features/Shared/Models/SourceExcelModels.cs` — `SourceExcelKind` 加 `OpcUa`
- [x] `ScadaEngine.Web/Services/SourceExcel/ISourceExcelAdapter.cs` — 加 `PointKeyField`（以 Seq 比對時使用）與可取得既有 JSON 的 `ToJson` 多載（見決策 4）
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceConfigFileIo.cs` — 加 `OPCUA_SECTION = "EngineOpcUaConfig"`
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceExcelDiffBuilder.cs` — 有 `PointKeyField` 時改以 key 比對，不做位置式 SID 位移偵測，改列出新增、移除、修改的點
- [x] `ScadaEngine.Web/Services/SourceExcel/SourceExcelImportCoordinator.cs` — 套用 `ToJson` 多載；OPC UA 不產生刪除候選（決策 6）
- [x] `ScadaEngine.Web/Services/OpcUaCoordinatorService.cs` — 新增 `SyncDbFromJsonAsync(name)`：依 JSON UPSERT Coordinator + Points；`_fileGate` 改為與匯入共用的鎖或以檔案指紋防撞（決策 7）
- [x] `ScadaEngine.Web/Features/OpcUaCoordinator/Controllers/OpcUaCoordinatorController.cs` — 加 `ImportTemplate` / `ExportExcel` / `ImportPreview` / `ImportCommit` / `SourceStatus`；提交後 `SyncDbFromJsonAsync` + `NotifyEngineAsync`
- [x] `ScadaEngine.Web/Features/OpcUaCoordinator/Views/Index.cshtml` — 引入 `_SourceExcelImport` partial
- [x] `ScadaEngine.Web/wwwroot/js/opcuacoordinator.js` — 匯入完成後重新整理列表
- [x] `ScadaEngine.Web/Program.cs` — 註冊 `OpcUaExcelAdapter`
- [x] `ScadaEngine.Web/Resources/*.resx` + `*.en.resx` — 新字串（共用 srcxl.* 與 opcuacoord.*）
- [x] `docs/i18n-glossary.md` — 如有新術語
- [x] `BuildRelease.ps1` / Release 清單 — 加 `OPCUA範本.xlsx`；`.xlsm` 處置見待釐清

### 資料庫

- 無 schema 異動。`WebEditedAt` 只存在 JSON，Engine 各 loader 會忽略未知欄位（`SourceWorkbook`、`NextSeq` 已是先例），實作時逐一確認三個 loader

## 關鍵設計決策

### 決策 1：不在伺服器存 Excel，JSON 仍是唯一真相
- **選擇**: Excel 是檢視；「匯出現行設定」就是取得最新 Excel 的方式。
- **理由**: 兩份真相必然分歧；網頁熱編輯要同步改 xlsx（儲存格格式、下拉驗證、使用者自加的欄位）成本高又脆弱。
- **放棄的選項**: 伺服器保存上傳的 xlsx，熱編輯時一併改寫 → 雙寫、格式保真、多人下載版本混亂。

### 決策 2：用「網頁修改時間 vs Excel 匯出時間」判斷衝突，不逐欄記修改歷史
- **選擇**: 熱編輯在 JSON 記 `WebEditedAt`；匯出的 Excel 記 `ScadaExportedAt`。匯入時若 `WebEditedAt > ScadaExportedAt`（或 Excel 沒有匯出時間），且該工作表有實際差異 → 警告並要求確認。
- **理由**: 能精準區分「拿最新匯出檔修改」（不警告）與「拿舊檔重匯」（警告），不會一律跳警告讓使用者麻木。只需記一個時間戳記，不需修改歷史表。
- **放棄的選項**:
  - 只要 JSON 曾被網頁修改就警告 → 先匯出再修改也被警告，誤報太多。
  - 逐欄記錄哪些欄位被網頁改過 → 複雜，價值有限；diff 已列出會變動的欄位。
- **細節**: 時間戳記存在活頁簿的自訂屬性，使用者在 Excel 內修改、另存新檔都會保留；若使用者複製工作表到另一個新檔，就沒有匯出時間 → 視為「不明」並警告（寧可多問）。

### 決策 3：OPC UA 版面沿用舊 xlsm 的 A–H，在尾端加 Seq 欄
- **選擇**:
  - 第 1 列：連線設定，標籤與數值成對（`EndpointUrl | 值 | Username | 值 | Password | 值 | ConnectTimeout | 值 | PollingInterval | 值 | MonitorEnabled | 值 | MaxNodesPerRead | 值`），以標籤搜尋定位，固定儲存格為備援（比照 Modbus）
  - 第 2 列：欄標題
  - 第 3 列起：A Name、B TagName（NodeId）、C ControlType（空白、AO、DO，下拉）、D Ratio、E Unit、F Min、G Max、H Device、**I Seq**（標題寫「Seq（勿改；新增留空）」，灰底）
- **理由**: 舊 xlsm 的檔案可直接上傳（比照 Modbus、DB 的相容做法）；Seq 放尾端不干擾一般編輯。
- **放棄的選項**: Seq 放 A 欄 → 和舊檔欄位錯位，舊檔無法直接匯入。
- **Device 空白時**：沿用舊轉換器規則，從 NodeId 的 `s=` 之後、第一個 `.` 之前推得；推不出則用工作表名稱。

### 決策 4：OPC UA 點位以 Seq 比對，不以位置比對
- **選擇**:
  - 有 Seq 的列 → 必須是既有 JSON 中存在的 Seq，否則報錯（「未知的 Seq，新增點位請留空」）；重複的 Seq 報錯。
  - 沒有 Seq 的列 → 先以 TagName（NodeId）對回既有點位（舊 xlsm 相容、使用者誤清空 Seq 時的保險）；對不到 → 新點位，從 `max(NextSeq, 既有最大 Seq + 1)` 起配號。
  - 既有點位在 Excel 中找不到 → 預覽列為「將移除的點位」。
  - 配號需讀既有 JSON，因此 `ISourceExcelAdapter` 加 `ToJson(sheet, workbook, existingJson)` 多載（預設實作轉呼叫舊版，Modbus、DB 不受影響）；adapter 仍保持純函數、無 I/O。
  - `ISourceExcelAdapter` 加 `PointKeyField`（Modbus、DB 回傳 null 維持位置比對；OPC UA 回傳 "Seq"），DiffBuilder 依此切換比對方式。
- **理由**: OPC UA 的 SID = `OPC{Id}-S{Seq}`，Seq 與陣列位置無關；網頁存檔已採「NextSeq 單調遞增、刪除不回收」，Excel 匯入必須一致，否則 HistoryData、警報、計算點位的引用會錯位。
- **放棄的選項**: 舊 Python 轉換器的「每次 1..N 重新編號」→ 刪中間一點就整批 SID 漂移。

### 決策 5：匯出不含密碼，匯入空白 = 保留原密碼
- **選擇**: 匯出時 Password 欄留空並加註「空白 = 保留原密碼」；匯入時填了值才覆寫。
- **理由**: Excel 會被寄送、複製、存在個人電腦；明文密碼外流風險高。網頁 SaveServer 已是「空字串 = 保留原密碼」，行為一致。
- **放棄的選項**: 匯出明文密碼 → 完整 round-trip，但有外洩風險。（待使用者確認，見待釐清）

### 決策 6：OPC UA 匯入不產生「刪除候選」，刪除 Server 仍走頁面上的刪除按鈕
- **選擇**: 匯入只做新增、覆寫；Excel 少了某個 Server 不會刪除它。
- **理由**: 現有網頁的 DeleteServer 是「刪 JSON + 刪 DB 列」，與 Modbus、DB 的「搬到 `_deleted/`、保留 DB」語意不同；把兩種語意混進匯入流程容易出錯。OPC UA 實務上 Server 數量少，逐台刪除即可。
- **放棄的選項**: 比照 Modbus 提供同檔刪除候選 → 需先統一 OPC UA 的刪除語意，超出本次範圍。

### 決策 7：OPC UA 匯入提交後，由 Web 直接 UPSERT DB 再發 reload
- **選擇**: 寫完 JSON 後呼叫 `OpcUaCoordinatorService.SyncDbFromJsonAsync`（依 JSON UPSERT Coordinator + Points），再 `NotifyEngineAsync`（MQTT reload + 點位快取同步）。
- **理由**: 現有網頁 SavePoints 就是「先寫 JSON、自己 UPSERT DB、再通知」；若只發 reload，Web 端快取同步可能早於 Engine UPSERT，造成新點位暫時不顯示。
- **並行**: OPC UA service 的 `_fileGate` 與匯入框架的寫檔各自加鎖；靠匯入提交前既有的 SHA-256 指紋重新比對（檔案在預覽後被改 → 409），即可擋下「預覽後有人在網頁存檔」的情況，不另外合併鎖。

## 實作步驟

1. [x] **A**：`DbPointConfigFileService` 改用 JsonNode 原地修改；補測試（`SourceWorkbook` 與未知欄位保留）
2. [x] **B-1**：`SourceJsonStamp` + 三個熱編輯路徑寫 `WebEditedAt`（OPC UA 的 `OpcUaJsonFile` 加欄位）
3. [x] **B-2**：匯出寫入 `ScadaExportedAt`；預覽讀取、計算 `HasWebEditConflict`；提交時檢查 `AcknowledgeWebEdit`；匯入寫檔不帶 `WebEditedAt`
4. [x] **B-3**：前端預覽 Modal 加警告標示與確認勾選；i18n
5. [x] **B-4**：測試 — 時間比對矩陣（無匯出時間、匯出早於修改、匯出晚於修改、無差異不警告）
6. [x] **C-1**：確認三個 Engine loader 忽略未知欄位；`SourceExcelKind.OpcUa`、`OPCUA_SECTION`、介面加 `PointKeyField` 與 `ToJson` 多載
7. [x] **C-2**：`OpcUaExcelAdapter`（解析、驗證、Seq 比對與配號、範本版面、密碼處理）+ 單元測試（round-trip、刪中間、重排、插入、舊 xlsm 無 Seq、未知 Seq、重複 Seq、NodeId 格式錯誤）
8. [x] **C-3**：DiffBuilder 依 key 比對；Coordinator 套用多載、OPC UA 不產生刪除候選
9. [x] **C-4**：Controller 端點 + `SyncDbFromJsonAsync` + 頁面引入 partial + JS 重新整理；i18n
10. [x] **C-5**：產生靜態 `OPCUA範本.xlsx`，納入 Release 打包
11. [x] 文件同步
12. [x] `dotnet build` + `dotnet test`（820/820 綠）；[ ] 起 dev Web 手動驗證 — 未執行，留給使用者驗證（避免無人值守時停生產 `JoularisWeb`）

## 已知風險 / 待釐清

- ✅ **密碼（決策 5）**：使用者未另行指示，採建議 — 匯出不帶密碼、匯入空白 = 保留。若現場需要在 Excel 集中保管帳密，再議。
- ✅ **舊 `OPCUA通訊檔案產生工具.xlsm`**：已從 repo 與 Release 包移除（`BuildRelease.ps1` OpcUaPoint 改帶 `*.xlsx`），舊巨集產的檔仍可直接上傳。
- ✅ **B 的確認強度**：採「需另外勾選才能提交」（前端 `srcxlAckWebEdit` + 後端 `AcknowledgeWebEdit` 雙重把關）。
- ⚠️ 使用者在 Excel 中修改 Seq 欄（例如手動填號）→ 未知 Seq 會報錯；但若改成另一個既有 Seq，會讓兩個點位互換身分。只能靠欄位灰底、標題註記與預覽 diff（會顯示該 Seq 的 Name/TagName 變了）提醒，無法完全防止。
- ⚠️ 刪除後重建同名 Server（DB 列已被刪）時，NextSeq 從 JSON 重新起算，SID 可能重用；與現有網頁行為相同，不在本次處理範圍。
- ⚠️ Modbus 的「設備顯示名稱」（`UpdateDeviceName`）只寫 DB、不在 JSON 也不在 Excel，與本次無關，不加 `WebEditedAt`。

## 測試策略

- [x] **本次是否動到核心計算 / 關鍵邏輯？**
  - [x] 是 → 補**單元測試**：
    - DB 熱編輯保留 `SourceWorkbook` 與未知欄位
    - 衝突判斷矩陣（`WebEditedAt` × `ScadaExportedAt` × 有無差異）
    - OPC UA Seq 穩定性：round-trip 不變、刪中間、重排、插入、無 Seq 以 NodeId 對回、未知 Seq / 重複 Seq 報錯、NextSeq 單調遞增
    - OPC UA 版面解析：標籤搜尋、Device 推導、ControlType、密碼空白保留
- [ ] 是否動到 DB 查詢 / 寫入或跨模組資料流？ 是 → `SyncDbFromJsonAsync` 以既有 repository 方法實作，靠手動驗證涵蓋（現有測試專案沒有 SQL 整合測試環境）
- [ ] 是否動到關鍵使用者路徑？ 是 → **E2E（Playwright）**：☐ 跳過 / ☐ 完成後補一支（請使用者決定）
- [ ] 手動驗證（瀏覽器操作）：
  - DB：改名 → 依檔案匯出仍篩得到
  - Modbus：改點位 → 拿舊 Excel 匯入看到警告；先匯出再匯入沒有警告
  - OPC UA：匯出 → 刪中間一列、加一列 → 匯入 → SID 不漂移、新點位出現在即時頁
- [x] 收尾：`dotnet test` 全綠

## 文件同步

- [x] `docs/功能說明書_DB來源管理.md` — 熱編輯保留欄位、覆寫警告
- [x] `docs/功能說明書_Modbus來源管理.md` — §4.1 加覆寫警告說明、「Excel 是檢視，請先匯出再編輯」
- [x] `docs/功能說明書_OPCUA通訊.md` — 新增 Excel 章節（版面、Seq 規則、密碼、不支援刪除候選）
- [x] `docs/功能說明書_Release打包與升級.md` — OPC UA 範本與 `.xlsm` 處置
- [x] `docs/架構.md` — 如資料流段落提到 OPC UA 設定來源，補 Excel 匯入
- [x] `CLAUDE.md` Key Configuration Files 的 `OpcUaPoint/*.json` 一列補「亦可 Excel 匯入」

---

## D：匯出寫入伺服器來源資料夾（2026-10-13 驗證期追加，已實作、待使用者驗證）

### 需求與第一性原理
使用者驗證時回報：「匯出 Excel」目前是瀏覽器下載（落在使用者的下載資料夾），期望是直接落在 Engine 讀的來源資料夾（即 appsettings `{Section}:WatchedFolder`；本機現值 Modbus 指向 Engine 部署目錄下的 `Modbus`）。

> **路徑原則**：本段所有資料夾一律由 `SourceConfigFileIo.GetWatchedFolder(section)` 即時解析 appsettings（相對路徑以 Web ContentRoot 為基準、reloadOnChange），**程式與測試不寫死任何絕對路徑**；部署路徑日後調整（另一 session 進行中）只需改 appsettings / Install 佈局，本功能不需改碼。範本排除以 `adapter.TemplateFileName` 檔名判斷，不依路徑。測試一律用暫存資料夾注入設定。

- 生產機 = Web 主機 = 使用者操作的機器，Excel 的編輯與 Engine 的 JSON 在同一台；下載再手動搬進資料夾是多餘的一步。
- 範本 `Modbus範本.xlsx` 本來就隨 Install.bat 鋪在這個資料夾，「資料夾裡有 xlsx」已是常態；Engine watcher `Filter = "*.json"`、Web `ListConfigNames` 只列 `*.json`，xlsx 不會被誤讀。
- **決策 1 不變**：JSON 仍是唯一真相，寫進資料夾的 xlsx 只是「最新一次匯出的快照」（帶 `ScadaExportedAt`），熱編輯不回寫它；拿舊快照重匯仍由 B 的 `WebEditedAt` 警告把關。

### 設計（建議，等裁定）
1. **新端點** `POST {Base}/ExportToFolder?workbook=`（三種來源共用 coordinator，Controller 各加一個 action）：
   - 內容同 `ExportAsync`；寫到 `WatchedFolder`，走 `SourceConfigFileIo.AtomicWrite`（tmp → Replace 留 `.bak`）。
   - **檔名固定**：全部匯出 → `{ExportFileNamePrefix}.xlsx`（例 `Modbus設定.xlsx`）；依檔案匯出 → 原 `SourceWorkbook` 名（例 `天母.xlsx`），這樣「匯入 → 匯出 → 改 → 再匯入」`SourceWorkbook` 不變、依檔案匯出的下拉也對得上。每次覆寫、不堆時間戳記檔（Excel 是可重生的檢視，留 `.bak` 一份即可）。
   - 檔案被 Excel 開著鎖住 → 409，前端顯示「請先關閉 Excel 再匯出」。
   - 回傳實際寫入的完整路徑，前端 toast 顯示伺服器回傳的實際完整路徑（不在前端或 resx 組路徑）。
2. **按鈕**：主按鈕「匯出 Excel」改為寫入資料夾；下拉內保留一項「下載到瀏覽器」（遠端電腦操作 Web 時仍可取得檔案）。
3. **從資料夾匯入**（可選）：匯入下拉列出 `WatchedFolder` 內的 `*.xlsx`（排除範本），選了直接走既有預覽流程，不必再經瀏覽器選檔。`ImportPreview` 加一個以伺服器路徑為來源的多載，解析／diff／提交全部沿用。
4. 三種來源一併生效（`_SourceExcelImport` partial 與 coordinator 共用，成本幾乎為零）；OPC UA 匯出仍不含密碼。

### 使用者裁定（2026-10-13）
- [x] 檔名：**固定名覆寫**（全部匯出 `{Prefix}.xlsx`、依檔案匯出用 `SourceWorkbook` 名）
- [x] 保留「下載到瀏覽器」（放下拉）
- [x] 做「從資料夾匯入」
- [x] 熱編輯後**自動重產** xlsx
  - 熱編輯照舊寫 JSON，成功後重產該 JSON 所屬 xlsx（依 `SourceWorkbook` 對應檔名；檔不存在不主動新建）
  - **未匯入修改保護**：xlsx 檔案修改時間晚於其 `ScadaExportedAt` → 不覆寫，網頁提示
  - xlsx 被 Excel 鎖住 → 略過並提示，JSON 存檔照常成功

### 由裁定衍生的設計補充
- **匯入成功後重產 xlsx**：匯入寫完 JSON 後，以新 JSON 重產資料夾內對應 xlsx（重蓋 `ScadaExportedAt`）。否則剛匯入的檔案修改時間晚於匯出時間，會被誤判為「有未匯入修改」，之後熱編輯就再也不會自動更新它。代價：使用者在 Excel 加的格式、額外欄位會被洗掉（Excel 是檢視，可接受）。瀏覽器上傳的匯入同理，資料夾內同名 xlsx 也一併重產（套用未匯入修改保護）。
- **頁面顯示「Excel 有未匯入修改」提示**：擴充既有 `SourceStatus`，列出資料夾內修改時間晚於 `ScadaExportedAt` 的 xlsx，頁面頂端提示並附「匯入」捷徑。目的是避免使用者以為「在 Excel 存檔就生效」—— Engine 只讀 JSON，Excel 存檔後必須在網頁匯入才生效。
- **不做「Excel 存檔自動匯入」**：自動匯入會跳過預覽與 SID 位移、覆寫網頁修改等確認，風險高於多按一次匯入。

### 驗收條件
- [ ] Modbus / DB / OPC UA 頁按「匯出 Excel」後，`WatchedFolder` 出現對應 xlsx，內容與瀏覽器下載版相同（含 `ScadaExportedAt`）（coordinator 層已測；三頁瀏覽器操作待使用者驗證）
- [x] 檔案被 Excel 開啟時匯出 → 明確錯誤訊息，JSON 與既有 xlsx 不受影響（測試以 FileShare.Read 模擬 Excel 開檔）
- [x] 匯出後 Engine 不觸發 reload（watcher `Filter = "*.json"`；測試確認 ListExisting 只列 JSON）
- [ ] 「下載到瀏覽器」仍可用（端點未改，待瀏覽器確認下拉）
- [ ] 「從資料夾匯入」能列出並預覽資料夾內 xlsx（排除範本），提交結果與上傳相同（coordinator 層已測；UI 待驗證）
- [ ] 網頁熱編輯存檔後，資料夾內對應 xlsx 自動更新；Excel 開著或有未匯入修改時不覆寫並提示，JSON 照常存檔（重產邏輯已測；三頁熱編輯串接待驗證）
- [x] 匯入成功後資料夾內對應 xlsx 重產，之後不會被誤判為「有未匯入修改」
- [ ] 在 Excel 存檔但未匯入 → 頁面顯示未匯入提示（判定已測；工具列提示待驗證）
- [x] i18n zh-TW / en 同步；`dotnet test` 全綠 840/840（新增 11 個 coordinator 資料夾測試）

### 檔案異動（預估）
- `Services/SourceExcel/SourceExcelImportCoordinator.cs` — `ExportToFolderAsync`、（可選）`PreviewFromFolderAsync`
- `Features/{Modbus,Db,OpcUa}Coordinator/Controllers/*.cs` — `ExportToFolder`、（可選）`ImportPreviewFromFolder`、`ListFolderWorkbooks`
- `Features/Shared/Views/_SourceExcelImport.cshtml` + `wwwroot/js/common/source-excel-import.js` — 按鈕改為 POST + toast、下拉項
- `Resources/Features.Shared.Views._SourceExcelImport.{,en}.resx`、`Services.SourceExcelImportCoordinator.{,en}.resx`
- `ScadaEngine.Tests/SourceExcel/SourceExcelImportCoordinatorTests.cs`
- docs：`功能說明書_Modbus來源管理.md` §4.1、`_DB來源管理.md` §8.4、`_OPCUA通訊.md` Excel 章節


## 進度日誌

### 2026-10-12
- 調查完成、plan 建立，等待使用者確認。

### 2026-10-13
- 使用者「執行」→ 動工。三項待釐清未另行指示，採 plan 內建議：匯出不帶密碼、移除舊 `.xlsm`、衝突需勾選確認。
- A / B / C 全部實作完成；新增 3 個測試檔，`dotnet test` 820/820 綠（原 741 + 本次新增與既有測試）。
- 產生 `ScadaEngine.Engine/OpcUaPoint/OPCUA範本.xlsx`（以 TemplateWriter 一次性產出）；`OPCUA通訊檔案產生工具.xlsm` 已 `git rm`；`BuildRelease.ps1` OpcUaPoint 改帶 `*.xlsx`。
- 文件同步完成（OPCUA通訊 / Modbus來源管理 / DB來源管理 / Release打包與升級 / 架構 / CLAUDE.md / i18n-glossary）。
- **未執行**：起 dev Web 的瀏覽器手動驗證（需暫停生產 `JoularisWeb`，無人值守不停），留給使用者驗證。
- 版本提議：新增功能 → `26.5.0 → 26.6.0`，等使用者裁定後再改 `Directory.Build.props`。
- 使用者驗證回報「匯出 Excel 的 ModbusID 怪怪的」：以生產 `C:\SCADA\Engine\App\Modbus\*.json` 重現，資料正確（F1 文字 `1,2,3,4,5,6,7,8`、格式 `@`），問題是 **F 欄寬 10 容不下、右鄰 G1 標籤擋住溢出**，看起來像被截斷。修法：`SourceExcelCells.FitColumnToRow1Value` 在欄寬設定後把第 1 列長值所在欄放寬（Modbus 的 B/F、OPC UA 的 B/D），補測試 `WriteSheet_LongModbusIdList_ColumnWideEnough`。
- 使用者接著指出：匯出目前是瀏覽器下載，期望落在 `C:\SCADA\Engine\App\Modbus`（WatchedFolder）→ 追加 **D 段**，等使用者裁定三個選項後再動工。

### 2026-10-13（D 段實作）
- 使用者「執行」D 段 → 動工。實作完成：coordinator `ExportToFolderAsync` / `ListFolderWorkbooks` / `PreviewFromFolderAsync` / `RefreshFolderWorkbooksAsync`；三個 Controller 各加 `ExportToFolder` / `FolderWorkbooks` / `ImportPreviewFromFolder`，熱編輯（Modbus UpdatePoints、DB UpdatePoint、OPC UA SaveServer/SavePoints/DeleteServer）成功後重產並回 `workbookNotice`；匯入提交、逐台刪除在 coordinator 內重產；partial 工具列 + 共用 JS + 三頁熱編輯 JS 顯示提示。
- `dotnet test` 840/840 綠；Web build 成功。
- 文件同步：Modbus來源管理 §4.1（新增「來源資料夾內的 Excel」小節 + API 表）、DB來源管理（API 表 + §8.4）、OPCUA通訊（API 表 + Excel 章節）、架構.md、Release打包與升級（升級保留 xlsx）。
- **未執行**：起 dev Web 的瀏覽器手動驗證（需暫停生產 `JoularisWeb`），留給使用者。
- D 段與設計的差異：
  - 「未匯入提示」改用獨立端點 `FolderWorkbooks`，**未擴充 `SourceStatus`** — `SourceStatus` 在匯入後每秒輪詢，每次都開 xlsx 讀自訂屬性太浪費；頁面載入與匯出後各呼叫一次即可
  - 修改時間判定：寫檔後 `File.SetLastWriteTimeUtc` = `ScadaExportedAt`，「未匯入」= 修改時間晚於匯出時間 + 2 秒容許
  - 重產「受影響」= `{Prefix}.xlsx` ＋ 檔名對得上某設定 `SourceWorkbook` 的 xlsx；不新建、`.xlsm` 與範本不碰；沒有 `ScadaExportedAt` 的同名檔視為「非系統匯出」不覆寫（提示原因）
  - 從資料夾匯入後重產限「每張工作表都已進 JSON」（匯入或無變更）；只勾部分工作表 → 不重產（否則會洗掉未匯入工作表的修改），檔案仍標「未匯入」
  - 從資料夾匯入後重產的工作表集合 = 該檔原有工作表 ∪ `SourceWorkbook` 相符者（避免 SourceWorkbook 為空的無變更工作表被洗掉）
  - 明確按「匯出」遇到同名檔有未匯入修改或非系統匯出 → 409 `needsConfirm`，確認後帶 `force=true` 覆寫（舊檔留 `.bak`）；依來源 Excel 匯出卻沒有任何設定對得上 → 拒絕，不寫空白檔
- 版本：A/B/C + D 屬新增功能；26.6.0 已被另一 session（install-root-joularis）使用 → 使用者裁定 **`26.7.0`**，已改 `Directory.Build.props`（與本 plan 程式同一個 commit）。

## 討論過程與成本記錄

### 討論過程摘要

- 使用者問題：網頁改點位只改到 JSON、Excel 沒改，OPC UA 也要檢查。
- 調查結果：伺服器沒有存 Excel，匯出功能本來就現讀 JSON；真正的問題是 A（DB 熱編輯遺失 `SourceWorkbook`，已讀碼確認）、B（舊 Excel 重新匯入會蓋掉網頁修改）、C（OPC UA 沒有網頁 Excel）。
- 我提出 A、B、C 三項建議 → 使用者回覆「ABC 寫一份 plan」→ 三項合併為本 plan。
- 驗證期（10-13）：使用者回報匯出 ModbusID 顯示異常（實為欄寬截斷，已修）；並要求匯出直接寫進伺服器來源資料夾 → 追加 D 段，三個待裁定項（檔名規則 / 保留下載 / 從資料夾匯入）。
- 使用者追問「熱編輯不回寫嗎？不是希望都不用人再去改 json？」→ 澄清熱編輯本來就寫 JSON、無人需手改；「不回寫」指資料夾 xlsx 快照。新增第 4 個待裁定項：熱編輯後自動重產 xlsx（含未匯入修改保護）。
- 使用者裁定四項皆採建議（固定名 / 保留下載 / 從資料夾匯入 / 熱編輯自動重產）；追問「看起來只吃 Excel 實際吃 JSON？」「匯入會改 JSON 嗎？」→ 澄清後補「匯入後重產 xlsx」「未匯入修改提示」，明訂不做 Excel 存檔自動匯入。
- 使用者告知另一 session 將調整部署路徑 → 檢查本 plan：設計段原寫死 `C:\SCADA\...` 範例改為「依 appsettings WatchedFolder 解析」並加路徑原則；進度日誌中的路徑屬歷史紀錄保留。已實作的 SourceExcel 程式與測試經掃描無寫死路徑。

### 成本記錄

| 階段 | 模型 | Input | Output | Cache Read | Cache Write | 備註 |
|------|------|-------|--------|------------|-------------|------|
| Plan 撰寫 | claude-opus-5-5 | 16 | 17,862 | 500,990 | 62,978 | 主 session transcript（f3c3d597…，依 message.id 去重 8 則）；Explore 子代理用量另存，未取得 |
| 實作（至等待驗證） | claude-fable-5-1 | 3,170 | 123,260 | 24,710,322 | 327,823 | session f55424d2…，依 message.id 去重 100 則；尚未 commit，commit/push 後另追記 |
| D 段實作（至等待驗證） | claude-opus-5-5 | 90 | 74,683 | 7,163,660 | 196,555 | session 40f98a63…，依 message.id 去重 45 則（統計至寫入本列前）；尚未 commit |

### Archive 總結（搬進 _archive 前回填）

- **總 token**：input __ / output __ / cache read __ / cache write __
- **模型**：
- **API 費用（當下牌價，附計算式）**：牌價查詢日 YYYY-MM-DD，來源 __
  - 計算式：

## 完成後補充

### 實際做法 vs 原計畫差異
- **Seq 對帳放在 `Reconcile` 而非 `ToJson`**：plan 原寫「`ToJson(sheet, workbook, existingJson)` 多載負責配號」，但「未知 Seq」要報錯、預覽要顯示新點位的 Seq，都得在比對**之前**做；故介面多加 `Reconcile(sheet, existing)`（預覽時、DiffBuilder 之前呼叫，純函數），`ToJson` 多載只負責保留原密碼 / NextSeq / 未知頂層欄位。
- **既有 JSON 不存在（新 Server）時 Seq 照單接受**：plan 寫「有 Seq 的列必須是既有 Seq」，但新 Server 沒有既有可比，若一律報錯，從別台匯出再匯入就無路可走；改為新 Server 接受任意正整數 Seq、空白者接在最大號之後。
- **`OpcUaJsonFile` DTO 加 `[JsonExtensionData]`**：OPC UA 的網頁存檔走 DTO 往返，原本會和 DB 一樣弄丟 `SourceWorkbook`（問題 A 的 OPC UA 版）；加 ExtensionData 一併保留所有未知頂層欄位。
- **密碼遮罩走 Header 雙欄**：`Header["Password"]` 存 `********`（列入 HeaderFields 比對 / 顯示）、`Header["PasswordRaw"]` 存實際值（不列入 HeaderFields，只給 ToJson），既有 JSON 解析時 Password 一律空字串 → 空白 = 無差異 = 保留。
- `opcuacoordinator.js` 不需改：匯入完成後的重新整理由共用 `source-excel-import.js` 輪詢 `SourceStatus` 處理；OPC UA 頁只引入該 JS。
- OPC UA Controller 原本沒有 resx，新增 `Features.OpcUaCoordinator.Controllers.OpcUaCoordinatorController.{,en}.resx`（param_error / excel_ext_invalid）。
- 刪除不再使用的 `Features/DbCoordinator/Models/DbPointJsonFileDtos.cs`（DB 熱編輯改 JsonNode 後無人引用）。
- 介面新成員（`PointKeyField` / `SupportsDeleteCandidates` / `Reconcile` / `ToJson` 多載）用 C# default interface implementation，Modbus / DB adapter 零改動。

### 踩到的雷
- 靜態範本 `OPCUA範本.xlsx` 沒有現成產生入口：以一支暫時 xunit 測試（讀環境變數輸出路徑）跑 `dotnet test --filter` 產出後刪掉測試檔。日後改版面要重新產生時可照做（或考慮加一支永久的「範本產生」工具測試）。
- 以 `head -N` 複製 resx 表頭時行數算錯兩次（少 resheader / 多帶一筆 Modbus 鍵），改以 `python xml.dom.minidom` 驗 XML + grep 確認。
- 工作目錄裡有與本 plan 無關的未提交變更（`Modbus.json`、`dbSetting.json`、`docs/標案/*`、Modbus 資料夾的現場 json/xlsm），收尾 commit 時要逐檔 `git add`，不可 `git add -A`。

### 對 memory / CLAUDE.md 的更新建議
- CLAUDE.md `OpcUaPoint/*.json` 一列已補「亦可 Excel 匯入 / WebEditedAt / ScadaExportedAt」。
- 無需新增 memory：規則都已寫進 docs（OPCUA通訊 §Excel、Modbus來源管理 §4.1）。
