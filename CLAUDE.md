# CLAUDE.md

本檔案提供 Claude Code 在此專案工作時的指引。

## 溝通規則

- 所有對話與回覆一律使用**台灣繁體中文**。
- 專有名詞（如 Astro、component、commit、push、TypeScript 等技術術語）可以使用英文，其餘內容都用灣繁體中文。
- 所有文案都不要讓人有AI感。
- icon都需要自己產生SVG經審核通過使用，不得直接使用emoji。

## Git 工作流程

- **完成工作後，必須先向使用者確認完成、取得同意，才能執行 `git commit` 與 `git push`。**
- 未經確認不可自行 commit 或 push。

## 專案概覽

- 這是 JOULARIS 的官方網站，使用 [Astro](https://astro.build) 建置。
- 遠端 repo：https://github.com/Allen117/JOULARIS.git（主分支為 `main`）。

### 常用指令

```bash
npm run dev      # 啟動開發伺服器
npm run build    # 建置正式版本（輸出到 dist/）
npm run preview  # 預覽建置結果
```

### 目錄結構

- `src/pages/` — 頁面（`index.astro` 為首頁）
- `src/components/` — 各區塊 component（Hero、Features、BrandStory、UseCases、WhyUs、Onboarding、Faq、Contact、Footer）
- `src/layouts/` — 版面配置（`BaseLayout.astro`）
- `src/styles/` — 全域樣式（`global.css`）
- `public/` — 靜態資源（logo、favicon）
