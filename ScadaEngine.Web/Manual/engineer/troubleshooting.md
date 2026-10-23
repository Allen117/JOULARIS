# 疑難排解

本章整理現場最常遇到的問題與排查順序。原則是：**先看服務狀態 → 再看 log → 最後才動設定**。

## 第一步：確認服務狀態

以系統管理員開 PowerShell：

```powershell
Get-Service JoularisEngine, JoularisWeb, JoularisLicense, JoularisModbusGateway
```

| 服務 | 停止時的症狀 |
|------|-------------|
| `JoularisEngine` | 即時值不更新、警報不觸發、流程圖不執行、每週備份不執行 |
| `JoularisWeb` | 網頁打不開 |
| `JoularisLicense` | 頁面出現紅色授權失效橫幅，採集暫停 |
| `JoularisModbusGateway` | 第三方 Modbus 讀不到（選配，未安裝時查不到屬正常） |

另外確認 SQL Server 與 MQTT Broker 服務也在執行。

重新啟動單一服務：`Restart-Service JoularisEngine`

## 第二步：看 log

| 元件 | Log 位置 |
|------|---------|
| Engine | `C:\JOULARIS\Engine\App\Log\ScadaEngine-*.log`（保留 30 天）；錯誤另存 `ScadaEngine-Error-*.log`（保留 90 天） |
| Web | `C:\JOULARIS\Web\App\Log\Web-*.log` |
| License Bridge | `C:\JOULARIS\LicenseBridge\Log\bridge-*.log`（保留 7 天） |
| Modbus Gateway | `C:\JOULARIS\ModbusServer\App\Log\ModbusGateway-*.log`（保留 30 天） |
| 安裝 / 遷移 | `C:\JOULARIS\install-log.txt` |

Engine log 常見訊息：

| 訊息 | 意義 |
|------|------|
| `Modbus 連線建立成功` / `Modbus 連線建立失敗` | 設備連線狀態 |
| `警報觸發: SID [type]` / `警報恢復: SID [type]` | 警報狀態轉換 |
| `計算點位 SID 計算失敗` | 計算點位公式錯誤 |
| `資料庫設定檔路徑: …（來源: …）` | 啟動時實際讀到的 dbSetting 位置 |

Engine 每 60 秒也會輸出一次系統狀態（設備數與各設備連線狀態）。

> 💡 提示：Web 的 **歷史資料 → 事件記錄** 也可查到資料庫備份結果、通知寄送結果、演算法狀態異常、點位設定修改紀錄等。

## 常見問題速查

### 網頁打不開

1. `JoularisWeb` 是否 Running
2. 本機開 `http://localhost:5038` 是否可以；本機可以、遠端不行 → 檢查防火牆 5038 / 7189 是否放行
3. 有 HTTPS 憑證時，HTTP 5038 會自動轉到 HTTPS 7189；遠端電腦若沒安裝 CA 會跳憑證警告，見 [內網 HTTPS](https)
4. Web 服務起不來且 Engine 也停止 → 多半是缺 ASP.NET Core Runtime 8，見 [部署與升級](deployment)

> ⚠️ 注意：Web 的 5038 埠固定寫在程式中，環境變數無法更改。若有另一個程式佔用 5038，Web 會起不來。

### 即時值不更新 / 全部 Bad

| 檢查 | 處理 |
|------|------|
| 頁面頂端有紅色授權失效橫幅 | 見 [授權加密狗](license) |
| `JoularisEngine` 未執行 | 啟動服務並看 Engine log |
| MQTT Broker 未執行 | 啟動 broker；Engine 斷線後每 30 秒自動重試連線 |
| 單一設備 Bad | 設備網路不通或 IP / Port 錯誤；Engine 會自動重連 |
| DB 來源點位 Bad | 外部系統寫了 `Quality = 0`，或 Engine 讀 `DBLatestData` 失敗 |
| OPC UA 點位 Bad | 到 OPC UA 來源頁用 **測試讀取** 驗證連線 |

