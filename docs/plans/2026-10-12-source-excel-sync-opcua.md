# 來源設定 Excel 一致性：DB 熱編輯 bug、舊 Excel 覆寫防護、OPC UA Excel 匯入匯出

**狀態**: 進行中 <!-- 進行中 | 已完成 | 廢棄 -->
**建立**: 2026-10-12
**最後更新**: 2026-10-12
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
- [ ] DB 來源在網頁改 Name/Unit 後，JSON 的 `SourceWorkbook`（以及任何非 DTO 欄位）原樣保留
- [ ] 改完後「依檔案匯出」的下拉仍能篩到該 Coordinator

### B：舊 Excel 覆寫網頁修改的防護
- [ ] Modbus / DB / OPC UA 三種來源的網頁熱編輯，會在 JSON 頂層記錄 `WebEditedAt`（ISO 8601 本地時間）
- [ ] 匯出的 Excel 帶有匯出時間（活頁簿自訂屬性 `ScadaExportedAt`）
- [ ] 匯入預覽中，若某張工作表屬於「覆寫」且有實際差異，並且 JSON 的 `WebEditedAt` 晚於 Excel 的匯出時間（或 Excel 沒有匯出時間，例如範本或手工建立的檔案），該列顯示警告：「此設定於 {時間} 在網頁修改過，匯入會覆寫，建議先匯出最新版再編輯」
- [ ] 有這種警告時，提交前需另外勾選確認（比照 SID 位移的確認方式）
- [ ] 匯入寫入的 JSON 不帶 `WebEditedAt`，代表 Excel 與 JSON 已重新一致
- [ ] 先匯出最新版、在 Excel 修改、再匯入：不出現警告

### C：OPC UA Excel 範本、匯出、匯入
- [ ] OPC UA 來源頁出現與 Modbus、DB 相同的 Excel 工具列：下載範本、匯出現行設定、依檔案匯出、匯入（含預覽）
- [ ] 一張工作表 = 一個 Server = 一個 JSON；多個 Device 以「Device」欄分組
- [ ] **SID 不漂移**：匯出 → 原樣匯入，所有點位的 Seq 不變；刪中間的列、調換順序、插入新列，既有點位的 Seq 都不變；新列配 `NextSeq` 起算的新號
- [ ] 舊 `.xlsm` 產出的檔案（沒有 Seq 欄）匯入既有 Server 時，以 NodeId（TagName）對回既有 Seq，不會整批換號
- [ ] 匯出不含明文密碼；匯入時密碼欄空白 = 保留原密碼
- [ ] 提交後 DB（`OpcUaCoordinator` / `OpcUaPoints`）同步 UPSERT、發 MQTT reload、同步 Web 端點位快取，與網頁存檔的效果相同
- [ ] 匯入預覽列出「將移除的點位」（Excel 少了的既有 Seq）

### 共通
- [ ] 中英文 i18n 同步
- [ ] `dotnet test` 全綠

## 檔案異動清單

### 新增

