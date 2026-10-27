# docs/plans 規則

plan 何時寫、何時停、何時 commit 見根目錄 CLAUDE.md §實作計畫（Plan）規則；本檔只管成本記錄。

## Token / 成本記錄（報告用）

每份 plan 都要在 plan.md 的「討論過程與成本記錄」段（見 `_template.md`）記錄成本，供未來報告使用：

- **寫完 plan 時**：記錄 (a) 討論過程摘要 — 提了哪些問題、使用者怎麼回、因此 plan 改了什麼；(b) 撰寫 plan 至此花費的 token 數（input / output 分列）與**使用模型**
- **實作期間**：每次 `git commit` / `git push` 後，追記該階段累計 token（跨對話的 plan：每個對話收尾前先把該對話用量寫進 plan，避免遺失）
- **Archive 時**：回填**總 token**（input / output / cache read / cache write 分列）、**模型**、**對應 API 費用** — 費用依「當下」官方牌價換算並列出計算式；牌價必須現查（`claude-api` skill 或 https://platform.claude.com/docs/en/about-claude/pricing ），**不可憑記憶報價**
- **token 取得方式**（模型無法自省用量，禁止估計或編造）：
  1. 解析本 session transcript：`%USERPROFILE%\.claude\projects\` 下**對應本專案**的資料夾（名稱 = 工作目錄路徑轉 kebab-case，形如 `d--Repo-ScadaEngine`）中**最近修改**的 `<session-id>.jsonl`，加總各則 assistant 訊息的 `message.usage`（`input_tokens` / `output_tokens` / `cache_read_input_tokens` / `cache_creation_input_tokens`）
     - ⚠️ 勿寫死帳號或路徑 —— 不同機器/帳號/repo 位置不同；用 `ls -t <projects 目錄>/*.jsonl | head -1` 取最新那份
  2. 若無法讀取，請使用者執行 `/cost` 並貼上結果
- 若某階段數字取不到，如實標「未取得」，不可補估
