# Designer 設備級綁定：泵浦 / 馬達型元件拖到畫布直接指定 Device、整台點位自動帶入

**狀態**: 已完成 <!-- 進行中 | 已完成 | 廢棄 -->
**建立**: 2026-10-14
**最後更新**: 2026-10-14（已 commit / push，歸檔）
**相關 commit**: `534a4af`（26.8.0；同日另兩筆待 commit 的版本依「一版本一 commit」先行：`4f204a6` 26.6.0、`7299ffb` 26.7.0）

---

## 目標 & 背景

同事反映 Designer 綁點位很累。第一性原理拆解：畫面成本 = 設備數 × 每台設備點位數 × 每次點選步驟。
目前泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機四種多點元件，每個槽位（運轉、故障、手自動、頻率、啟停、頻率設定…）
都要各開一次兩步驟 picker，一台泵浦 6 個槽位 = 6 次、十台 = 60 次。

主流 SCADA（Ignition UDT + Template、WinCC Faceplate、Citect Genie、iFIX Dynamo）的解法一致：
**綁定單位是「設備」不是「點位」** — 符號內綁相對角色，實例化時只指定一台設備，成員點位自動對上。
本 plan 是這條路的第一步（純前端、不動資料模型、不動儲存格式）：

> 泵浦 / 馬達型元件拖到畫布 → picker 直接問「這是哪台 Device？」→ 停在**設備層**（不下鑽到點位）→
> 就地預覽各槽位命中結果 → 確認 → 整台點位一次帶入（使用者 2026-10-14 提議）。

設備邊界**直接採用既有 Device 分群**（使用者 2026-10-14 裁定）：Modbus `Tag.Device` → `szDeviceGroup`、OPC UA 的 `Coordinator/Device`、
DB 來源的 Coordinator 群組，picker 已把這些統一投影成每個點位的 `szDeviceLabel`（`_enrichPointsWithDeviceLabel`）。
「一台設備」= picker 設備清單上的一個**葉節點**（單站號 Coordinator / 多站號子站 / Device 子群 / DB 群組 / 計算點位群組），
其點位集合就是 picker 第二步原本會列出的那組（`_renderFilteredPoints` 的篩選條件），在這個集合內用「設備角色別名表」依點位名稱比對各槽位。
開發環境 `Modbus.json` 的 Device 欄全空、命名為 `CH1RUN` 式，**不納入考量**（使用者裁定）；
Device 空白時 `szDeviceLabel` 會退到站號 / Coordinator 名，候選池變成整站，同角色多筆命中會在面板標示，由使用者挑。

既有零件可直接沿用：
- `js/role-aliases.js` 的別名表 + `_roleMatch.resolveRole`（電表角色，token 式比對）— 本次**新增一組設備角色別名**，比對同時支援 token 式（`PUMP-1-RUN`）與字尾式（`CH1RUN`，Device 已填時名稱常仍帶設備碼），不改既有電表角色行為
- `picker.js` 的 `szDeviceLabel` 投影（`_enrichPointsWithDeviceLabel`）與 `_findSameDevicePointForRole`（表格列範本已用「同 `szDeviceLabel` + 角色」找點）— 本次設備比對就是同一條規則
- `picker.js` 設備清單（`renderDeviceList`）的葉節點與第二步篩選（`_renderFilteredPoints`）— 本次把「葉節點 = 一台設備、其篩選結果 = 該設備的點位集合」直接當候選，不另建設備清單
- `picker.js` 的 `_pumpPickerSlot` 槽位機制與 pump/motor 分支的 min/max 帶入規則 — 整台帶入時每個槽位沿用同一套寫入規則

後續階段（**不在本 plan**）：拖放點位 / 設備直接建元件、參數化頁面（頁面吃 `{device}` 參數）。

## 驗收條件

