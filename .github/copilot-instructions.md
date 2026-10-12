# SCADA 引擎系統(ScadaEngine.Engine)設計規範 (SYSTEM_DESIGN.md)

## 1. 專案願景與目標
* **開發環境**：使用 C# 12 / .NET 8 Worker Service。
* **通訊核心**：Modbus TCP 採集與 MQTT (Pub-Sub) 轉發。
* **架構理念**：MVC 模式，落實「肥 Model，瘦 Controller」原則，核心運算與資料驗證封裝於 Model 。
* **語言規定**： 所有輸出（包含回覆、思考、任務清單、分析、建議、程式碼註解）均使用繁體中文，嚴禁使用簡體或其他語言。

## 1.1 安全協定（關鍵）
項目	規範說明
禁止硬編碼機密	絕對不可將 API 金鑰、密碼或權杖寫入程式碼，必須使用 .env 檔案或環境變數
輸入驗證	所有外部輸入（使用者、API、資料庫）都必須經過驗證與清理
依賴項檢查	未經明確許可，不得引入新依賴項，盡量使用標準函式庫（如 Python 的 os、json）
資料隱私	絕對不可記錄敏感使用者資料（PII），日誌中的 ID 與電子郵件須遮蔽處理



## 2. 命名規則標準 (依據工研院規範)
所有程式碼變數、類別名稱、內部成員必須符合以下規則 ：

### 2.1 匈牙利命名法前綴 (Basic Types)
* **`n` (int/long/short/uint)**：整數、編號。例如：`nDeviceId`, `nCount` 。
* **`f` (float)**：單精度浮點數。例如：`fTemperatureC` 。
* **`d` (double)**：雙精度浮點數。例如：`dVoltageV` 。
* **`sz` (string)**：字串。例如：`szName`, `szIp` 。
* **`is` (bool)**：布林值。例如：`isConnected`, `isAlarm` 。
* **`dt` (DateTime)**：時間戳記。例如：`dtCreated`, `dtTimestamp` 。
* **`dec` (decimal)**：數值。例如：`decPrice`。
* **`ch` (char)**：字元。例如：`chSeparator`。

### 2.2 容器類後綴 (Containers)
* **List<T>**：名稱後綴 `List`。例如：`deviceList` 。
* **Dictionary<K,V>**：名稱後綴 `Dict` 或 `Map`。例如：`statusDict` 。
* **Array (T[])**：名稱後綴 `Array`。例如：`sampleArray` 。
* **Queue/Stack**：例如 `txQueue`, `undoStack` 。

### 2.3 作用域與特殊定義 (Scope)
* **私有欄位 (Private Field)**：必須加上底線 `_`。例如：`private int _nCount;` 。
* **靜態變數 (Static)**：加上 `s_` 前綴。例如：`static string s_szVersion;` 。
* **介面 (Interface)**：必須以 `I` 開頭。例如：`public interface IRepository`。

---

## 3. JSON 配置檔規格說明 

### 3.1 Modbus連線參數與點位(Modbus/Modbus.json)此檔案負責Modbus連線參數與點位(可能有很多個，json檔名會不同)

### 3.1.1 根節點 (Root) 欄位定義
* **`TypeId` (n)**: 通訊類型識別碼，modbus為30。
* **`IP` (sz)**: 遠端 Modbus 設備的 IP 地址。
* **`Port` (n)**: TCP 通訊埠，預設為 502。
* **`ModbusId` (sz)**: 設備站號 (Unit ID)，如果有多個，會用逗點隔開，例如：`1,2,3`。
* **`connectTimeout` (n)**: * 
    **單位**：毫秒 (ms)。   
    **定義**：建立通訊連線的最長等待時間。

* **`Tags` (List)**: 存放該設備下所有通訊點位的列表。

### 3.1.2 點位列表 (Tags) 內部欄位定義
* **`Name` (sz)**: 點位代碼（如 `CH1RUN`）。
* **`Address` (n)**: Modbus 暫存器起始地址。
* **`DataType` (sz)**: 資料型態（Integer, FloatingPt, SwappedFP 等）。
* **`Ratio` (f)**: 數值縮放比例。實際值 = 原始值做完資料型態轉換後的結果 × Ratio。
* **`Unit` (sz)**: 物理單位（如 C, F）。
* **`Max` (sz)**: 如果是控制點位，此為可控制之最大值。
* **`Min` (sz)**: 如果是控制點位，此為可控制之最小值。

### 3.1.3 點位的定義與讀值方法
* **'SID'(sz)**: 每個點位都會有個專屬的SID，他是XXX-SN的形式，每個json檔進資料庫時會取得一個自動生成的ID，XXX=ID*65536+ModbusID*256+1，N則是依照Tags順序。

