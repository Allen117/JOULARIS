# 對外 Modbus TCP Gateway

Modbus Gateway 把系統所有即時點位值，以標準 **Modbus TCP / FC4（Input Registers）/ float32** 對外提供，讓第三方 SCADA、PLC、HMI 用現成的 Modbus 驅動即可讀取本系統資料。它另附一個唯讀網頁，列出位址對照表與即時值。

Gateway 是**選配**，與主系統分開安裝（`InstallModbusServer.bat`），掛掉也不影響主系統採集。

## 基本資訊

| 項目 | 內容 |
|------|------|
| 服務名 | `JoularisModbusGateway`（顯示名 JOULARIS Modbus Gateway） |
| 安裝位置 | `C:\JOULARIS\ModbusServer\App` |
| Modbus TCP 埠 | 502（安裝時可改） |
| 瀏覽網頁 | `http://<主機>:5041/`（安裝時可改） |
| Log | `C:\JOULARIS\ModbusServer\App\Log\ModbusGateway-*.log`（保留 30 天） |
| 資料來源 | MQTT 即時資料、資料庫最新值、DB 來源點位 |

> 💡 提示：網頁預設埠刻意用 5041，因為 Windows 10/11 內建服務幾乎一定佔用 5040。

## 給第三方的對接規格

| 項目 | 規格 |
|------|------|
| 協定 | Modbus TCP |
| 功能碼 | **FC4 Read Input Registers**（唯讀，不提供寫入） |
| Unit ID | 依設定檔 `UnitId`（預設 **1**），用錯會被直接斷線 |
| 資料型別 | 每點 float32 = **2 個暫存器**，位址一律偶數（0、2、4…） |
| 字組順序 | 預設 **CDAB**（低字組在前，ModScan 的「Floating Pt」）；可切換為 ABCD（ModScan 的「Swapped FP」） |
| 品質不良 | 該點回傳 **NaN**；可切換為保持最後值 |
| 位址表示 | 0 起算的暫存器位址；網頁另顯示 6 位數表示法 300001 + 位址 |

> 💡 提示：NaN 只是數值編碼、不是 Modbus 例外。一次讀一整串暫存器時整包照常成功，只有壞點那 2 個暫存器是 NaN。

## 位址規劃

| 來源 | 起始位址 | 範圍 | 容量 |
|------|---------|------|------|
| Modbus 採集點 | 0 | 0～9999 | 5000 點 |
| 計算點位（僅啟用者） | 10000 | 10000～13999 | 2000 點 |
| DB 來源 | 14000 | 14000～19999 | 3000 點 |
| OPC UA 來源 | 20000 | 20000～25999 | 3000 點 |

### 位址永不變動（AddressMap.json）

- 首次啟動時，各段依 SID 排序配位址，寫入 `C:\JOULARIS\ModbusServer\App\AddressMap.json`
- 之後**只把新點位加在該段尾端**，永不重排、永不回收；第三方寫死的位址永久有效
- 刪除或停用的點位，位址保留為空洞，回傳 NaN，網頁標示「已停用」

> ⚠️ 注意：`AddressMap.json` 是位址對照的唯一紀錄，**升級時必須保留、絕對不可刪除**。刪掉重建等於全部位址重排，所有第三方對接都會讀錯點。`InstallModbusServer.bat` 升級時會自動備份與還原；檔案損毀時服務會拒絕啟動，請人工處理而不要直接刪除。

## 安裝與升級