- [x] 從工具箱拖泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機到畫布 → 開 picker（標題「選擇設備」）；設備清單點到**葉節點**時不進點位清單，改顯示**命中預覽**：本型別每個槽位 → 命中點位名 / 「未找到」/ 多筆時標「多筆命中（取第一筆）」，頂部顯示「命中 N / M」（Playwright harness 驗證）
- [x] 預覽畫面按「確認」→ 建立元件、所有命中槽位的 SID 與名稱寫入 props、`szTitle` = 葉節點標籤、頻率 / 頻率設定 / 負載的 min/max 依點位帶入（與單槽位重選一致）、toast「已帶入 N / M」；未命中槽位留空，使用者再用既有單槽位「重選」補
- [x] 預覽畫面可「返回」換另一台設備；關閉 picker（取消 / X）→ **仍建立未綁定元件**（保留舊行為，不卡人）
- [x] 屬性面板頂部有「**重新指定設備**」按鈕 → 同一 picker 流程；若元件已有任一槽位綁定，確認前先 confirm「將以『X』的點位覆蓋目前綁定，未命中的槽位會清空」→ 確定才套用（元件 = 設備實例，換設備就整台換）；confirm 取消 → 不動、picker 留在預覽
- [x] 葉節點涵蓋：單站號無 Device 的 Coordinator（整站）、多站號子站、單站號 Device 子群、「未分群」桶、DB 來源群組、計算點位群組 —— 凡能走到第二步點位清單的節點都能當設備（候選集合走 `_pointInPickedDevice`，與點位清單同一條件；harness 驗 Device 子群 + 計算群組）
- [x] 以 Device 分群驗證：Tag.Device=`冰水泵1` 下有 `RUN` / `故障` / `頻率` / `啟停` / `頻率設定` → 預覽五中一缺（手自動）；`冰水泵2` 的同名點位不出現
- [x] 點位名帶設備碼仍可比對：Device=`冰機1` 下 `CH1RUN` / `CH1ALARM` / `CH1MOA` → 運轉 / 故障 / 手自動三中（字尾式）
- [x] 同設備同角色多筆（`RUN` 與 `RUNNING` 並存）→ 預覽標「多筆命中」取第一筆，可事後單槽位重選
- [x] 其他元件型別（gauge / DI / AO / 表格…）的 picker 行為零變化；既有單槽位「重選」照常（harness 驗 gauge 下鑽點位清單 + pump 單槽位重選）
- [x] 電表角色比對（表格列範本、EnergyMeter 頁）行為零變化 —— `resolveRole` 回傳不變（09-15 的 20 組斷言重跑全過）
- [x] 複製頁面清空綁定（`isDesignerBindingKey`）不受影響：本次不新增任何 `szSid*` / `szCid*` 以外的綁定 key（node 斷言 `no new props keys`）
- [x] zh-TW / en resx 同步；`node --check` 全部新改 JS 通過；scratchpad node 斷言腳本全過（86 組）

## 檔案異動清單

### 新增

- [x] `ScadaEngine.Web/wwwroot/js/designer/equip-autobind.js` — 設備級自動綁定模組（IIFE，掛 `window._equipAutobind`）
  - 純函式（node 可測、不碰 DOM）：`buildEquipSuggestions(arrCandidatePoints, szWidgetType)`（候選 = picker 葉節點的點位集合，由呼叫端篩好傳入）、`applyToProps(props, result, szDeviceLabel, bClearMissing)`、`hasAnyBinding`
  - UI：picker 內預覽區 render（`renderEquipPreview(container, result)`）、`applyToWidget(el, result, szDeviceLabel, bClearMissing)`（`applyToProps` + `renderWidget` / `renderPropPanel` + toast 摘要）
  - 槽位→角色對照表 `EQUIP_SLOT_ROLES[szWidgetType]`（見決策 2）

### 修改

