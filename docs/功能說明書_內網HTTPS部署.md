# 內網 HTTPS 部署說明

讓同網段其他機器以 `https://<伺服器IP>:7189` 存取 Web，並在安裝內網 CA 後**完全不跳憑證警告**。

## 架構（方案 B：內網 CA 簽發）

純內網 IP 環境無法向公認 CA 申請憑證（公認 CA 只簽有網域名稱者），因此自建一個**內網專屬 CA**：

```
內網 CA（ca.pfx，私鑰留在伺服器）
   │ 簽發
   └── 伺服器憑證（scada-web.pfx，SAN 含伺服器 LAN IP）── Kestrel 載入
       ▲
       └ 每台 client 安裝 CA 公開憑證（ScadaEngine-CA.crt）到「受信任的根憑證授權單位」
         → 該 client 之後連本系統 HTTPS 零警告
```

- **加密**：與正常 HTTPS 相同，帳密/資料全程加密
- **身分驗證**：由內網 CA 背書；client 信任 CA 後即信任伺服器憑證

## 埠與繫結

| 埠 | 協定 | 說明 |
|----|------|------|
| 5038 | HTTP | 一律開；若憑證存在會自動 307 轉址至 HTTPS |
| 7189 | HTTPS | 僅在 `certs/scada-web.pfx` 存在時啟用 |

繫結邏輯在 [ScadaEngine.Web/Program.cs](../ScadaEngine.Web/Program.cs)：`ConfigureKestrel` 用 `ListenAnyIP` 綁全網卡；`isHttpsEnabled`（= pfx 是否存在）同時控制 HTTPS 繫結、`AddHttpsRedirection` 與 `UseHttpsRedirection`。**沒有 pfx 的部署會自動退回純 HTTP，不會壞掉。**

## 檔案

| 檔案 | 進 git | 說明 |
|------|:---:|------|
| `certs/generate-https-cert.ps1` | ✅ | 產生 CA + 伺服器憑證（伺服器端執行） |
| `certs/install-ca-on-client.ps1` | ✅ | 於各 client 安裝 CA（client 端執行） |
| `certs/ca.pfx` | ❌ | 內網 CA 私鑰，機密，留在伺服器 |
| `certs/ScadaEngine-CA.crt` | ❌ | CA 公開憑證，發給每台 client |
| `certs/scada-web.pfx` | ❌ | 伺服器憑證，Kestrel 載入（密碼 `ScadaWeb`，須與 Program.cs 一致） |

憑證產物皆由 [.gitignore](../.gitignore) 排除，各機自行產生。

## 與 Release / Install.bat 的整合

`BuildRelease.ps1` 打包時：

