# 內網 HTTPS

本章說明如何讓同網段的電腦以 `https://<伺服器IP>:7189` 連線，並在每台用戶端安裝內網 CA 後**完全不跳憑證警告**。

## 運作方式

純內網 IP 無法向公開憑證機構申請憑證，因此系統自建一個「內網專屬 CA」：

- **內網 CA**（`ca.pfx`，含私鑰，留在伺服器）簽發**伺服器憑證**（`scada-web.pfx`，內含伺服器的 LAN IP）
- Web 啟動時載入伺服器憑證，開啟 HTTPS
- 每台用戶端把 CA 的**公開憑證**（`ScadaEngine-CA.crt`）安裝到「受信任的根憑證授權單位」後，連線即零警告

| 埠 | 協定 | 說明 |
|----|------|------|
| 5038 | HTTP | 一律開啟；有憑證時自動轉址到 HTTPS |
| 7189 | HTTPS | 只有 `Web\App\certs\scada-web.pfx` 存在時才啟用 |

> 💡 提示：沒有憑證檔時 Web 會自動退回純 HTTP，不會壞掉。

## 伺服器端

### 使用 Install.bat 安裝（一般情況）

不需手動處理。`Install.bat` 步驟 [6/8] 會：

- **首次安裝**：自動產生 CA 與伺服器憑證（用安裝當下偵測到的 LAN IP），並把用戶端安裝包複製到 `C:\JOULARIS\ClientCA_Installer\`
- **升級**：偵測到既有憑證就略過，沿用原憑證，用戶端不必重裝
- 憑證產生失敗時只印警告，Web 退回純 HTTP，不中斷安裝

防火牆 5038 / 7189 也由 `Install.bat` 自動放行。

### 手動產生或重簽

僅在手動部署、或伺服器 IP 變更時需要：

```powershell
powershell -ExecutionPolicy Bypass -File C:\JOULARIS\Web\App\certs\generate-https-cert.ps1
net stop JoularisWeb
net start JoularisWeb
```

手動部署時防火牆需自行放行：

```powershell
New-NetFirewallRule -DisplayName "JOULARIS Web" -Direction Inbound -Protocol TCP -LocalPort 5038 -Action Allow
New-NetFirewallRule -DisplayName "JOULARIS Web HTTPS" -Direction Inbound -Protocol TCP -LocalPort 7189 -Action Allow
```

## 用戶端（每台一次）

1. 把伺服器的 `C:\JOULARIS\ClientCA_Installer\` 整個資料夾（內含 `ScadaEngine-CA.crt` 與 `install-ca-on-client.ps1`）複製到用戶端電腦
2. 以**系統管理員**開 PowerShell，進入該資料夾執行 `./install-ca-on-client.ps1`
3. **完全關閉**瀏覽器後重開
4. 以 `https://<伺服器IP>:7189` 連線，網址列應顯示安全連線、沒有警告

> ⚠️ 注意：請用伺服器的 **IP** 連線，且必須是憑證裡記錄的那個 IP（產生憑證時畫面會印出）。用電腦名稱或其他 IP 連線仍會跳警告。

> 💡 提示：用戶端安裝的只是**公開**憑證，不含私鑰，只代表信任本內網 CA 簽發的憑證，不會讓該電腦能冒充他人。

> 💡 提示：用戶端尚未安裝 CA 前，瀏覽器會跳憑證警告，但按「繼續前往」仍可使用。

## 維運情境

| 情境 | 處理 |
|------|------|
| 伺服器 IP 變更 | 伺服器重跑 `generate-https-cert.ps1`；CA 沿用，只重簽伺服器憑證 → **用戶端不必重裝**。重啟 `JoularisWeb` |
| 要重建 CA | 刪除 `Web\App\certs\ca.pfx` 後重跑腳本 → **所有用戶端都要重裝新的** `ScadaEngine-CA.crt` |
| 暫時停用 HTTPS（只用 HTTP 5038） | 刪除 `Web\App\certs\scada-web.pfx`，重啟 `JoularisWeb` |
| 用戶端移除 CA | 以系統管理員執行下方指令 |

用戶端移除 CA：

```powershell
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like '*ScadaEngine Internal CA*' } | Remove-Item
```

有效期限：CA 10 年、伺服器憑證 5 年。

> ⚠️ 注意：`ca.pfx` 是 CA 私鑰，請勿複製給用戶端或外流。Release 包本身不含任何憑證檔，憑證都在各伺服器現場產生，升級時原地保留。

## 驗證憑證內容

伺服器憑證的關鍵欄位：

- 簽發者：`CN=ScadaEngine Internal CA, O=ScadaEngine, C=TW`
- 主體別名（SAN）：`localhost`、`127.0.0.1`、伺服器 LAN IP
- 用途：伺服器驗證

用戶端未安裝 CA 時，唯一的錯誤應該是「根憑證不受信任」；若出現「名稱不符」，表示連線用的 IP 不在憑證裡，請重簽。