- [x] `ScadaEngine.Web/wwwroot/js/role-aliases.js` — 新增 `window._equipRoleAliases`（RUN / FAULT / MODE / FREQ / STARTSTOP / FREQSET / LOAD / CHWOUT / SETTEMP / WATERTEMP）與 `window._equipRoleMatch.resolveEquipRole(szName, arrRoles)`（整名 → 尾 token → 字尾式三段比對、回角色或 null；`arrRoles` 白名單限定型別角色）。既有 `_roleAliases` / `_roleMatch` **不動**
- [x] `ScadaEngine.Web/wwwroot/js/designer/picker.js`
  - 把 `_renderFilteredPoints` 內的「點位是否屬於目前選定葉節點」篩選抽成 `_pointInPickedDevice(p)`（純重構，列表行為不變），設備模式用同一條件取候選
  - 新增 `_equipDeviceMode` 旗標（四種設備型別開 picker 時為 true）+ `openEquipDevicePicker(x, y, szType)`（新建）/ `rerouteEquipDevice()`（屬性面板按鈕、重綁）
  - 葉節點 click（`selectDeviceItem` / `selectDeviceGroupItem` / `selectDbGroup` / `selectCalcGroup` / `_showCalcPointsFlat` / `showDemandPointStep`）尾端改呼叫 `_afterLeafSelected()`：設備模式下不切到點位清單，改 `_showEquipPreview()`（把 `ppStep2` 的 `pointListContainer` 當預覽區、搜尋框隱藏、標題維持「選擇設備」+ 既有設備名 tag 顯示 X、命中 > 0 才啟用確認鍵）
  - `confirmPointPick()` 開頭加設備模式分支 `_confirmEquipPick()`：新建 → `createWidget` + `applyToWidget`；重綁 → 有綁定先 confirm → `applyToWidget(…, bClearMissing=true)`
  - picker `hidden.bs.modal` 事件（`_onPickerHidden`，載入時只註冊 listener）：設備模式且**未確認**且為新建 → `createWidget(szType, x, y)`（取消仍建空元件）；一律重設旗標 + 還原搜尋框
  - `goBackToDevices()` 開頭 `_resetEquipPreviewUi()` 還原搜尋框；`_showPickerStep0` / `goBackToStep0` 標題依模式切換
- [x] `ScadaEngine.Web/wwwroot/js/designer/widget-core.js` — drop handler：四種設備型別改走 `openEquipDevicePicker(x, y, szType)`；`createWidget` 改為回傳建立的元件 `el`（供確認路徑直接套用）
- [x] `ScadaEngine.Web/wwwroot/js/designer/prop-panel.js` — 四種設備型別 SID 監控段頂部加「重新指定設備」按鈕（`_equipReassignBtnHtml()` → `onclick="rerouteEquipDevice()"`）
- [x] `ScadaEngine.Web/wwwroot/css/designer.css` — （plan 未列、實作時補）預覽沿用 `.row-template-body` 樣式，新增 `.rt-tag.rt-multi`（多筆命中黃 tag）與 `.rt-detected.rt-detected-none`（0 命中紅框）
- [x] `ScadaEngine.Web/Features/Designer/Views/Index.cshtml` — 載入 `equip-autobind.js`（置於 `role-aliases.js` 之後）；picker modal 不加新 step 標記，預覽直接渲染進 `ppStep2` 的 `pointListContainer`（搜尋框隱藏）
- [x] `ScadaEngine.Web/Resources/Features.Designer.Views.Index.resx` + `.en.resx` — 新 key：`designer.equip.title.device`、`reassign_btn`、`preview_hits`（`{n}` / `{m}`）、`preview_title`、`preview_hint`、`not_found`、`multi_hit`、`no_candidates`、`confirm_overwrite`（含 `{device}`）、`toast_applied`（`{n}` / `{m}`）（比原列表多 `preview_title` / `preview_hint` / `no_candidates` 三個）
- [x] `docs/功能說明書_SCADA監控頁.md` — §7.4 (10) 馬達型設備後新增「設備級自動綁定」小節（流程、槽位對照、比對規則與命名風格三例、限制）
- [x] `docs/i18n-glossary.md` — 畫面設計段登錄 設備級自動綁定 / 選擇設備 / 重新指定設備 / 槽位 / 命中 / 未找到 / 多筆命中 / 已帶入

### 資料庫

- 無。不動 `DatabaseSchema.json`、不動設計 JSON 儲存格式（props key 全部沿用既有槽位）

## 關鍵設計決策

### 決策 1：拖放即問「這是哪台 Device」，停在設備層直接帶入（使用者 2026-10-14 提議）
- **選擇**: 四種設備元件拖到畫布就開 picker，但使用者只選到**設備葉節點**為止，不下鑽到點位；葉節點 click 當場顯示各槽位命中預覽，確認即整台帶入。「一台設備」的點位集合 = picker 第二步原本會列的那組點位
- **理由**: (a) 這就是 SCADA 的 Template 實例化心智模型：元件 = 某台設備的一個實例，使用者只需回答「哪台」。(b) 比 v2「先選一個點位再反推設備」少一層、也少一個「為什麼要我先選運轉狀態」的疑惑。(c) 候選集合沿用 picker 既有篩選，單站號 / 多站號 / Device 子群 / DB 群組 / 計算群組全部自動支援，零新資料、零新 API
- **放棄的選項**:
  - v2「選任一點位 → 反推設備 → 建議面板」— 多一步且需另開一個 modal；設備層預覽已涵蓋「看到會帶什麼再確認」的透明度
  - 另做獨立「設備選擇」modal — 重複設備清單邏輯；直接複用 picker 設備層省掉一份要同步維護的清單