- [ ] `ScadaEngine.Web/Services/SourceExcel/OpcUaExcelAdapter.cs` — OPC UA 版面解析、驗證、JSON 序列化、範本版面
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceJsonStamp.cs` — `WebEditedAt` 讀寫小工具（JsonNode 上設定、清除）
- [ ] `ScadaEngine.Tests/SourceExcel/OpcUaExcelAdapterTests.cs`
- [ ] `ScadaEngine.Tests/SourceExcel/WebEditedWarningTests.cs`
- [ ] `ScadaEngine.Tests/SourceExcel/DbPointHotEditPreserveTests.cs`（或併入既有測試檔）
- [ ] `ScadaEngine.Engine/OpcUaPoint/OPCUA範本.xlsx` — 靜態範本，比照 Modbus、DB 隨 Release 出貨

### 修改

**A**
- [ ] `ScadaEngine.Web/Services/DbPointConfigFileService.cs` — 改用 JsonNode 原地修改 `Points[i].Name/Unit`，比照 `ModbusConfigFileService`

**B**
- [ ] `ScadaEngine.Web/Services/ModbusConfigFileService.cs` — 熱編輯寫入 `WebEditedAt`
- [ ] `ScadaEngine.Web/Services/DbPointConfigFileService.cs` — 熱編輯寫入 `WebEditedAt`
- [ ] `ScadaEngine.Web/Services/OpcUaCoordinatorService.cs` — SaveServer / SavePoints 寫入 `WebEditedAt`（`OpcUaJsonFile` 加欄位）
- [ ] `ScadaEngine.Web/Features/Shared/Models/SourceExcelModels.cs` — `ParsedSourceSheet` 加 `WebEditedAt`
- [ ] `ScadaEngine.Web/Features/Shared/Models/SourceExcelPreviewDtos.cs` — 預覽列加 `WebEditedAt`、`HasWebEditConflict`；確認請求加 `AcknowledgeWebEdit`
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceExcelTemplateWriter.cs` — 匯出時寫入活頁簿自訂屬性 `ScadaExportedAt`
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceExcelImportCoordinator.cs` — 預覽時讀取 `ScadaExportedAt`、比對；提交時檢查確認勾選
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceExcelDiffBuilder.cs` — 計算 `HasWebEditConflict`
- [ ] `ScadaEngine.Web/Services/SourceExcel/ModbusExcelAdapter.cs`、`DbPointExcelAdapter.cs` — `ParseJson` 讀出 `WebEditedAt`
- [ ] `ScadaEngine.Web/wwwroot/js/common/source-excel-import.js` — 警告標示 + 確認勾選
- [ ] `ScadaEngine.Web/Features/Shared/Views/_SourceExcelImport.cshtml` — 如需加確認區塊

**C**
- [ ] `ScadaEngine.Web/Features/Shared/Models/SourceExcelModels.cs` — `SourceExcelKind` 加 `OpcUa`
- [ ] `ScadaEngine.Web/Services/SourceExcel/ISourceExcelAdapter.cs` — 加 `PointKeyField`（以 Seq 比對時使用）與可取得既有 JSON 的 `ToJson` 多載（見決策 4）
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceConfigFileIo.cs` — 加 `OPCUA_SECTION = "EngineOpcUaConfig"`
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceExcelDiffBuilder.cs` — 有 `PointKeyField` 時改以 key 比對，不做位置式 SID 位移偵測，改列出新增、移除、修改的點
- [ ] `ScadaEngine.Web/Services/SourceExcel/SourceExcelImportCoordinator.cs` — 套用 `ToJson` 多載；OPC UA 不產生刪除候選（決策 6）
- [ ] `ScadaEngine.Web/Services/OpcUaCoordinatorService.cs` — 新增 `SyncDbFromJsonAsync(name)`：依 JSON UPSERT Coordinator + Points；`_fileGate` 改為與匯入共用的鎖或以檔案指紋防撞（決策 7）
- [ ] `ScadaEngine.Web/Features/OpcUaCoordinator/Controllers/OpcUaCoordinatorController.cs` — 加 `ImportTemplate` / `ExportExcel` / `ImportPreview` / `ImportCommit` / `SourceStatus`；提交後 `SyncDbFromJsonAsync` + `NotifyEngineAsync`
- [ ] `ScadaEngine.Web/Features/OpcUaCoordinator/Views/Index.cshtml` — 引入 `_SourceExcelImport` partial
- [ ] `ScadaEngine.Web/wwwroot/js/opcuacoordinator.js` — 匯入完成後重新整理列表
- [ ] `ScadaEngine.Web/Program.cs` — 註冊 `OpcUaExcelAdapter`
- [ ] `ScadaEngine.Web/Resources/*.resx` + `*.en.resx` — 新字串（共用 srcxl.* 與 opcuacoord.*）
- [ ] `docs/i18n-glossary.md` — 如有新術語
- [ ] `BuildRelease.ps1` / Release 清單 — 加 `OPCUA範本.xlsx`；`.xlsm` 處置見待釐清

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

