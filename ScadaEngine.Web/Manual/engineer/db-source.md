# DB 來源

DB 來源用於「資料已經由其他系統寫進 SQL Server」的情境：外部系統（第三方監控、PLC 閘道、排程腳本、手動匯入工具）只要把**已換算好的工程值**寫進統一入口表 `DBLatestData`，Engine 就會定期讀取，讓這些點位跟 Modbus 點位一樣享有歷史趨勢、警報、計算點位、能源報表與即時監控。

## 基本觀念

- **一個 DB 來源（Coordinator）最多 100 個點位**，設定檔存放在 `C:\JOULARIS\Engine\App\DBPoint\{名稱}.json`
- **SID 格式 `DB{編號}-S{序號}`**，例 `DB1-S5`。編號由資料庫依名稱配發，同名重新匯入編號不變；序號 = 點位在清單中的順序（1～100）
- **外部系統寫的就是工程值**：Engine 不做任何倍率換算
- **即時生效**：匯入、刪除、改名後 Web 自動通知 Engine 重新載入（MQTT `SCADA/Sys/DbCoordinator/Reload`），免重啟

> ⚠️ 注意：與 Modbus 相同，**新增點位一律加在最後一列**。中間插入或刪除會使後續 SID 位移，警報規則、計算點位與畫面綁定都會錯位。

## 進入頁面

選單：**工程師模式 → 點位設定 → DB 來源**

- **頁首按鈕**：**下載範本**、**匯出 Excel**、**匯入 Excel**、**重新整理頁面**、**通知 Engine 重新載入 JSON**
- **左側「DB 通訊」清單**：已載入的 DB 來源，徽章意義同 Modbus（**停用** / **設定檔已移除** / **待 Engine 載入**）
- **右側「設備詳細資料」**：**Id**、**Name（設備名稱）**、**PollingInterval（輪詢間隔 ms）**、**ConnectTimeout（連線逾時 ms）**、**MonitorEnabled（啟用監控）**、**點位數**，以及點位列表（**SID** / **名稱** / **單位** / **Min** / **Max**）與 **刪除來源** 按鈕

<!-- IMG-TODO: engineer/db-source-01 | DB 來源：左側來源清單與右側點位列表（含 SID 欄） | route=/DbCoordinator -->

## 建立 DB 來源

### 步驟一：填寫範本

1. 按 **下載範本** 取得 `DB來源範本.xlsx`
2. **一張工作表 = 一個 DB 來源**，工作表名稱即來源名稱
3. 第 1 列設定：**ConnectTimeout**（空白 = 1000）、**PollingInterval**（空白 = 1000 ms，最短 200）、**MonitorEnabled**（空白 = TRUE）
4. 第 2 列起每列一個點位：**Name**（≤100 字）/ **Unit**（≤50 字）/ **Min** / **Max**（空白 = 0 / 100）
5. 超過 100 個點位會列為錯誤，請拆成兩張工作表

### 步驟二：匯入

1. 按 **匯入 Excel** 上傳，檢視 **匯入預覽**（新增 / 覆寫 / 無變更 / 錯誤 / 刪除候選）
2. 按 **確認匯入**；系統寫入設定檔並自動通知 Engine 重載
3. 頁面等待 Engine 寫入資料庫後自動重新整理，新來源出現在左側清單

> 💡 提示：預覽、SID 位移警告、覆寫網頁修改警告、匯出到來源資料夾、從來源資料夾匯入等規則與 Modbus 完全相同，請參考 [Modbus 來源](modbus-source) 的「匯入 Excel」與「匯出 Excel 與來源資料夾」兩節。全部匯出的檔名為 `DB來源設定.xlsx`。

### 步驟三：取得 SID 交給外部系統

匯入完成後，在右側點位列表即可看到每個點位的 **SID**。將 SID 清單交給負責寫入的外部系統。

## 外部系統如何寫入

外部系統只需 `INSERT` / `UPDATE` `DBLatestData` 表，以 SID 為鍵：