### 決策 2：槽位 → 角色對照表與別名（v1 寫死在 JS，不做設定頁）

| 槽位 key | 角色 | 別名（lower-case，字尾式與 token 式共用）|
|---|---|---|
| `szSidRun` | RUN | run, running, on, status, st, 運轉, 運行, 啟動狀態 |
| `szSidFault` | FAULT | alarm, alm, fault, flt, trip, err, error, fail, 故障, 警報, 跳脫 |
| `szSidMode` | MODE | moa, hoa, mode, auto, am, ma, remote, rem, local, loc, 手自動, 遠端, 現場, 模式 |
| `szSidFreq` | FREQ | hz, freq, frequency, spd, speed, fb, hzfb, rpm, 頻率, 轉速 |
| `szCidStartStop` | STARTSTOP | cmd, start, startstop, ss, onoff, runcmd, 啟停, 啟動, 開關 |
| `szCidFreqSet` | FREQSET | sp, hzsp, freqsp, speedsp, sethz, setfreq, 頻率設定, 設定頻率 |
| `szSidLoad`（冰機） | LOAD | load, pct, rla, 負載 |
| `szSidChwOut`（冰機） | CHWOUT | chws, chwout, chwst, lchwt, 冰水出水, 出水溫 |
| `szCidSetTemp`（冰機） | SETTEMP | chwsp, tsp, settemp, 設定溫度, 溫度設定 |
| `szSidWaterTemp`（冷卻塔） | WATERTEMP | cwout, cws, cwst, 出水溫, 冷卻水出水 |

- **選擇**: 與電表角色一樣寫在 `role-aliases.js`，v1 不做 UI 設定
- **理由**: 現有電表角色已如此運作一年沒人要求改；別名表是程式常數比較好測。`Designer/Templates` API（表格列範本）已存在，未來要開放編輯可直接把這張表搬進去，不需重新設計
- **放棄的選項**: 立即做設定頁 — 加一倍工時，v1 先驗證「自動帶入」本身有沒有打中痛點
- ⚠️ `出水溫` 同時出現在 CHWOUT 與 WATERTEMP：兩者不會同時出現在同一元件型別（冰機 vs 冷卻塔），依 `EQUIP_SLOT_ROLES[szWidgetType]` 只解析該型別需要的角色，不衝突
- ⚠️ `on` / `st` / `sp` / `fb` 這類短別名只在**字尾式**且前綴非空時成立（`CH1ON` ✓、`ON` 單獨 ✗），避免誤吃

### 決策 3：角色比對與候選規則（核心純函式）

```
resolveEquipRole(szName) → role | null:
  norm = 正規化（沿用 _normalizeHeaderText：全形→半形、去尾括號單位、小寫）
  (a) 整名命中別名 → role（吃 "運轉"、"故障"、"頻率設定" 這類純角色命名）
  (b) 以 - _ 空白 切 tokens，tokens ≥ 2 時尾 token 命中 → role（PUMP-1-RUN、冰水泵1_運轉）
  (c) 字尾式：單 token 且長度 > 別名長度時，依「別名長度由長到短」試 endsWith → role（CH1RUN、CWP1ALARM）
  皆未中 → null

buildEquipSuggestions(arrCandidates, szWidgetType):
  arrCandidates = picker 目前葉節點的點位集合（_pointInPickedDevice 篩出，與第二步清單完全相同）
  對 EQUIP_SLOT_ROLES[szWidgetType] 每個槽位：
        hits = 候選中 resolveEquipRole(p.szName) === 槽位角色 者
        point = hits[0] ?? null、bMulti = hits.length > 1
  回 { arrItems: [{ szSlotKey, szNameKey, szLabelKey, point, bMulti }], nHit, nTotal }
```

- **字尾式只對單 token 且前綴非空成立**：`ON` 單獨不算、`CH1ON` 才算，壓低 `on / st / sp / fb` 短別名誤判
- **只解析該元件型別需要的角色**：冰機不會去找 WATERTEMP、冷卻塔不會去找 CHWOUT，`出水溫` 同時在兩表也不衝突
- **多筆命中取第一筆並標示**：不猜哪個對，預覽看得到、事後能用單槽位重選修正
- **候選用 picker 同一條篩選**：Device 空白的站 = 整站當一台設備（多筆命中機率高但不報錯），補 Device 欄即自動變好