1. [ ] **A**：`DbPointConfigFileService` 改用 JsonNode 原地修改；補測試（`SourceWorkbook` 與未知欄位保留）
2. [ ] **B-1**：`SourceJsonStamp` + 三個熱編輯路徑寫 `WebEditedAt`（OPC UA 的 `OpcUaJsonFile` 加欄位）
3. [ ] **B-2**：匯出寫入 `ScadaExportedAt`；預覽讀取、計算 `HasWebEditConflict`；提交時檢查 `AcknowledgeWebEdit`；匯入寫檔不帶 `WebEditedAt`
4. [ ] **B-3**：前端預覽 Modal 加警告標示與確認勾選；i18n
5. [ ] **B-4**：測試 — 時間比對矩陣（無匯出時間、匯出早於修改、匯出晚於修改、無差異不警告）
6. [ ] **C-1**：確認三個 Engine loader 忽略未知欄位；`SourceExcelKind.OpcUa`、`OPCUA_SECTION`、介面加 `PointKeyField` 與 `ToJson` 多載
7. [ ] **C-2**：`OpcUaExcelAdapter`（解析、驗證、Seq 比對與配號、範本版面、密碼處理）+ 單元測試（round-trip、刪中間、重排、插入、舊 xlsm 無 Seq、未知 Seq、重複 Seq、NodeId 格式錯誤）
8. [ ] **C-3**：DiffBuilder 依 key 比對；Coordinator 套用多載、OPC UA 不產生刪除候選
9. [ ] **C-4**：Controller 端點 + `SyncDbFromJsonAsync` + 頁面引入 partial + JS 重新整理；i18n
10. [ ] **C-5**：產生靜態 `OPCUA範本.xlsx`，納入 Release 打包
11. [ ] 文件同步
12. [ ] `dotnet build` + `dotnet test`；起 dev Web 手動驗證（需暫停 `JoularisWeb`，驗完當輪復原）

## 已知風險 / 待釐清

- ❓ **密碼（決策 5）**：匯出不帶密碼可以嗎？還是現場習慣在 Excel 裡集中保管連線帳密？
- ❓ **舊 `OPCUA通訊檔案產生工具.xlsm`**：網頁版上線後，是從 Release 包與 repo 移除（比照 Modbus、DB 已移除舊巨集），還是保留一陣子？建議**移除**，舊檔仍可直接上傳。
- ❓ **B 的確認強度**：有衝突時「需另外勾選才能提交」，還是「只顯示警告、不擋」？建議**需勾選**，因為覆寫網頁修改是無聲的資料遺失。
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
- [ ] 收尾：`dotnet test` 全綠

## 文件同步

- [ ] `docs/功能說明書_DB來源管理.md` — 熱編輯保留欄位、覆寫警告
- [ ] `docs/功能說明書_Modbus來源管理.md` — §4.1 加覆寫警告說明、「Excel 是檢視，請先匯出再編輯」
- [ ] `docs/功能說明書_OPCUA通訊.md` — 新增 Excel 章節（版面、Seq 規則、密碼、不支援刪除候選）
- [ ] `docs/功能說明書_Release打包與升級.md` — OPC UA 範本與 `.xlsm` 處置
- [ ] `docs/架構.md` — 如資料流段落提到 OPC UA 設定來源，補 Excel 匯入
- [ ] `CLAUDE.md` Key Configuration Files 的 `OpcUaPoint/*.json` 一列補「亦可 Excel 匯入」

---

## 進度日誌

### 2026-10-12
- 調查完成、plan 建立，等待使用者確認。

## 討論過程與成本記錄

### 討論過程摘要

- 使用者問題：網頁改點位只改到 JSON、Excel 沒改，OPC UA 也要檢查。
- 調查結果：伺服器沒有存 Excel，匯出功能本來就現讀 JSON；真正的問題是 A（DB 熱編輯遺失 `SourceWorkbook`，已讀碼確認）、B（舊 Excel 重新匯入會蓋掉網頁修改）、C（OPC UA 沒有網頁 Excel）。
- 我提出 A、B、C 三項建議 → 使用者回覆「ABC 寫一份 plan」→ 三項合併為本 plan。

### 成本記錄

| 階段 | 模型 | Input | Output | Cache Read | Cache Write | 備註 |
|------|------|-------|--------|------------|-------------|------|
| Plan 撰寫 | claude-opus-5-5 | 16 | 17,862 | 500,990 | 62,978 | 主 session transcript（f3c3d597…，依 message.id 去重 8 則）；Explore 子代理用量另存，未取得 |

### Archive 總結（搬進 _archive 前回填）

- **總 token**：input __ / output __ / cache read __ / cache write __
- **模型**：
- **API 費用（當下牌價，附計算式）**：牌價查詢日 YYYY-MM-DD，來源 __
  - 計算式：

## 完成後補充

### 實際做法 vs 原計畫差異
-

### 踩到的雷
-

### 對 memory / CLAUDE.md 的更新建議
-
