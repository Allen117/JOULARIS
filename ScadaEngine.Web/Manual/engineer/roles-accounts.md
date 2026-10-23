# 角色體系與帳號 SOP

本章說明系統的三種角色、工程師模式的範圍，以及工程師帳號從裝機、交機到忘記密碼的完整處理流程。

## 三種角色

| 角色 | 定位 | 可使用的頁面 |
|------|------|-------------|
| **Engineer** | 系統建置工程師（廠商端） | 全部頁面，並**獨佔**「工程師模式」七頁 |
| **Admin** | 客戶端管理者 | 工程師模式以外的全部頁面；可管理 Admin / User 帳號 |
| **User** | 一般操作人員 | 依帳號設定逐頁授權（授權清單不含工程師模式頁） |

### 工程師模式包含哪些頁面

只有 Engineer 登入時，導覽列才會出現 **工程師模式** 下拉選單，內含：

| 選單 | 用途 | 本手冊章節 |
|------|------|-----------|
| **工程師模式 → 畫面設計** | 設計即時監控畫面 | [畫面設計](designer) |
| **工程師模式 → 點位設定 → Modbus來源** | Modbus 設備與點位 | [Modbus 來源](modbus-source) |
| **工程師模式 → 點位設定 → DB 來源** | 外部系統寫入 SQL 的點位 | [DB 來源](db-source) |
| **工程師模式 → 點位設定 → OPC UA 來源** | OPC UA Server 與點位 | [OPC UA 來源](opcua-source) |
| **工程師模式 → 點位設定 → 計算點位** | 公式衍生點位 | [計算點位](calc-point) |
| **工程師模式 → 流程圖控制** | 控制邏輯與演算法 | [流程圖控制與演算法](logic-flow) |
| **工程師模式 → 系統參數** | 現場可調的系統上限與門檻 | [系統參數](system-parameter) |

> ⚠️ 注意：Admin 或 User 即使直接在網址列輸入上述頁面，也會被導向「無權限存取」頁。工程師模式頁不能透過權限設定開放給其他角色。

> 💡 提示：本「工程師手冊」也只有 Engineer 看得到。交機給業主後，請引導 Admin 閱讀 SCADA / EMS 使用手冊。

## 帳號管理規則

帳號管理頁位於 **系統設定 → 帳號管理**，Admin 與 Engineer 都能進入，但看到的範圍不同：

- **Admin 看不到也管不到 Engineer 帳號**：清單中不會列出 Engineer，**角色** 下拉也沒有 Engineer 選項
- **Engineer 可管理所有帳號**：包含建立其他 Engineer 的個人具名帳號、互相重設密碼
- 建立或修改 Engineer 帳號，操作者本身必須是 Engineer

<!-- IMG-TODO: engineer/roles-accounts-01 | 帳號管理：使用者清單與新增使用者視窗（Engineer 登入時角色下拉含 Engineer） | route=/AccountSetting -->

## 裝機到交機的帳號流程

### 步驟一：裝機時建立 bootstrap 帳號 engineer

執行 Release 包的 `Install.bat` 時，在 **[5/8] Database setup** 步驟會呼叫 `install-db.ps1`，於 console 要求輸入 `engineer` 帳號的密碼：

1. 輸入密碼（畫面遮罩，至少 8 碼）
2. 再輸入一次核對
3. 完成後即可用帳號 `engineer` 登入 Web

> 💡 提示：資料庫內只要已存在任何 Engineer 角色帳號（含停用的），`install-db.ps1` 就會跳過、不會重複建立。升級時重跑不會長出新的 `engineer`。

> 💡 提示：資料庫內已有 Engineer 時，系統不會再引導建立首組 Admin（不會出現 /Setup 頁）。請直接用 `engineer` 登入，再為業主建立 Admin。

### 步驟二：建立個人具名帳號