### 決策 4：取消 picker 仍建立未綁定元件（使用者 2026-10-14 確認）
- **選擇**: 設備模式新建時，picker 被關閉而未確認 → 仍 `createWidget` 建空元件
- **理由**: 保留「先排版、後綁點」的工作流；問一句不等於強迫回答
- **實作**: 監聽 `hidden.bs.modal`，以 `_equipConfirmed` 旗標區分確認 / 取消；確認路徑已建元件則不重複建

### 決策 5：重新指定設備 = 整台覆蓋（含清空未命中槽位）
- **選擇**: 屬性面板「重新指定設備」套用時，命中槽位覆蓋、未命中槽位清空；若原本有任一綁定，先 confirm
- **理由**: 元件是設備實例，換了設備就不該殘留上一台的點位；清空比留下錯的綁定安全（畫面上掛錯點位比空白更難發現）
- **放棄的選項**: 只填空槽位、不動已綁定 — 適合 v2 的「補綁」語意，不適合「換設備」語意；要補單一槽位仍有既有「重選」

### 決策 6：不新增任何 props key、不動儲存格式
- 所有帶入都寫進既有 `szSid* / szCid* / sz*Name` 槽位，ScadaPage 執行期零改動；舊設計 JSON 完全相容；複製頁面清空規則（`/^szSid|^szCid/`）自動涵蓋

## 實作步驟

1. [x] `role-aliases.js`：加 `_equipRoleAliases` + `_equipRoleMatch.resolveEquipRole`（整名 → 尾 token → 字尾式），自帶 `_normalize` 複用
2. [x] `equip-autobind.js`：純函式 `buildEquipSuggestions` + `EQUIP_SLOT_ROLES`（含每型別的 slot / nameKey / i18n label key / min-max 帶入規則）
3. [x] scratchpad `test-equip-autobind.js`（node）：三種命名風格（純角色 / 分隔符 / 字尾式）+ 多筆命中 + 短別名反例 + 型別只產生自己的槽位 + applyToProps + 電表 resolveRole 回歸 → **86 組全過**
4. [x] `picker.js`：抽 `_pointInPickedDevice(p)`（純重構；harness 驗 gauge 流程點位清單筆數不變）
5. [x] `picker.js`：設備模式（開啟 / 葉節點攔截 / 預覽 / 確認 / 取消回退）+ `equip-autobind.js` 的 `renderEquipPreview` / `applyToWidget`
6. [x] `widget-core.js` drop 四型別改開設備 picker；`prop-panel.js` 加「重新指定設備」按鈕（含 confirm 覆蓋）
7. [x] `Index.cshtml` 載入順序 + resx zh-TW / en + glossary
8. [x] `node --check` 全部改動 JS；`dotnet build ScadaEngine.Web` 0 錯誤（4 個既有警告與本次無關）
9. [x] 瀏覽器驗證 → **改走離線 Playwright harness**（不停生產服務、不登入、不碰 DB）：scratchpad `make-harness.js` 以真實 `Index.cshtml`（Localizer 代入 resx）+ 真實 designer JS / CSS + 假 `/Designer/Devices|Points` 組成 `file://` 頁，`test-equip-flow.py` 以 `DragEvent` 真拖放 → zh-TW / en 各 **54 組斷言全過、零 pageerror、零 i18n 漏 key**（流程涵蓋：拖放開 picker → 冰水泵1 預覽 5/6 → 返回 → 冰水泵2 6/6 確認 → props / szTitle / nFreqMax / toast / 面板按鈕；取消 + X 關閉仍建空元件；重新指定設備 confirm 取消不動 / 確定整台覆蓋含清空頻率槽；冰機 7 槽 + 多筆命中 tag；計算群組當設備 0 命中確認鍵停用；gauge 下鑽點位清單不變；pump 單槽位重選不變）。真站台手動驗證留給使用者（需 Engineer 登入）
10. [x] 文件同步 `docs/功能說明書_SCADA監控頁.md`
11. [x] 停下等使用者驗證 + 提版號 → 使用者「可以」、26.8.0 定案；並指示「一版本一 commit & push」→ 工作樹內另兩個未 commit 的 plan（26.6.0 install-root / 26.7.0 source-excel）先各自分批 commit（跨版本共用檔依 hunk 內容分類 stage），再 commit 本版 `534a4af`，三筆各自 push