### 3.2 資料庫配置(Database.json)此檔案負責資料庫連線參數。

本節定義資料庫連線標準，Copilot 應依此生成連線模型。
* **`DatabaseAddress` (sz)**: 資料庫伺服器 IP 或主機名稱。例如：`127.0.0.1`。
* **`DataBaseName` (sz)**: 資料庫名稱。例如：`wsnCsharp`。
* **`DataBaseAccount` (sz)**: 登入帳號。例如：`wsn`。
* **`DataBasePassword` (sz)**: **(敏感資料)** 登入密碼。

### 3.3 MQTT 與連線配置(MqttSetting/mqttsetting.json)此檔案負責 MQTT Broker 連線參數與資料庫連線字串。

本節定義MQTT Broker 連線標準，Copilot 應依此生成連線模型。
* **`szBrokerIp (sz)**: MQTT Broker 的 IP 地址。例：'127.0.0.1'。

* **`nPort (n)**: MQTT 通訊埠。例：1883。

* **`szClientId (sz)**: 端點識別碼。例：SCADA_Main_Engine。

* **`szBaseTopic (sz)**: 發布主題的前綴。例：SCADA/Realtime。

* **`isRetain (is)**: 是否保留最後一筆訊息。

* **`szDefaultConnection (sz)**: 資料庫完整連線字串。

---

## 4. 通訊與邏輯架構
### 4.1 Modbus 採集邏輯
* 採用輪詢 (Polling) 模式讀取暫存器。
* 根據 `DataType` 處理位元組交換邏輯 (Byte Swap)。

### 4.2 MQTT 發布邏輯
* **運作本質**：主動推播 (Push)，資料產生後毫秒級送達 。
* **斷線處理**：內建 LWT (遺言) 功能，斷線立刻通知 。

---

## 5. UI 元件命名規範 (預留 MVC 呈現使用)
採用 **前綴導向 (Prefix-first)** 格式：**縮寫 + 業務邏輯名稱** 。

### 5.1 基礎輸入與顯示 (Basic Controls)
| 元件名稱 | 縮寫 | 範例 |
| :--- | :--- | :--- |
| Label (標籤) | `lbl` | `lblUserName` |
| Button (按鈕) | `btn` | `btnSubmit`, `btnCancel`  |
| TextBox (文字框) | `tb` | `tbPassword`, `tbEmail`  |
| CheckBox (核取方塊) | `chk` | `chkIsActive`  |
| RadioButton (單選鈕) | `rb` | `rbFemale` |
| ComboBox (下拉選單) | `cb` | `cbCategory` |
| ListBox (清單方塊) | `lstb` | `lstbLogs`  |
| PictureBox (圖片框) | `pic`/`pb` | `picAvatar`, `pbLogo` |

### 5.2 進階資料展示 (Data Controls)
| 元件名稱 | 建議縮寫 | 範例 |
| :--- | :--- | :--- |
| DataGridView (資料格) | `dgv` | `dgvOrderList`|
| ListView (清單檢視) | `lvw`/`lv` | `lvwFiles`|
| TreeView (樹狀檢視) | `tvw`/`tv` | `tvDirectory`|
| Chart (圖表) | `cht` | `chtSalesData`|

### 5.3 容器與佈局 (Containers & Layouts)
| 元件名稱 | 建議縮寫 | 範例 |
| :--- | :--- | :--- |
| Form (視窗表單) | `frm` | `frmMain`|
| Panel (面板) | `pnl` | `pnlHeader`|
| GroupBox (群組方塊) | `grp`/`gb` | `grpSettings`|
| TabControl (標籤控制) | `tab` | `tabMainOptions`|
| TabPage (單一標籤頁) | `tpg` | `tpgGeneralSettings`|

### 5.4 功能性元件 (Provider & Non-Visual)
| 元件名稱 | 建議縮寫 | 範例 |
| :--- | :--- | :--- |
| Timer (計時器) | `tmr` | `tmrAutoRefresh`|
| Menu (選單) | `mnu` | `mnuFile`|
| MenuItem (選單項目) | `tsm` | `tsmSave`, `tsmExit`|
| ToolTip (工具提示) | `ttp` | `ttpHelp`|
| BackgroundWorker | `bgw` | `bgwFileProcessor`|

## 6. 配置管理與部署規範 (Deployment)

### 6.1 檔案存放路徑 (Output Path)
為了方便現場維護，設定檔採分類存放，程式讀取路徑應相對於執行檔目錄：
* **資料庫設定**：生產機 `C:\SCADA\Shared\dbSetting.json`（Engine / Web / Gateway 共用一份）、開發 `ScadaEngine.Engine/Setting/dbSetting.json`；解析順序見 docs/架構.md §共用設定檔解析順序
* **Modbus 點位設定**：`./Modbus/modbus.json`