1. 以 `engineer` 登入，進入 **系統設定 → 帳號管理**
2. 按 **新增使用者**，填 **帳號**、**姓名**、**密碼**、**確認密碼**，**角色** 選 **Engineer**
3. 為每位會到現場的工程師各建一個帳號
4. 以個人帳號登入確認可用後，編輯 `engineer`，取消勾選 **啟用帳號** 並 **儲存變更**

> 💡 提示：具名帳號搭配事件記錄（例如點位修改會記錄操作者帳號），出事時可追溯是誰改的；人員離職時直接停用該帳號即可，不必通知所有人換密碼。

### 步驟三：為業主建立 Admin 與 User

1. **新增使用者**，**角色** 選 **Admin**，交給業主管理者
2. 一般操作人員選 **User**，並在下方 **頁面存取權限** 勾選可用頁面
3. **即時監控子頁面（ScadaPage）** 區塊可逐頁勾 **可檢視** / **可控制**

> ⚠️ 注意：**即時監控子頁面** 清單來自已儲存的畫面設計。請先在 [畫面設計](designer) 完成並儲存頁面，再回來設定 User 權限，否則會顯示「尚無已發佈的畫面設計」。

## 忘記工程師密碼

使用 Release 包根目錄的 `reset-engineer-password.ps1`：

1. 將 Release 包（或至少此腳本）以 USB 帶到現場伺服器
2. 以 PowerShell 執行：`powershell -ExecutionPolicy Bypass -File reset-engineer-password.ps1`
3. 輸入新密碼，腳本直接覆蓋 `engineer` 的密碼，並確保帳號為啟用狀態
4. `engineer` 不存在時會自動建立（既有站台補建第一個 Engineer 帳號也用這支）
5. 登入後，依需要再停用或保留 `engineer`

腳本自動尋找資料庫設定，順序為：環境變數 `JOULARIS_DBSETTING` → `C:\JOULARIS\Shared\dbSetting.json` → 包根 `Shared\` → 腳本同資料夾 → 包內 `Engine\App\Setting` → 舊佈局 `C:\JOULARIS\Engine\App\Setting`。它以 dbSetting 的應用帳號連線，不需要 SQL sysadmin。

> ⚠️ 注意：此腳本**刻意不安裝到客戶伺服器**——留在伺服器上等於任何能碰檔案系統的人都能一鍵重設工程師密碼。請隨 USB 攜帶，用完帶走。

### 無法執行腳本時（手動 SQL）

在 SSMS 對系統資料庫執行（`<hash>` 為新密碼的 SHA256 小寫十六進位字串）：

```sql
INSERT INTO Users (Username, RealName, PasswordHash, Role, Department, IsActive, CreatedAt, UpdatedAt)
VALUES (N'engineer', N'工程師', N'<hash>', N'Engineer', N'', 1, GETDATE(), GETDATE());
```

若帳號已存在，改用 `UPDATE Users SET PasswordHash = N'<hash>', IsActive = 1 WHERE Username = N'engineer'`。

## 密碼政策

- 不做強制定期換密碼（依 NIST SP 800-63B 建議），**懷疑外洩時才換**
- 換密碼可在帳號管理頁由其他 Engineer 重設，或用 `reset-engineer-password.ps1`

## 常見狀況

| 狀況 | 原因 / 處理 |
|------|------------|
| 登入後導覽列沒有「工程師模式」 | 登入的不是 Engineer 角色帳號 |
| Admin 說帳號清單少了人 | 正常：Admin 看不到 Engineer 帳號 |
| 新機裝完，開登入頁被導到初始設定頁 | 資料庫沒有任何啟用的 Admin / Engineer（例如 `install-db.ps1` 時跳過了密碼輸入）。可在伺服器本機建立首組 Admin，或執行 `reset-engineer-password.ps1` 補建 `engineer` |
| 從遠端電腦開登入頁，提示要在伺服器本機完成初始設定 | 首次初始設定只允許在伺服器本機操作，請到伺服器上開 `http://localhost:5038` |