## 已知風險 / 待釐清

- ✅ 拖放即問 Device、停在設備層直接帶入：使用者提議（v3）
- ✅ 取消仍建空元件（決策 4）使用者已確認
- ✅ 設備邊界採既有 Device 分群、不管開發環境命名，使用者已裁定
- ⚠️ `_renderFilteredPoints` 篩選抽函式是純重構但觸及所有 picker 路徑，步驟 4 先單獨驗一次既有行為再疊新功能
- ℹ️ 別名表（決策 2）使用者未另補字尾，以表列為準；之後要加只動 `role-aliases.js` 一張表
- ⚠️ Device 欄空白的站：`szDeviceLabel` 退到站號 / Coordinator 名 → 候選池 = 整站 → 同角色多筆命中機率高，面板會標示但不會自動選對；補 Device 欄是正解（Modbus 來源頁可熱編輯）
- ⚠️ 名稱不含任何角色字（如 `Modbus61`）無法比對，面板顯示「未找到」— 資料命名問題，v1 不猜
- ⚠️ 短別名誤判風險（`on` / `st` / `sp`）：以「字尾式僅在前綴非空」+「只解析該元件型別需要的角色」兩道限制壓低；node 測試加反例鎖住
- ⚠️ `role-aliases.js` 在 `Index.cshtml` 載入順序晚於 `picker.js`：既有設計是 runtime 才解析引用，`equip-autobind.js` 須放在 `role-aliases.js` **之後**、且所有呼叫都在事件內發生
- ⚠️ 本機為生產機：驗證需起開發 Web 實例，依 CLAUDE.md 規則停 / 啟 `JoularisWeb` 並在回覆明講中斷窗口

## 測試策略

- [x] **本次是否動到核心計算 / 關鍵邏輯？**
  - [x] 是 → 純函式以 **node 斷言腳本**鎖住（repo 無 JS 測試框架，比照 2026-09-15 role-match plan 做法，腳本放 scratchpad、結果寫進度日誌）。要鎖的規則：
    - `resolveEquipRole`：`運轉`→RUN、`故障`→FAULT、`頻率設定`→FREQSET、`PUMP-1-RUN`→RUN、`PUMP-1-HZSP`→FREQSET、`冰水泵1_運轉`→RUN、`CH1RUN`→RUN、`CH1MOA`→MODE、`CWP1ALARM`→FAULT、`V` / `kWhTotal` / `Modbus61` / `ON` / `SP`→null
    - `buildEquipSuggestions`：只在傳入候選內找（呼叫端已依葉節點篩）；同角色兩筆 → `bMulti=true` 取第一筆；冰機型別不產生 WATERTEMP 槽位、冷卻塔不產生 CHWOUT；`nHit` / `nTotal` 正確
    - 既有 `_roleMatch.resolveRole` 回歸：沿用 09-15 的 20 組斷言原樣再跑一次，結果不變
- [ ] 是否動到 DB 查詢 / 寫入或跨模組資料流？ 否（純前端、不動 API、不動儲存格式）
- [ ] 是否動到關鍵使用者路徑？ 是 → **E2E（Playwright）**：☑ 跳過（Designer 既有無 E2E；以手動驗證替代）
- [ ] 手動驗證（瀏覽器操作）：
  0. 前置：在 Modbus 來源頁把測試站的點位 Device 欄填成 `冰水泵1` / `冰水泵2`（各含運轉 / 故障 / 頻率 / 啟停 / 頻率設定）
  1. Designer 拖泵浦 → picker 標題「選擇設備」→ 展開站 → 點 `冰水泵1` 葉節點 → 預覽五中一缺（手自動）→ 確認 → 畫布泵浦 title=冰水泵1、屬性面板五槽位已填、toast 5/6
  2. 預覽畫面「返回」改點 `冰水泵2` → 預覽換成泵2 的點位 → 確認
  3. 拖泵浦後直接關 picker → 仍有一台未綁定泵浦
  4. 屬性面板「重新指定設備」→ 選 `冰水泵2` → confirm 覆蓋文字含「冰水泵2」→ 確定 → 五槽位換成泵2、原本手動補的手自動槽位被清空
  5. 對 gauge / DI / 表格 cell 開 picker → 仍走到點位清單，行為不變
  4. 取消 picker 後拖放仍建立未綁定泵浦
  6. 儲存 → 重新載入 Designer → 綁定存在；/ScadaPage 即時值正常
  7. 切 en 介面：標題 / 按鈕 / 預覽 / toast 全英文
