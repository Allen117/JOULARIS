---
name: scada-deploy
description: 部署到 C:\JOULARIS 與本機服務操作。用於：建 Release 包 / 安裝或升級生產機；起開發 Web 實例撞到 5038 埠需暫停生產服務；build 失敗因 ScadaEngine.Web 進程鎖住 bin。
---

# 部署與本機服務操作

詳細流程與服務命名 → docs/功能說明書_部署與遠端更新.md

## 部署到 C:\JOULARIS

- **一鍵全部署**：repo 根目錄 `QuickDeployAll.bat`（系統管理員執行）＝ `BuildRelease.ps1` 打包 → 最新 Release 包 `Install.bat` 安裝 Engine + Web（升級自動備份/還原現場設定）。**不碰 Modbus Gateway** — 要裝/更新 Gateway 另跑 Release 包內 `InstallModbusServer.bat`
- **Release 包為 framework-dependent（瘦身）**：三個 .NET 8 app（Engine/Web/ModbusServer）不自帶 runtime，**目標機須先裝 `ASP.NET Core Runtime 8.0.x (x64)`**（一包覆蓋三者）。`Install.bat` 開頭會偵測，缺就中止不動機器。**升級既有 self-contained 生產機前，務必先裝好 runtime**。只鎖 major=8（只有 .NET 9 不會跑）
- **Python**：抽成 package 根目錄的版號化基礎包，Install.bat 只在缺/版本變更才鋪；只改 C# 碼時可 `BuildRelease.ps1 -NoPython` 產 app-only 小包
- **在 macOS 建包**：`.bat` 在 Mac 跑不了，改執行 `./BuildRelease.command [-NoPython]`（需 `~/.dotnet` 的 .NET 8 SDK + dotnet tool 版 pwsh，不需 Homebrew）；產出的 Release 包搬到 Windows 目標機跑 `Install.bat`。Mac 無法生成 PythonRuntime，完整包須先從 Windows 機複製該資料夾進 repo → 說明書 §在 macOS 上建包
- **安裝根目錄唯一真相 = Install.bat 的 `set "_ROOT=C:\JOULARIS"`**（26.6.0 起；之前是 `C:\SCADA`）。程式碼**不得**寫死任何根目錄 — Shared / LicenseBridge / Algorithms / Python / Web 的 WatchedFolder 都以執行檔位置相對推算。舊機偵測到 `C:\SCADA` 時 Install.bat 先跑 `Engine\App\Setting\migrate-install-root.ps1` 自動搬家（服務 / SQL 資料檔 / 排程 / 設定檔改指；可 `-WhatIf` 預演）→ 說明書 §安裝根目錄遷移
- ⚠️ 各專案 `Scripts\QuickDeploy.bat` 的 Update（選 6）會用 **repo 版設定檔硬蓋** `C:\JOULARIS\...` 現場設定（無備份），且停服務後的進程清理用 `ScadaEngine*` 萬用字元會**誤殺另一個服務的進程**；生產機更新一律走 QuickDeployAll / Install.bat

## 測試撞埠時可暫停生產服務（2026-09-24 使用者授權）

Web 埠 5038 寫死在 Program.cs（`ListenAnyIP`，環境變數無效），生產服務 `JoularisWeb` 佔住雙棧時開發實例起不來。
**為了驗證改動而需要起開發 Web 實例時，允許 `Stop-Service JoularisWeb` 暫停生產服務**（Engine 對應 `JoularisEngine` 同理；四個服務名見說明書 §服務命名）。

- 驗完**同一輪必須復原**：關掉開發實例（路徑過濾殺進程，見下節）→ `Start-Service` 把停掉的服務全部拉回來，並確認 `Get-Service` 為 Running
- 停/啟服務要在回覆中明講，讓使用者知道生產監控有中斷窗口
- 先 `Get-Service Joularis*` 確認本機有沒有生產服務；開發機可能根本沒裝

## 卡住的 Web 進程（鎖住 bin 導致 build 失敗）

生產機上**同時會有兩個 `ScadaEngine.Web` 進程**：`C:\JOULARIS\Web\App\` 是**部署站台**，repo 底下 `ScadaEngine.Web\bin\` 才是開發 build（repo 位置各機不同）。
只有後者會鎖住 build 輸出，用路徑過濾，只停 repo 那支：

```powershell
Get-Process -Name 'ScadaEngine.Web' -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like '*\ScadaEngine.Web\bin\*' -and $_.Path -notlike 'C:\JOULARIS\*' } |
  Stop-Process -Force
```

停之前先 `Get-Process -Name 'ScadaEngine.Web' | Select-Object Id, Path` 確認打到的是哪一個。
為驗證而啟動的開發 Web 進程，**驗完同一輪關掉**，否則下次 build 會被自己鎖住。