1. 在 Release 包根目錄，對 `InstallModbusServer.bat` 按右鍵 → **以系統管理員身分執行**
2. 升級時自動備份 `Setting\`、`MqttSetting\`、`AddressMap.json`，覆蓋程式後還原
3. **埠檢查**：502 或 5041 已被佔用時，畫面顯示佔用的程式，並請你輸入替代埠（建議 1502 / 5042），確定後自動寫回設定檔
4. 註冊服務、放行防火牆（實際選定的埠）、啟動服務

> ⚠️ 注意：`Install.bat` 與 `QuickDeployAll.bat` **都不會**安裝或更新 Gateway，升級主系統後若 Gateway 也需更新，請另外執行 `InstallModbusServer.bat`。

### 資料庫連線

- **與主系統同一台**：直接讀 `C:\JOULARIS\Shared\dbSetting.json`，不另放設定
- **裝在另一台機器**：安裝程式會從範本鋪一份 `ModbusServer\App\Setting\dbSetting.json`，請改成主機的 SQL Server IP 與帳密；同時修改 `MqttSetting\MqttSetting.json` 指向主機的 broker

## 設定檔

`C:\JOULARIS\ModbusServer\App\Setting\ModbusServerSetting.json`（只在啟動時讀取，修改後需重啟 `JoularisModbusGateway`）：

| 欄位 | 預設 | 說明 |
|------|------|------|
| `ModbusListenPort` | 502 | Modbus TCP 埠 |
| `UnitId` | 1 | 第三方必須使用的 Unit ID |
| `WordOrder` | `CDAB` | 或 `ABCD` |
| `BadQualityMode` | `NaN` | 或 `HoldLast`（保持最後值） |
| `WebPort` | 5041 | 瀏覽網頁埠 |
| `DbPollSeconds` | 2 | DB 來源點位輪詢間隔 |
| `StaleSeconds` | 300 | 點位超過此秒數未更新即視為斷線 |
| `Segments` | 見位址規劃 | 各段起始與容量 |

> ⚠️ 注意：段界（`Segments`）只能在初次規劃時調整。上線後**不可改既有段的起始位址**；段滿時只能加大容量。

## 瀏覽網頁

開 `http://<主機>:5041/`（唯讀、不需登入，請以防火牆或內網管控存取）：

- 欄位：位址（兩種表示法）、SID、名稱、來源類型、原始位址、即時值、單位、狀態（GOOD / BAD / STALE / NO_DATA / 已停用）、更新時間
- 可依關鍵字、來源類型、狀態篩選；每 5 秒自動更新
- 頂部顯示 Modbus 監聽狀態、字組順序、Unit ID、MQTT 連線、點位總數與最後掃描時間
- **重新掃描點位** 按鈕：手動把新點位加入對照表

<!-- IMG-TODO: engineer/modbus-gateway-01 | Gateway 瀏覽網頁：位址對照表與即時值 | route=none -->

把此網頁的對照表（可篩選後截圖或抄錄）交給第三方，作為他們設定讀取位址的依據。

## 新點位何時出現

- Gateway 啟動時
- DB 來源或 OPC UA 來源在網頁上修改後（自動通知重掃）
- 按網頁的 **重新掃描點位**

> 💡 提示：新增 **Modbus 採集點** 或 **計算點位** 後不會自動通知 Gateway，請按 **重新掃描點位** 或重啟服務。

## 驗證

1. 開瀏覽網頁，確認點位狀態多數為 GOOD
2. 用 ModScan、mbpoll 等工具以 FC4、正確 Unit ID 讀取幾個位址，與網頁的即時值比對
3. 浮點數顯示亂碼時切換工具的字組順序設定

## 常見狀況

| 狀況 | 原因 / 處理 |
|------|------------|
| 一連線就斷、讀不到 | Unit ID 不對，必須使用設定檔的 `UnitId` |
| 數值是亂碼 | 字組順序不符，調整第三方驅動或本服務的 `WordOrder` |
| 讀到 NaN | 該點品質 Bad、Engine 斷線超過 `StaleSeconds`，或點位已刪除；看網頁的狀態欄 |
| 全部 NaN 但網頁正常 | 讀的位址沒有對齊偶數，或用了 FC3（本服務只提供 FC4） |
| 網頁打不開 | `WebPort` 被其他程式佔用，改埠後重啟；log 會記錄佔用訊息 |
| log 出現「位址段已滿」 | 加大該段 `CapacityPoints` 後重啟，不可改 `Start` |