- [x] 收尾：`dotnet test` 全綠（840 通過 / 0 失敗；本次不新增 C# 測試）

> 手動驗證步驟 1–5 已由離線 Playwright harness 等價覆蓋（見實作步驟 9）；步驟 6（儲存 → 重載 → /ScadaPage 即時值）與步驟 7（真站台 en 介面）需 Engineer 登入真站台，留給使用者。

## 文件同步

- [x] 更新 `docs/功能說明書_SCADA監控頁.md`（§7.4 新增「設備級自動綁定」小節；§3 檔案結構只列 ScadaPage 檔、Designer 檔本就不在其中，故未加）
- [x] `docs/i18n-glossary.md` 新詞登錄
- [x] 不需更新 `CLAUDE.md`（未引入新架構模式）

---

## 進度日誌

### 2026-10-14
- 討論「一般 SCADA 怎麼做」→ 結論：業界靠 UDT + 參數化 Template 把綁定單位從點位提升到設備；本專案先做第一步「設備級綁定」
- 讀碼確認：點位 API 無讀寫旗標、CID 與 SID 同一字串；開發 Modbus.json 命名為 `CH1RUN` 式無分隔符且 Device 欄全空 → 決策 3 的字尾式推斷由此而來
- plan 寫完，提出兩問（別名表 / 決策 4）
- 使用者回覆：「直接抓 Device 就好，開發環境不用管」「4 照你提議」→ 刪除名稱前綴推斷整套規則，設備邊界改為 `szDeviceLabel`；決策 4 定案
- 使用者提議：「元件拉到畫面時直接問這是什麼 Device，是的話直接帶進去」→ v3：picker 停在設備層 + 就地預覽 + 確認整台帶入；刪掉「先選一個點位」與獨立建議 modal；屬性面板按鈕改「重新指定設備」（整台覆蓋）
- 使用者「執行」→ 開工。依序完成 role-aliases / equip-autobind / picker / widget-core / prop-panel / cshtml / resx / css / docs / glossary；`node --check` 全過、node 斷言 86 組全過、`dotnet build ScadaEngine.Web` 0 錯誤
- 瀏覽器驗證：真站台需 Engineer 登入且本機為生產 DB（不能為了測試改 Device 欄），改做離線 Playwright harness（真實前端 + 假 API），zh-TW / en 各 54 組斷言全過；過程抓到一個渲染 bug（「未找到」列 title 屬性塞了 HTML 導致多出 `">`），已修
- **停下等使用者驗證 + 版號確認（26.7.0 → 26.8.0）**

## 討論過程與成本記錄

### 討論過程摘要

- 使用者：同事反映 Designer 客製化程度高但一個個點很累，一般 SCADA 怎麼做？
- Claude：對照 Ignition / WinCC / Citect / iFIX 四層做法（結構化 Tag、參數化符號、參數化頁面、拖放建元件），指出版面在所有產品都是手拉、被自動化的是綁定與實例化；盤點本專案已有 Device 分群與角色別名半套零件；建議三步順序，第一步「設備級綁定」投入最小
- 使用者：「好」→ 依 plan 規則（跨多檔 + 設計決策）先寫本 plan
- 讀碼發現開發環境命名無分隔符、Device 欄空 → 初版 plan 設計了「名稱字尾反推設備前綴 + 精確前綴比對」
- Claude 提問：(1) 別名表是否要補慣用字尾？(2) 拖放先開 picker 是否接受？
- 使用者：「直接抓 Device 就好，開發環境不用管他」「4 照你提議」→ plan 刪掉前綴推斷（決策 1 / 3 改寫、驗收與測試案例改以 Device 分群為準），決策 4 定案；別名表未補字尾，以原表為準
- 使用者：「要不要元件拉到畫面時直接問這是什麼 Device，是的話就直接帶進去？」→ Claude 確認 picker 設備層葉節點即可承載 → plan v3：拖放即問 Device、停在設備層、預覽後整台帶入；v2 的「選任一點位反推」與獨立建議面板整段移除，流程少一層

### 成本記錄

