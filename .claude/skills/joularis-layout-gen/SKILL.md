---
name: joularis-layout-gen
description: 把 pipe-diagram 產的系統圖（layout.json，target joularis）轉成 JOULARIS Designer 可匯入的頁面檔（.joularis-page.json）：底圖 + 依設備型別自動排好的 DI / AI / DO / AO 點位 widget，點位綁定由 Designer「匯入頁面檔」依當地點位自動解析。觸發詞：「自動排點位」「產頁面檔」「layout.json 轉監控頁」「系統圖匯入 Designer」「joularis-page」。不適用：任意 jpg 底圖的障礙偵測排位（v2，未做）、直接寫 DB。
---

# joularis-layout-gen

pipe-diagram 畫圖時已在每台設備下方預留點位區（`guides`），位置是**已知**的；本 skill 只做兩件事：
把點位 widget 排進預留區、把底圖和 widget 打包成 Designer 匯入用的 JSON。**點位綁定不在這裡做**——
widget 只帶「我是哪台設備的哪個角色」（`props.objRoleBind`），由 Designer 匯入時用 `role-aliases.js`
在當下站台的點位中解析（別名只有一份真相，見 plan `docs/plans/_archive/2026-10-21-designer-layout-import.md` 決策 3）。

## 流程

1. **先跑 pipe-diagram**，layout.json 必須：
   - `"target": "joularis"`（預留區 320 × 60；寫成 iauto 也能排，但報告會警告）
   - 每個 guide 寫 `"for": "<設備 id>"`（沒寫就從 `text` 第一個空白前的字串猜，報告標「由文字推斷」）
   - 冰機 / 冷卻水塔的 guide 建議 `"size": "wide"`（430 寬，4 欄 × 100）；一般 320 寬塞 4 欄會把 widget 壓到 72 寬且放不下角色標籤
   - 直式預留區 `"size": "tall"`（210×100）會自動改排成 2 欄多列（fit_grid 取 widget 最寬的排法），報告標「已改少欄多列」屬正常
2. 產生：
   ```
   python <skill 目錄>/scripts/gen_page.py <案名>/layout.json [--bg png|svg] [--out <資料夾>]
   ```
   輸出（預設寫在 layout.json 旁）：
   - `<案名>.joularis-page.json` — 匯入用頁面檔（底圖 base64 內嵌，上限 8 MB）
   - `<案名>_layout_preview.png` — 底圖疊 widget 框：綠 DI / 黃 AI / 橘 DO / 紫 AO / 灰 角色標籤；虛線 = 未綁定佔位
   - `<案名>_report.md` — 設備 → 座位清單、未綁定佔位、提醒
3. **預覽圖給使用者看過才交檔**（Read 那張 PNG），有重疊、壓到設備或標籤要先回 layout.json 調 guide 位置。
4. 使用者在 Designer 頁面樹右鍵 →「匯入頁面檔…」→ 確認每台設備對到的 Device → 建頁 → 按「儲存」。

## 排位規則（roles.json）

- **固定座位**（plan 決策 4）：每個角色有固定格；匯入時該 Device 有命中才建 widget，沒命中留空、不遞補；**整欄都沒命中才收掉該欄**
  | 型別 | 第 1 列 | 第 2 列 |
  |---|---|---|
  | 泵（pump-gray / pump-blue / pump-light） | 故障 DI ｜ 啟停 DO ｜ 頻率設定 AO | 手自動 DI ｜ 運轉 DI ｜ 頻率 AI |
  | 冰機（chiller） | 故障 DI ｜ 啟停 DO ｜ 溫度設定 AO ｜（空） | 遠端/現場 DI ｜ 運轉 DI ｜ 冰水出水溫 AI ｜ 負載 AI |
  | 冷卻水塔（cooling-tower） | 故障 DI ｜ 啟停 DO ｜ 頻率設定 AO ｜（空） | 手自動 DI ｜ 運轉 DI ｜ 頻率 AI ｜ 出水溫 AI |
- 每格 100 × 20、欄距 110、列距 40。**DI / AI 在監控頁不顯示標題**（只有 tooltip），所以格內左側 40 px 放一個 text widget 當角色短名（i18n key `designer.import.label.*`，匯入時依語系解析），widget 佔右側 60 px；DO / AO 本身會顯示標題，佔滿整格
- 框太窄：先縮 widget 寬（最小 60），再改少欄多列；縮到放不下標籤時不放標籤（報告提醒）；仍放不下 → exit 3
- 感測器（sensor-temp / flow-meter / gauge-pressure）旁、效率 / 氣象面板每列放**未綁定**的 AI 佔位（plan 決策 5：不猜），標題 = 感測器 ID / 面板列文字
- 底圖 = pipe-diagram `build()` 拿掉「預留區」圖層後，用 Edge 無頭模式轉 PNG（`--bg svg` 直接內嵌 SVG，縮放較清晰但依賴用戶端字型）

角色別名（點名怎麼對到 RUN / FAULT…）**不放這裡**，在 `ScadaEngine.Web/wwwroot/js/role-aliases.js` 的 `_equipRoleAliases`。

## exit code

| code | 意義 |
|---|---|
| 0 | 成功 |
| 2 | guide 對不到設備、設備型別沒有座位定義、同一台設備兩塊預留區（報告列出，不產頁面檔） |
| 3 | 預留區放不下座位，或感測器四周沒有空位放佔位 |
| 4 | 找不到 pipe-diagram build.py 或 Edge 轉 PNG 失敗 |

## 注意

- **不直接寫 DB**：Designer 存檔是整棵頁面樹一起存，工具直接寫會和開著的 Designer 互相覆蓋
- 頁面檔可攜：在開發機產、帶到任何站台匯入；綁定以「匯入當下該站台的點位」解析。開發機沒有點位時，匯入對話框勾「保留未命中的座位」才會留下全部 widget
- pipe-diagram 是使用者層 skill（iAUTO 共用），對它只做向下相容的加法（`for`、`size: wide`）
- 測試：`python scripts/test_gen_page.py`（`GEN_PAGE_TEST_PNG=1` 會多跑一次 Edge 轉檔）