| 欄位 | 說明 |
|------|------|
| `SID` | 例 `DB1-S5` |
| `Value` | **工程值**（float） |
| `Timestamp` | 外部系統的資料時間 |
| `Quality` | 只有明確寫 `0` 才視為 Bad；`1`、NULL 或其他值都視為 Good |

```sql
UPDATE DBLatestData
SET Value = 25.3, Timestamp = GETDATE()
WHERE SID = 'DB1-S5';
```

- 只改 `Value` 不碰 `Quality` 也可以
- 外部系統要主動宣告「這筆值不可信」時才寫 `Quality = 0`
- 匯入點位時系統已預先建立每個 SID 的列（Quality = 1），外部系統直接 UPDATE 即可

> 💡 提示：Engine 每次輪詢都會寫一筆歷史（每分鐘一筆、以輪詢當下時間為準），所以外部系統停止更新時，趨勢圖會呈現一條水平線而不是斷線。若需要「停滯偵測」，應由寫入端把 Quality 改為 0。

> ⚠️ 注意：從 26.9.1 以前升級的站台，舊版預先建立的列 Quality 為 0，導致只改 Value 的點位永遠顯示 Bad。升級後請在 SSMS 執行一次（排除氣象來源 `Weather`）：

```sql
UPDATE DBLatestData SET Quality = 1
WHERE Quality = 0
  AND SID NOT IN (SELECT p.SID FROM DBPoints p
                  JOIN DBCoordinator c ON c.Id = p.CoordinatorId
                  WHERE c.Name = 'Weather');
```

## 修改點位名稱與單位

1. 左側選來源，在點位列表**雙擊名稱或單位**（或點鉛筆圖示）
2. 修改後按 Enter 或 ✓ 儲存；Esc 或 ✗ 取消
3. 系統回寫設定檔並自動通知 Engine 重載

只有名稱與單位可在網頁修改；增刪點位、改 Min / Max、改輪詢參數請走「匯出 Excel → 修改 → 匯入」。

> 💡 提示：單位會影響自動帶入。例如電表/迴路設定只列出單位為 `kWh` 的點位，改了單位後就能被選到。

## 刪除來源

按右側 **刪除來源** 並確認。設定檔移到 `DBPoint\_deleted\`，Engine 停止輪詢；資料庫紀錄與歷史資料保留，同名重新匯入即恢復且 SID 不變。

## 控制寫入

DB 點位也可以被控制：畫面上的 AO / DO 元件或流程圖的寫入節點綁定 DB 點位時，系統會把值寫回 `DBLatestData`（寫入前檢查是否在 Min～Max 範圍內，超出則不寫）。外部系統要讀取這些控制值時，同樣讀 `DBLatestData`。

## 應用範例：時間模擬點位

站台可建立一個名為 `TimeSim` 的 DB 來源，含「當日分鐘數（0～1439）」與「當日小時數（0～23）」兩個點位，再以 Windows 排程 `ScadaTimeSimUpdate` 每分鐘用 `sqlcmd` 執行 `UpdateTimeSim.sql` 更新數值，讓警報、計算點位與流程圖可以取得當日時間進度。

- 換機部署順序：放入 `TimeSim.json` → Engine 重載（產生 SID）→ 註冊排程
- 檢查排程：`Get-ScheduledTask -TaskName ScadaTimeSimUpdate`
- 排程停掉時數值停滯，Engine 不會報錯

## 常見狀況

| 狀況 | 原因 / 處理 |
|------|------------|
| 匯入後一直 **待 Engine 載入** | 確認 Engine 正在執行，按 **通知 Engine 重新載入 JSON**；仍無效時比對 Engine / Web log 的「資料庫設定檔路徑」，並查 Engine log |
| 手動把 JSON 放進 `DBPoint\` 沒生效 | DB 來源不靠資料夾監看，請按 **通知 Engine 重新載入 JSON** |
| 點位一直 Bad | 外部系統寫了 `Quality = 0`，或 Engine 讀 `DBLatestData` 的 SQL 失敗（查 Engine log） |
| 即時值時間戳記不對 | 即時值的時間來自外部系統寫入的 `Timestamp`，請檢查外部系統時鐘 |
| 外部系統寫了不存在的 SID | Engine 記一次警告後略過，不影響其他點位 |