- ✅ **會**把 `certs/*.ps1`（`generate-https-cert.ps1`、`install-ca-on-client.ps1`）copy 進 `Web\App\certs\`
- ❌ **絕不**打包任何 `*.pfx` / `*.crt`（含私鑰，且綁特定機器 IP）—— 由 csproj `CopyToPublishDirectory="Never"` 保證

`Install.bat` 部署到 `C:\JOULARIS\Web\App`，並自動放行防火牆 5038 + 7189。因為 Program.cs 由 `ContentRoot\certs` 讀 pfx，部署後憑證路徑即 `C:\JOULARIS\Web\App\certs\`，與腳本落點一致。

**Install.bat 自動產憑證（步驟 [5/7]）**：安裝流程會在 DB 設定後、開防火牆前**自動執行 `generate-https-cert.ps1`**，所以 install 一跑完 HTTPS 即就緒，不再需要手動跑：

- **首裝**（`certs\scada-web.pfx` 不存在）→ 自動產生 CA + 伺服器憑證（用**目標機當下偵測到的 LAN IP** 簽 SAN）
- **升級**（`scada-web.pfx` 已存在）→ **略過不動**，沿用既有憑證（IP 若變過，手動重跑 `generate-https-cert.ps1`）
- 產生後自動把 client 安裝包（`ScadaEngine-CA.crt` + `install-ca-on-client.ps1`）複製到 **`C:\JOULARIS\ClientCA_Installer\`**，整夾拷給每台 client 即可
- 憑證產生失敗（無 PowerShell 權限等）→ 印 WARN，Web 優雅退回純 HTTP，不中斷安裝
- ⚠️ 因憑證存在會觸發 Program.cs 強制轉址：**client 裝好 CA 前連線會跳憑證警告**（可按繼續前往），裝 CA 後零警告

**重裝 / 升級行為**（回答「腳本會不會跟著更新、憑證會不會被蓋掉」）：

- Install.bat 以 `xcopy /Y` 覆蓋 `Web\App`，**只新增/覆蓋、不刪除**目標多出的檔案
- → 每次重裝：`certs\*.ps1` **會更新**成新版；`certs\*.pfx` / `*.crt`（部署機現產、release 不帶）**原地保留**
- → **升級不必重產憑證、client 不必重裝 CA**（[5/7] 偵測到既有 pfx 會略過）

## 部署步驟

### 一、伺服器端（一次）

**用 Install.bat 安裝者無需手動動作** —— 步驟 [5/7] 已自動產生憑證並組好 client 安裝包，Web 啟動即載入。

僅**手動部署**（非 Install.bat）或**首裝後想改 IP 重簽**時，才自行執行：

```powershell
# 部署路徑
powershell -ExecutionPolicy Bypass -File C:\JOULARIS\Web\App\certs\generate-https-cert.ps1
net stop JoularisWeb; net start JoularisWeb   # 重啟讓 Kestrel 載入憑證
```

> 開發機測試則於原始碼路徑 `ScadaEngine.Web\certs\` 直接跑 `./generate-https-cert.ps1`，`dotnet run` 即讀取。

防火牆 5038 / 7189 由 Install.bat 自動放行；若手動部署（非 Install.bat），需自行放行：

```powershell
New-NetFirewallRule -DisplayName "JOULARIS Web" -Direction Inbound -Protocol TCP -LocalPort 5038 -Action Allow
New-NetFirewallRule -DisplayName "JOULARIS Web HTTPS" -Direction Inbound -Protocol TCP -LocalPort 7189 -Action Allow
```

啟動 Web 後，控制台會印出實際可用的 HTTP / HTTPS 位址。

### 二、每台 client（一次）

把伺服器 `C:\JOULARIS\ClientCA_Installer\` 整個資料夾（內含 `ScadaEngine-CA.crt` + `install-ca-on-client.ps1`）複製到該 client，以**系統管理員**執行：

```powershell
./install-ca-on-client.ps1       # 裝入「受信任的根憑證授權單位」
```

完全關閉並重開瀏覽器 → 以 `https://<伺服器IP>:7189` 連線，不再跳警告。

> 只裝**公開** CA 憑證（不含私鑰），僅代表該機信任本內網 CA 簽發的憑證，不會讓該機能冒充他人。

## 維運

- **伺服器 IP 變了**：伺服器端重跑 `generate-https-cert.ps1`。CA 沿用既有 `ca.pfx`，只重簽 `scada-web.pfx` → **client 不必重裝**。重啟 Web 生效。
- **要重建 CA**：刪掉 `ca.pfx` 再跑 → 所有 client 須重裝新的 `ScadaEngine-CA.crt`。
- **client 移除 CA**（系統管理員）：
  ```powershell
  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like '*ScadaEngine Internal CA*' } | Remove-Item
  ```
- **有效期**：CA 10 年、伺服器憑證 5 年。

## 憑證細節（驗證用）

伺服器憑證 `scada-web.pfx`：

- Issuer：`CN=ScadaEngine Internal CA, O=ScadaEngine, C=TW`
- SAN：`DNS=localhost, IPAddress=127.0.0.1, IPAddress=<伺服器LAN IP>`（IP 型 SAN，滿足 Chrome 對 IP 連線的驗證）
- EKU：serverAuth（`1.3.6.1.5.5.7.3.1`）

client 未裝 CA 時，TLS 驗證結果為「唯一問題 = 根未受信任（UntrustedRoot）」、無名稱不符 —— 裝 CA 後即完全通過。