### 6.2 專案配置 (csproj)
* 所有設定檔必須設定為 `CopyToOutputDirectory: PreserveNewest`。
* 部署時需確保 `bin` 目錄下包含 `Setting`、`Modbus` 與 `Mqtt` 子資料夾。

## 7. Modbus 指令產生與解析邏輯 (Command Logic)

### 7.1 地址解析規則 (Address Mapping)
* **地址慣例**：`modbus.json` 中的 `szAddress` 採 5 位數慣例表示法。
    * `4xxxx`或`4xxxxx`: 對應 Read Holding Registers (Function Code 03)。
    * `3xxxx`或`3xxxxx`: 對應 Read Input Registers (Function Code 04)。
    * `1xxx`、`2xxx`: 對應 Read Input Registers (Function Code 01)。
    * `1xxxx`: 對應 Read Input Registers (Function Code 02)。
    
    
* **偏移量計算**：程式讀取 `40001` 時，應自動扣除前綴並減 1，以 0-based 索引 `0` 向設備發送請求。

### 7.2 資料解析與位元組處理 (Data Conversion)
依據 `szDataType` 欄位執行不同的解析邏輯：
* **Integer**: 讀取 1 個 Register (16-bit)，轉為 `short` 。
* **UInteger**: 讀取 1 個 Register (16-bit)，轉為 `ushort`。
* **FloatingPt (32-bit)**: 讀取 2 個連續 Registers，按照"CDAB"順序後解析 。
* **SwappedFP (32-bit)**: 讀取 2 個連續 Registers，按照"ABCD"順序後解析 。
* **Double (64-bit)**: 讀取 4 個連續 Registers，按照"GHEFCDAB"順序後解析。
* **SwappedDouble (64-bit)**: 讀取 4 個連續 Registers，按照"ABCDEFGH"順序後解析。
* **UINT32 BE (32-bit)**: 讀取 2 個連續 Registers，按照"ABCD" Big-Endian 順序後解析為無號 32 位元整數 (`uint`)。
### 7.3 批量讀取優化 (Batch Read)
* **邏輯**：系統應分析 `tagList`，將地址連續（相差在 10 個 Register 以內）的點位自動打包成單次指令，以減少網路封包負擔。

### 7.4 物理量轉換 (Ratio Calculation)
* **公式**：`fFinalValue = (nRawData * fRatio)`。
* **位置**：此邏輯必須實作於 `TagModel` 類別內（符合肥 Model 原則）。

## 8. 資料持久化預留規範 (Data Persistence Ready)

### 8.1 歷史數據模型 (History Model)
* **規範**：所有採集後的即時資料物件，必須包含 `dtTimestamp` (dt) 、 `SID` (sz) 、`Value` (f) 欄位 。
* **目的**：確保資料在發布至 MQTT 或存入 SQL 時具備時序性 。

### 8.2 儲存介面定義 (Repository Interface)
* **介面**：定義 `IDataRepository` 。
* **方法**：`SaveHistoryDataAsync(historyDataList)`, `SaveLatestDataAsync(latestDataList)` 等。
* **開發策略**：初期實作採 Empty Implementation，待底層通訊穩定後再行實作 SQL 儲存邏輯 。

### 9. 程式碼品質與文件化 (Quality & Docs)
### 7.1 XML 註解規範 (C# Standard)
所有 公開的 (Public) 類別與函式必須包含 XML 註解區塊。這能確保 VS Code 的 Copilot 與 IntelliSense 能提供正確的開發提示。

<summary>: 簡述函式功能。

<param>: 說明參數的物理意義與單位。

<returns>: 說明回傳值的意義。

範例：

C#
/// <summary>
/// 根據 Ratio 計算 Modbus 點位的實體物理量。
/// </summary>
/// <param name="nRawData">原始暫存器整數值</param>
/// <param name="fRatio">點位定義縮放比例</param>
/// <returns>計算後的浮點數物理量</returns>
public float CalculatePhysicalValue(int nRawData, float fRatio) 
{
    return nRawData * fRatio;
}
### 7.2 函式複雜度限制
長度限制：單一函式不超過 50 行。

巢狀深度：最高 3 層，優先使用 「提前返回 (Early Return)」 策略避免箭頭程式碼。

### 9. Antigravity 產出物規範
### 9.1 實作計畫
進行複雜任務時：

先建立「實作計畫」產出物
等待使用者核准後才開始撰寫程式碼