| 階段 | 模型 | Input | Output | Cache Read | Cache Write | 備註 |
|------|------|-------|--------|------------|-------------|------|
| Plan 撰寫（含 v2 / v3 修訂） | claude-fable-5-1 | 1132 | 93,234 | 4,151,282 | 305,285 | 2026-10-14 plan v3 改完時自 session transcript（41 則 assistant）加總 |
| 實作 + 離線 Playwright 驗證 + 文件（本對話，截至回填 plan 時） | claude-fable-5-1 | 4,076 | 563,216 | 23,754,867 | 1,013,904 | 2026-10-14 自 session `341cf1ff-…jsonl`（133 則 assistant）加總；plan 撰寫在另一 session `2167a581-…` |
| 本對話全程（實作 + 驗證 + 三版本分批 commit/push + 歸檔，取代上一列） | claude-fable-5-1 | 5,464 | 663,905 | 39,930,695 | 1,395,917 | 2026-10-14 歸檔前自 session `341cf1ff-…jsonl`（182 則 assistant）加總；含替 26.6.0 / 26.7.0 分批 commit 的工作，無法拆分 |

### Archive 總結（搬進 _archive 前回填）

- **總 token**（plan 撰寫 + 本對話全程）：input 6,596 / output 757,139 / cache read 44,081,977 / cache write 1,701,202
- **模型**：claude-fable-5-1（兩階段皆是）
- **API 費用（當下牌價，附計算式）**：牌價查詢日 2026-10-14，來源 https://platform.claude.com/docs/en/about-claude/pricing（WebFetch）
  - 牌價（Claude Fable 5.1，每 1M token）：input $10、output $50、cache read $0.25、cache write 1h $20（5m $12.50）；本 session 使用 1h cache TTL，以 $20 計
  - 計算式：6,596×10 + 757,139×50 + 44,081,977×0.25 + 1,701,202×20 ÷ 1e6 = 0.066 + 37.857 + 11.020 + 34.024 = **≈ $82.97 美元**（若 cache write 按 5m $12.50 計則 ≈ $70.21）

## 完成後補充

### 實際做法 vs 原計畫差異
- 預覽標題：plan 寫「設備：X」，實作維持標題「選擇設備」、設備名顯示在既有 `ppDeviceName` tag（同一資訊、少一個 i18n key）
- 多補三個 i18n key（`preview_title` / `preview_hint` / `no_candidates`）與兩個 CSS class（`.rt-multi` / `.rt-detected-none`），預覽沿用列範本面板 `.row-template-body` 樣式不另寫一套
- `createWidget` 改回傳 `el`（原本無回傳），確認路徑直接拿到新元件套用，不依賴 `selectedEl` 時序
- 葉節點攔截除 plan 列的四個，另涵蓋 `_showCalcPointsFlat`（無群組時的整池計算點位）與 `showDemandPointStep`（需量），讓「凡能到第二步的節點都能當設備」成立
- 確認鍵只在命中 > 0 時啟用（plan 寫「確認鍵啟用」）：0 命中確認只會改 szTitle 或清空全部，沒有意義
- `_ensurePickerData` 失敗（API 掛）時設備模式直接建空元件，不卡人（plan 未定義此分支）
- 瀏覽器驗證改離線 harness（見實作步驟 9），未停生產服務

### 踩到的雷
- `renderEquipPreview` 把含 HTML 的字串塞進 `title="…"` 屬性（未命中列的 `<span class="rt-na">—</span>`），內含雙引號提前關閉屬性，畫面多出 `">` 字樣 —— 只有真的在瀏覽器渲染才看得到，node 純函式測試抓不到；**UI render 一定要截圖看一眼**
- Playwright 對 `drop` 的模擬：`page.dispatch_event` 不好帶 `DataTransfer`，改在 `page.evaluate` 內 `new DragEvent('drop', { dataTransfer: new DataTransfer() })` 直接派發，Chromium 可行
- 此機 `python3` 別名（Windows Store 轉址）在 Bash 下回 exit 49 無輸出，要用 `python`

### 對 memory / CLAUDE.md 的更新建議
- 「Designer 前端改動的瀏覽器驗證」可沿用本次 harness 做法（`make-harness.js` + Playwright 互動斷言），不必停生產服務、不必登入；若日後常用，值得搬進 `.claude/skills/mock-render/recipes/`（目前留在 scratchpad）