### Web 與 Engine 看到的資料不一致 / 匯入後一直「待 Engine 載入」

最常見原因是 Engine 與 Web 讀到不同的 `dbSetting.json`，連到不同的資料庫。

1. 打開 Engine 與 Web 的 log，找啟動時的 `資料庫設定檔路徑:` 那一行
2. 兩者應同為 `C:\JOULARIS\Shared\dbSetting.json`（來源：Shared）
3. 不同時，依下表的讀取順序找出是哪一層搶先命中：

| 順序 | 路徑 | 說明 |
|------|------|------|
| 1 | 環境變數 `JOULARIS_DBSETTING` 指定的路徑 | 設了但檔案不存在則略過 |
| 2 | `C:\JOULARIS\Shared\dbSetting.json` | 生產機正常位置 |
| 3 | 各程式自己的 `App\Setting\dbSetting.json` | 舊佈局殘留；生產機不應存在 |
| 4 | 開發用 repo 路徑 | 只在開發機 |

4. 刪除 `App\Setting\` 下殘留的 dbSetting 複本或移除錯誤的環境變數，重啟 Engine 與 Web

> 💡 提示：Email / 簡訊 / Line 設定檔也用同樣的順序（但沒有環境變數層），log 中有對應的「通知設定檔路徑」行。Web 改了 SMTP 設定但 Engine 沒生效時，同樣先比對這幾行。

### 已刪除的點位或舊名稱又出現

Engine 以 Retain 方式發布 MQTT，broker 會保留每個點位的最後一筆訊息。刪除或改名點位後，broker 上的舊訊息仍在，Web 重啟時可能短暫顯示。

處理：**將 Engine 與 MQTT Broker 一起重啟**，即可清除舊的保留訊息。

### 演算法清單不對 / 演算法節點錯誤

- 流程圖編輯器的演算法清單讀 `C:\JOULARIS\Web\App\Algorithms\`；Engine 執行的是 `C:\JOULARIS\Engine\App\Algorithms\`。兩處內容需一致
- 清單數量與資料夾檔案對不上時，檢查機器上是否殘留其他舊部署的 Algorithms 資料夾（例如 `C:\Engine\App\Algorithms`）
- 演算法節點出現 `API_CALL_FAILED`：Engine 連不上 Python 演算法服務，檢查 `Engine\App\PythonRuntime\` 是否存在並看 Engine log
- 修改演算法檔頭標記後要重啟 Engine

### 資料庫相關

| 狀況 | 處理 |
|------|------|
| log 顯示資料庫不存在且無權限建立 | 執行 `install-db.ps1`，見 [資料庫建立與備份](database-backup) |
| log 顯示資料庫存在但帳號開不了 | 應用帳號沒有對應到該資料庫，重跑 `install-db.ps1` 補權限 |
| log 出現欄位型別不一致的警告 | 系統只加欄位、不改既有欄位；屬資料庫漂移，需人工評估 |
| 每週備份失敗 | 檢查 `Backup` 資料夾是否存在、SQL 服務帳號是否有寫入權；補跑 `install-db.ps1` |

### 忘記工程師密碼

使用 Release 包根目錄的 `reset-engineer-password.ps1`，見 [角色體系與帳號 SOP](roles-accounts)。

## 開發機與生產機同時存在時

若在同一台機器上同時有開發 build 與正式站台，會有兩個 `ScadaEngine.Web` 進程。結束開發進程時**務必依路徑過濾**，避免誤關正式站台：

```powershell
Get-Process -Name 'ScadaEngine.Web' | Select-Object Id, Path
```

確認哪個是開發 build 後再結束該進程；正式站台的路徑在 `C:\JOULARIS\Web\App\`。

## 回報問題時請提供

1. 版號（Web 頁尾，或 services.msc 服務描述）
2. 問題發生時間
3. 相關服務的 log（Engine / Web / License / Gateway，依問題而定）
4. 事件記錄頁的相關紀錄截圖
5. 若與設定有關，附上對應設定檔（`dbSetting.json` 請遮蔽密碼）
