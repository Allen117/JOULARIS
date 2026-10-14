# Designer 設備範本庫：元件群組存成範本、拖出時只問「哪台設備」

**狀態**: 進行中 <!-- 進行中 | 已完成 | 廢棄 -->
**建立**: 2026-10-14
**最後更新**: 2026-10-14
**相關 commit**: P1 = `534a4af`（26.8.0，已完成）；P2 / P3 完成後回填
**承接**: `_archive/2026-10-14-designer-equipment-autobind.md`（P1 地基已由該 plan 實作完成並歸檔；本 plan 只做 P2 / P3，P1 段落保留作為介面依據）

---

## 目標 & 背景

同事反映 Designer 綁點位很累：成本 = 設備數 × 每台點位數 × 每次點選步驟。
討論結論：主流 SCADA（Ignition UDT + Template、WinCC Faceplate、Citect Genie）一律把**「它是哪台設備」與「它長什麼樣」拆開**——
長相永遠手畫，被自動化的只有綁定與實例化。業主堅持自己的畫風是常態，所以解法不是做更多內建元件，而是：

> 任何一群元件（內建泵浦、自繪圖片、即時值、DI 燈、控制鈕、管路、文字…）畫一次、綁一台真設備 → **存成範本**（綁定以**角色**保存，不存 SID）
> → 之後從工具箱拖範本出來，picker 只問「**這是哪台 Device？**」→ 整組元件展開、所有角色自動對上該設備的點位。

第一性原理拆成三件獨立可驗證的事，各自一個 commit：

| 階段 | 做什麼 | 解決誰的痛 |
|---|---|---|
| **P1 地基 ✅ 已完成（534a4af / 26.8.0）** | 設備角色別名表 + picker「設備模式」（停在設備層、預覽命中、整台帶入）；套在內建泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機四種元件 | 用內建元件的人，立刻一台一次搞定 |
| **P2 範本庫** | 選取元件群組 → 存為範本（綁定→角色，審核後存 DB）；工具箱「設備範本」分頁；拖範本 → 設備模式 → 展開實例 | 業主自繪畫風的人，畫一次、其餘全是實例 |
| **P3 實例重綁** | 實例保留角色與群組標記 → 右鍵「重新指定設備」整組換；頁面複製（清綁定）後每組一鍵重綁 | 新增同型設備 = 複製整頁 + 每組選一台 |

**不做**（明確排除）：範本與實例的「活連結」（改範本自動更新所有實例）、參數化頁面、表格儲存格的角色綁定（v1 限制，見風險）。

## 驗收條件

### P1 地基（已於 534a4af 完成，列此供 P2 / P3 對照介面）
- [x] 拖泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機到畫布 → picker 標題「選擇設備」，設備清單點到**葉節點**（單站號 Coordinator / 多站號子站 / Device 子群 / 未分群桶 / DB 群組 / 計算群組）不進點位清單，改顯示**命中預覽**（每槽位 → 點位名 / 未找到 / 多筆命中取第一筆；頂部「命中 N / M」）
- [x] 確認 → 建立元件、命中槽位寫入 SID 與名稱、`szTitle` = 設備標籤、頻率 / 頻率設定 / 負載 min-max 依點位帶入、toast「已帶入 N / M」；未命中槽位留空由既有「重選」補
- [x] 關閉 picker 不確認 → 仍建立未綁定元件（使用者已確認）
- [x] 屬性面板「重新指定設備」→ 同流程；已有綁定先 confirm「將以『X』覆蓋，未命中槽位清空」
- [x] 其他元件型別 picker 行為零變化；電表角色 `resolveRole` 回傳零變化；`isDesignerBindingKey` 清空範圍不受影響

**P1 已出貨的介面（P2 / P3 直接沿用，不重做）**：
- `role-aliases.js`：`window._equipRoleAliases`（RUN / FAULT / MODE / FREQ / STARTSTOP / FREQSET / LOAD / CHWOUT / SETTEMP / WATERTEMP）+ `window._equipRoleMatch.resolveEquipRole(szName, arrRoles?)`（可傳角色白名單）
- `designer/equip-autobind.js`：`window._equipAutobind = { EQUIP_TYPES, EQUIP_SLOT_ROLES, isEquipType, buildEquipSuggestions(arrCandidates, szWidgetType), applyToProps(props, result, szDeviceLabel, bClearMissing), applyToWidget(el, …), hasAnyBinding, renderEquipPreview(container, result) }`
- `picker.js`：`_equipDeviceMode` / `_equipConfirmed` / `openEquipDevicePicker(x, y, szType)` / `rerouteEquipDevice()` / `_showEquipPreview()` / `_confirmEquipPick()` / `_pointInPickedDevice(p)`；葉節點攔截已涵蓋 Modbus 整站 / 子站 / Device 子群 / 未分群 / DB 群組 / 計算群組 / 整池計算 / 需量；hidden 事件：取消 → 建空元件
- `widget-core.js`：`createWidget` 回傳 `el`；drop 四型別走 `openEquipDevicePicker`
- 預覽沿用列範本面板 `.row-template-body` 樣式；i18n key 前綴 `designer.equip.*`

### P2 範本庫
- [ ] 畫布多選（Ctrl / Shift / 框選）≥ 1 個元件 → 右鍵或工具列「存為設備範本」→ 審核對話框：範本名稱、分類；逐列列出「元件 | 綁定槽位 | 目前綁定點位 | 推斷角色（下拉可改 / 可選『不綁』）」；推斷不到的列標紅；文字元件內容等於設備標籤時建議改成 `{device}` 佔位
- [ ] 存檔 → 寫入 DB `ScadaDesignTemplate`；範本內所有 `szSid*` / `szCid*` / `sz*Name` 清空、角色寫進各元件 `props.objRoleBind`；元件座標改為相對群組左上角
- [ ] 工具箱新增「設備範本」區塊：依分類列出（內建 + 使用者），可拖；旁有「管理」→ 重新命名 / 刪除（內建鎖定，只能「另存為」）
- [ ] 拖範本到畫布 → 設備模式 picker → 預覽（依範本內**去重後的角色**列命中）→ 確認 → 以落點為左上角展開全部元件、角色解析為 SID、`{device}` 替換、每個元件打上 `szTplInstance`（同一實例同 id）/ `szTplKey` / `szTplDevice`；未命中角色留空並 toast 列出
- [ ] 取消 picker → 不建立任何元件（範本沒有「空實例」的意義；與 P1 內建元件行為刻意不同）
- [ ] 內建預設範本（見決策 7，**待使用者確認清單**）於 Web 啟動時依 `szKey` upsert 進 DB；刪不掉、改名不了，可另存
- [ ] 儲存設計 → 重新載入 → 實例元件照常；`/ScadaPage` 渲染零改動（實例就是普通元件 + 幾個不影響渲染的 props）
- [ ] 複製頁面（樹層級）後：綁定被清空但 `objRoleBind` / `szTplInstance` / `szTplKey` 保留、`szTplDevice` 清空

### P3 實例重綁
- [ ] 畫布上右鍵實例內任一元件 → 「選取整組」（同 `szTplInstance` 全選）/ 「重新指定設備」→ 設備模式 picker → 預覽 → 確認 → 整組依 `objRoleBind` 重新解析（命中覆蓋、未命中清空、`{device}` 文字重算）
- [ ] 頁面複製後的實例：右鍵「重新指定設備」直接可用（靠保留的角色，不靠舊點位名）
- [ ] 非實例元件（手放的）右鍵無此二項；混選實例 + 非實例 → 只處理實例元件並提示跳過數
- [ ] 實例元件被個別刪除 / 移動 / 改綁後，其餘元件不受影響（實例只是標記，不是容器）

### 共同
- [ ] zh-TW / en resx 同步；glossary 新詞登錄；`node --check` 全部新改 JS；scratchpad node 斷言全過；`dotnet test` 全綠
- [ ] `docs/功能說明書_SCADA監控頁.md` 新增「設備範本與實例」章節；`docs/架構.md` 資料表對照補 `ScadaDesignTemplate`

## 檔案異動清單

### 新增

- [ ] `ScadaEngine.Web/wwwroot/js/designer/equip-template.js` — P2/P3：
  - 純函式：`inferRoleBindings(arrWidgetStates, fnResolvePointName)`（綁定→角色推斷，供審核對話框）、`buildTemplatePayload(arrWidgetStates, bbox)`（清 SID、相對座標）、`resolveTemplate(tpl, arrCandidates, szDeviceLabel)`（角色→SID、`{device}` 替換、回 `{ arrWidgetStates, arrUnresolved }`）、`collectTemplateRoles(tpl)`（去重角色清單，供預覽）
  - UI：存為範本審核對話框、工具箱範本區塊 render、拖放落點展開、實例右鍵項目
- [ ] `ScadaEngine.Web/Features/Designer/Models/DesignTemplateDtos.cs` — 範本 DTO（`szKey / szName / szCategory / bBuiltIn / nW / nH / szWidgetStateJson`）
- [ ] `ScadaEngine.Web/Features/Designer/Templates/builtin/*.json` — 內建範本種子（一檔一範本，Content 複製到輸出目錄）
- [ ] `ScadaEngine.Web/Services/DesignTemplateSeeder.cs` — 啟動時讀種子目錄、依 `szKey` upsert（`IHostedService` 或沿用現有 schema-sync 啟動點）
- [ ] `ScadaEngine.Engine/Models/ScadaDesignTemplateModel.cs` — 對應資料表
- [ ] `ScadaEngine.Tests/Designer/DesignTemplateSeedTests.cs` — 種子 JSON 可解析、`szKey` 唯一、每個 `objRoleBind` 的槽位 key 都是 `szSid*`/`szCid*`、座標非負
- [ ] `ScadaEngine.Tests/Designer/DesignTemplateRepositoryTests.cs` — 若既有整合測試基礎可用：upsert / list / delete（內建不可刪）

### 修改

- [ ] `ScadaEngine.Web/wwwroot/js/role-aliases.js` — `_equipRoleAliases` **擴充**新角色（CHWIN / CWIN / SAT / RAT / TEMP / HUM / CO2 / PRESS / FLOW，見決策 2）；既有 10 個角色與 `_roleAliases` / `_roleMatch` 不動
- [ ] `ScadaEngine.Web/wwwroot/js/designer/equip-autobind.js` — 不改槽位邏輯；若 `renderEquipPreview` 可參數化列標籤則讓範本模式共用，否則範本預覽在 `equip-template.js` 自 render（同樣沿用 `.row-template-body`）
- [ ] `ScadaEngine.Web/wwwroot/js/designer/picker.js` — 設備模式**泛化**：`openEquipDevicePicker` 之外加 `openDevicePickerForTemplate(x, y, szTplKey)` 與 `openDevicePickerForInstance(szInstanceId)`；`_showEquipPreview` / `_confirmEquipPick` 依模式分派（設備元件 / 範本 / 實例），候選一律 `_pointInPickedDevice`；範本與實例模式的取消**不建元件**
- [ ] `ScadaEngine.Web/wwwroot/js/designer/widget-core.js` — drop：`templateKey` 走範本模式；`createWidgetFromState(ws, nX, nY)`（展開實例用，與 `pasteWidgets` 共用同一條建構路徑）
- [ ] `ScadaEngine.Web/wwwroot/js/designer/widget-defs.js` — `DESIGNER_BINDING_DELETE_KEYS` 加 `szTplDevice`（頁面複製時清）；`auditDesignerBindingKeys` 白名單 `objRoleBind` / `szTplInstance` / `szTplKey` / `szTplDevice`
- [ ] `ScadaEngine.Web/wwwroot/js/designer/prop-panel.js` — 實例元件頂部顯示「範本：X / 設備：Y」資訊列 + 「整組重新指定設備」按鈕（P1 的單元件按鈕保留）
- [ ] `ScadaEngine.Web/wwwroot/js/designer/ctx-menu.js` — 畫布右鍵加「存為設備範本」（有選取時）、「選取整組」「重新指定設備」（實例時）
- [ ] `ScadaEngine.Web/wwwroot/js/designer/page-clipboard.js` — 確認清空規則涵蓋 `szTplDevice`（走 `DESIGNER_BINDING_DELETE_KEYS` 即自動涵蓋，驗證即可）
- [ ] `ScadaEngine.Web/Features/Designer/Views/Index.cshtml` — 工具箱「設備範本」區塊 + 管理 modal + 存為範本審核 modal；載入 `equip-autobind.js` / `equip-template.js`
- [ ] `ScadaEngine.Web/Features/Designer/Controllers/DesignerController.cs` — `GET /Designer/EquipTemplates`、`POST /Designer/EquipTemplates`（upsert，使用者範本）、`DELETE /Designer/EquipTemplates/{key}`（內建拒絕）；皆 `Roles="Engineer"`
- [ ] `ScadaEngine.Engine/Data/Interfaces/IDataRepository.cs` + `SqlServerDataRepository.cs` — 範本 CRUD 三方法
- [ ] `ScadaEngine.Engine/DatabaseSchema/DatabaseSchema.json` — 新表 `ScadaDesignTemplate`
- [ ] `ScadaEngine.Web/ScadaEngine.Web.csproj` — 種子 JSON `CopyToOutputDirectory`
- [ ] `ScadaEngine.Web/Resources/Features.Designer.Views.Index.resx` + `.en.resx` — 新 key（`designer.equip.*`、`designer.tpl.*`，含內建範本名稱 `designer.tpl.builtin.{key}`）
- [ ] `docs/功能說明書_SCADA監控頁.md`、`docs/架構.md`、`docs/i18n-glossary.md`

### 資料庫

- [ ] 新表 `ScadaDesignTemplate`：`Id` int identity PK、`TemplateKey` nvarchar(100) unique（內建 `builtin.{name}`、使用者 `user.{guid}`）、`Name` nvarchar(100)、`Category` nvarchar(50)、`IsBuiltIn` bit、`Width` int、`Height` int、`WidgetStateJson` nvarchar(max)、`UpdatedAt` datetime2
- [ ] `DatabaseSchema.json` 加表 → Engine / Web 啟動自動建（只加不減）

## 關鍵設計決策

### 決策 1：一台設備 = picker 設備清單的一個葉節點（使用者 2026-10-14 裁定：直接抓 Device）
- **選擇**: 候選點位 = 該葉節點在第二步會列出的那組（`_renderFilteredPoints` 篩選抽成 `_pointInPickedDevice`）。Modbus 站 / 子站 / Device 子群 / DB 群組 / 計算群組全部自動支援
- **理由**: 零新資料、零新 API、與表格列範本的「同設備」定義一致；Device 填得好不好直接反映在命中率，是運維責任
- **放棄**: 從點位名反推設備前綴（只為 Device 空白的開發環境）；獨立設備選擇 modal（重複清單邏輯）

### 決策 2：角色 = 綁定的唯一抽象；別名表集中在 `role-aliases.js`，v1 不做設定頁
角色分三組，同一張表：

| 組 | 角色 | 別名（lower-case；token 式與字尾式共用）|
|---|---|---|
| 設備狀態 | RUN | run, running, on, status, st, 運轉, 運行 |
| | FAULT | alarm, alm, fault, flt, trip, err, error, fail, 故障, 警報, 跳脫 |
| | MODE | moa, hoa, mode, auto, am, ma, remote, rem, local, loc, 手自動, 遠端, 現場, 模式 |
| | FREQ | hz, freq, frequency, spd, speed, fb, hzfb, rpm, 頻率, 轉速 |
| | LOAD | load, pct, rla, 負載 |
| 設備控制 | STARTSTOP | cmd, start, startstop, ss, onoff, runcmd, 啟停, 啟動, 開關 |
| | FREQSET | sp, hzsp, freqsp, speedsp, sethz, setfreq, 頻率設定, 設定頻率 |
| | SETTEMP | chwsp, tsp, settemp, 設定溫度, 溫度設定 |
| 溫濕度 / 製程 | CHWOUT（已出貨） | chws, chwout, chwst, lchwt, 冰水出水, 出水溫 |
| | WATERTEMP（已出貨；冷卻水出水） | cwout, cws, cwst, 出水溫, 冷卻水出水 |
| | **CHWIN**（新） | chwr, chwin, chwrt, echwt, 冰水回水, 冰水回水溫 |
| | **CWIN**（新） | cwr, cwin, cwrt, ecwt, 冷卻水回水, 回水溫 |
| | **SAT**（新） | sat, sa_t, supplytemp, 送風溫度, 送風溫 |
| | **RAT**（新） | rat, ra_t, returntemp, 回風溫度, 回風溫 |
| | **TEMP**（新） | temp, temperature, t, 溫度 |
| | **HUM**（新） | hum, humidity, rh, 濕度, 相對濕度 |
| | **CO2**（新） | co2, 二氧化碳 |
| | **PRESS**（新） | press, pressure, pa, kpa, 壓力, 壓差 |
| | **FLOW**（新） | flow, gpm, lpm, cmh, 流量 |
| 電力（既有） | V / A / KW / KVA / KVAr / PF / KWH / Hz / KWHr | `_roleAliases` 原表不動 |

- 比對：已出貨的 `resolveEquipRole(szName, arrRoles)` 整名 → 尾 token → 字尾式；短別名（`on / st / sp / fb`，新增 `t`）只在字尾式且前綴非空時成立
- **角色白名單**：設備元件只解析該型別的槽位角色；範本模式只解析**該範本內出現的角色**（`collectTemplateRoles`）→ `出水溫` 在冰機範本落 CHWOUT、在冷卻塔範本落 WATERTEMP，不衝突；範本同時含兩者時預覽標多筆命中，由人修
- 既有 `_EQUIP_SHORT_ALIASES` 集合加 `t`（TEMP 別名）
- 範本審核對話框用同一張表推斷，推斷錯可在對話框改；**存進範本的是角色，不是推斷過程**
- **放棄**: 做別名設定頁 — 電表角色寫死一年沒人要改；之後要開放，`Designer/Templates` API（表格列範本）可直接擴充

### 決策 3：範本 = 清掉 SID、改存角色的 widget 陣列；實例 = 展開後的普通元件（ScadaPage 零改動）
```
範本 JSON（DB WidgetStateJson）
[ { szType:'pump', nX:0, nY:24, nW:130, nH:120,
    props:{ ...其他外觀 props..., szSidRun:'', szRunName:'', ..., objRoleBind:{ szSidRun:'RUN', szSidFault:'FAULT', szSidMode:'MODE', szSidFreq:'FREQ', szCidStartStop:'STARTSTOP', szCidFreqSet:'FREQSET' } } },
  { szType:'text', nX:0, nY:0, props:{ szText:'{device}' } } ]

實例元件（展開到頁面 arrWidgetState）
{ szType:'pump', nX:420, nY:184, props:{ szSidRun:'65793-S2', szRunName:'冰水泵1 / RUN', ...,
    objRoleBind:{...同上...}, szTplInstance:'ti_1728900000_3', szTplKey:'builtin.pump', szTplDevice:'冰水泵1' } }
```
- **為什麼展開而不是「範本元件」**：ScadaPage 的 1 秒渲染迴圈依 `szType` 分派，加一種容器型別要動 render.js 與所有 widget-*.js；展開後執行期完全不知道範本存在
- **為什麼實例上保留 `objRoleBind`**：P3「重新指定設備」靠它，不靠舊點位名反推；頁面複製清空 SID 後角色仍在，複製整頁 → 每組重綁就成立
- **標記 key 命名**避開 `isDesignerBindingKey`（`/^szSid|^szCid/`）與 `auditDesignerBindingKeys` 的 `/Sid|Cid|CircuitId|ScheduleId/i`；`szTplDevice` 加進 `DESIGNER_BINDING_DELETE_KEYS` 讓頁面複製順手清掉
- **文字 `{device}` 佔位**：展開與重綁時替換成設備標籤；審核對話框對「內容恰等於設備標籤」的文字元件自動建議
- **放棄**: 活連結（改範本更新所有實例）— 需要實例持有範本版本 + 差異合併，與「業主自己畫」的手改需求衝突；v1 不做，實例改了就是改了

### 決策 4：範本存 DB，不存檔案；內建範本以種子 JSON 隨 Web 出貨、啟動 upsert
- **選擇**: 新表 `ScadaDesignTemplate`，走 `DatabaseSchema.json` 自動建表；內建種子放 `Features/Designer/Templates/builtin/*.json`，啟動時依 `szKey` upsert（內建永遠以出貨版為準）
- **理由**: 設計本身在 DB，範本跟著 DB 一起被每週備份與升級還原；表格列範本的檔案做法是單一全域設定，範本庫是會長大的使用者資料，性質不同
- **內建鎖定**: `IsBuiltIn=1` 不可刪、不可改名，可「另存為」成使用者範本再改；升級時內建被覆蓋不會動到使用者範本
- **放棄**: 範本存 `Setting/*.json` — 升級時 Install.bat 的設定備份還原規則是針對已知設定檔，新增一類檔案要改部署腳本

### 決策 5：存為範本走「從實例學習」，不要求使用者先懂角色
- **選擇**: 使用者畫一群、綁一台真設備（任何方式綁都行）→ 選取 → 存為範本 → 系統把每個綁定的點位名丟進別名表推斷角色 → 審核對話框逐列確認 / 修正 → 存
- **理由**: 業主自繪的流程本來就是「先做第一台」；推斷只是把他已經做過的事轉成角色，推錯當場改。比「先在每個元件上填角色再存」少一次抽象理解
- **沒綁點位的元件**（純裝飾圖、管路、文字）：原樣進範本，不列在審核表（文字除外，見 `{device}` 建議）
- **表格元件**：v1 原樣進範本但**儲存格綁定會被清空且不轉角色**，審核對話框明示；原因見風險段

### 決策 6：P1 內建設備元件取消 picker 仍建空元件；P2 範本取消則不建（刻意不同）
- 內建元件有「先排版後綁點」的工作流；範本沒有「不知道是哪台的實例」這種東西，取消就是沒要放

### 決策 7：內建預設範本清單（**待使用者確認**，確認後才寫種子）

| # | szKey | 名稱 | 分類 | 組成（元件 → 角色） |
|---|---|---|---|---|
| 1 | `builtin.pump` | 水泵（內建圖） | 冰水系統 | 文字 `{device}`；**pump** RUN / FAULT / MODE / FREQ / STARTSTOP / FREQSET |
| 2 | `builtin.pump_panel` | 水泵面板（自繪友善） | 冰水系統 | 文字 `{device}`；**image**（佔位圖，業主換自己的圖；動畫條件綁 RUN）；diPoint FAULT；realtimeValue FREQ；controlBtn STARTSTOP；aoPoint FREQSET |
| 3 | `builtin.cooling_tower` | 冷卻水塔（內建圖） | 冰水系統 | 文字 `{device}`；**coolingTower** RUN / FAULT / MODE / FREQ / WATERTEMP / STARTSTOP / FREQSET |
| 4 | `builtin.cooling_tower_anim` | 冷卻水塔（動畫圖） | 冰水系統 | 文字 `{device}`；**image** 內建圖庫 CT（RUN）；diPoint FAULT；realtimeValue WATERTEMP（出水）；realtimeValue CWIN（回水）；controlBtn STARTSTOP |
| 5 | `builtin.chiller` | 冰機（內建圖） | 冰水系統 | 文字 `{device}`；**chiller** RUN / FAULT / MODE / LOAD / CHWOUT / STARTSTOP / SETTEMP |
| 6 | `builtin.chiller_panel` | 冰機數據卡 | 冰水系統 | 文字 `{device}`；diPoint RUN；diPoint FAULT；diPoint MODE（遠端/現場）；realtimeValue LOAD / CHWOUT / CHWIN / WATERTEMP（冷卻水出）/ CWIN；aoPoint SETTEMP；controlBtn STARTSTOP |
| 7 | `builtin.ahu` | 空調箱 | 空調 | 文字 `{device}`；**ahuFan** RUN / FAULT / MODE / FREQ / STARTSTOP / FREQSET；realtimeValue SAT / RAT；aoPoint SETTEMP |
| 8 | `builtin.fan` | 風機（動畫圖） | 空調 | 文字 `{device}`；**image** 內建圖庫 Fan（RUN）；diPoint FAULT；controlBtn STARTSTOP |
| 9 | `builtin.meter` | 電表數據列 | 電力 | 文字 `{device}`；realtimeValue V / A / KW / PF / KWH（沿用既有電表角色） |
| 10 | `builtin.temp_hum` | 溫濕度 | 環境 | 文字 `{device}`；realtimeValue TEMP / HUM |

- 每個範本尺寸控制在約 200×160（內建圖款）/ 260×200（面板款），落點為左上角
- 名稱走 i18n key `designer.tpl.builtin.{key}`，使用者範本存自由文字
- 可增刪：使用者確認時直接說「砍 4、6」或「加 XX」即可

### 決策 8：P2 / P3 各一個 commit、各自提版號
- P1 已出 `26.8.0`（534a4af）；P2 範本庫 = 新功能 → `26.9.0`；P3 實例重綁 → `26.10.0`（每階段完成停下驗證、確認版號後 commit）
- 若使用者偏好 P2 + P3 合併 → 單一 `26.9.0`，plan 註明即可

## 實作步驟

### P1 地基 ✅（534a4af，另一對話完成；node 斷言 86 組 + 離線 Playwright 108 組全過）
1. [x] `role-aliases.js` 設備角色 + `resolveEquipRole`
2. [x] `equip-autobind.js`
3. [x] node 斷言
4. [x] `picker.js` 設備模式 + `_pointInPickedDevice`
5. [x] `widget-core.js` / `prop-panel.js` / resx / css / docs / glossary
6. [x] 驗證 → 26.8.0 commit / push

### P2 範本庫
7. [ ] `DatabaseSchema.json` 加表；`ScadaDesignTemplateModel`；`IDataRepository` + `SqlServerDataRepository` 三方法
8. [ ] `DesignTemplateDtos` + Controller 三 API + `DesignTemplateSeeder`（啟動 upsert）
9. [ ] 內建種子 JSON（依決策 7 確認後的清單手寫；每檔跑 `DesignTemplateSeedTests`）
9a. [ ] `role-aliases.js` 擴充新角色 + `_EQUIP_SHORT_ALIASES` 加 `t`；node 斷言補新角色案例（既有 86 組重跑不變）
10. [ ] `equip-template.js` 純函式：`inferRoleBindings` / `buildTemplatePayload` / `resolveTemplate` / `collectTemplateRoles` + node 斷言（含：角色去重、`{device}` 替換、未命中清單、相對座標、表格儲存格清空）
11. [ ] `widget-defs.js` 白名單 + `szTplDevice` 清空；`widget-core.js` `createWidgetFromState` 抽出與 `pasteWidgets` 共用
12. [ ] 存為範本：ctx-menu 項目 + 審核 modal + POST
13. [ ] 工具箱範本區塊 + 管理 modal + 拖放 → picker 範本模式 → 展開
14. [ ] resx / glossary；`dotnet test`；瀏覽器驗證 → 停下等確認 + 版號 → commit / push

### P3 實例重綁
15. [ ] ctx-menu「選取整組」「重新指定設備」；`resolveTemplate` 以實例元件的 `objRoleBind` 重算；混選提示
16. [ ] 頁面複製後重綁流程驗證（樹層級複製 → 貼上 → 每組重綁）
17. [ ] 文件：`功能說明書_SCADA監控頁.md` 新章節、`架構.md` 資料表對照、CLAUDE.md 不需改
18. [ ] `dotnet test`；瀏覽器驗證 → 停下等確認 + 版號 → commit / push → archive 回填成本

## 已知風險 / 待釐清

- ❓ **決策 7 內建範本清單**：請確認要保留哪些、要加哪些（本 plan 停在這裡等答覆 + 開工指令）
- ❓ **版號策略**（決策 8）：三段各提一版，或最後合併一版？
- ⚠️ **表格儲存格不支援角色**（v1）：表格綁定在 `arrCells[r][c].szSid` 巢狀結構，且同一表格常混多台設備（一列一台），與「一個實例 = 一台設備」的模型衝突；範本內表格會被清空儲存格綁定，審核對話框明示。電表類請用 `builtin.meter`（即時值組合）
- ⚠️ **Device 空白的站**：候選池 = 整站，多筆命中機率高；預覽會標示，補 Device 欄即改善（Modbus 來源頁可熱編輯）
- ⚠️ P2 要把 P1 的 `_showEquipPreview` / `_confirmEquipPick` 泛化成三種模式，動到剛出貨的路徑：改完先把 P1 的離線 Playwright harness（scratchpad `make-harness.js`，見歸檔 plan「踩到的雷」）重跑一次確認設備元件流程不回歸
- ⚠️ `auditDesignerBindingKeys` 啟動自檢會對新 props 名稱敏感：命名定案後先跑一次確認不誤報
- ⚠️ 範本內 image 元件若引用**上傳**圖（`/Designer/asset/{hash}`）：內容定址，跨機搬範本時圖可能不存在 → v1 內建範本只用 wwwroot 圖庫；使用者範本引用上傳圖屬同機行為，文件註明
- ⚠️ 本機為生產機：驗證需起開發 Web，依 CLAUDE.md 規則停 / 啟 `JoularisWeb` 並在回覆明講中斷窗口

## 測試策略

- [x] **本次是否動到核心計算 / 關鍵邏輯？** 是
  - [x] **node 斷言**（純函式，scratchpad，結果寫進度日誌）：
    - `resolveEquipRole`（P1 已有 86 組，重跑不變）+ 新角色：`送風溫度`→SAT、`RAT`→RAT、`CHWR`→CHWIN、`CWR`→CWIN、`溫度`→TEMP、`RH`→HUM、`CO2`→CO2、`壓差`→PRESS、`流量`→FLOW、`T` 單獨→null、`AHU1T`→TEMP；白名單：`出水溫` 在 `['CHWOUT']` → CHWOUT、在 `['WATERTEMP']` → WATERTEMP
    - `buildEquipSuggestions`：只在候選內找、多筆 `bMulti`、型別槽位隔離、`nHit / nTotal`
    - `inferRoleBindings`：綁定點位名→角色；推斷不到回 null 列；文字等於設備標籤→建議 `{device}`
    - `buildTemplatePayload`：所有 `szSid*` / `szCid*` / `sz*Name` 清空；座標相對 bbox 左上；表格 `arrCells[].szSid` 清空
    - `resolveTemplate`：角色→SID 與名稱；min/max 帶入；`{device}` 替換；未命中清單；同角色多元件共用同一點位；輸出不含範本的 `nX/nY` 原值（已加落點）
  - [x] **C# 單元測試**（`ScadaEngine.Tests/Designer/`）：種子 JSON 全部可解析、`szKey` 唯一且 `builtin.` 開頭、`objRoleBind` 的 key 皆符合 `^sz(Sid|Cid)`、角色名皆在允許清單（清單以 C# 常數維護一份，與 JS 表對照 → 測試同時讀 `role-aliases.js` 以 regex 抽角色名比對，防兩邊漂移）
- [x] 是否動到 DB 查詢 / 寫入？ 是 → `DesignTemplateRepositoryTests`（若既有整合測試基礎需真 DB 則標 `[Trait("Category","Integration")]` 與現況一致）
- [ ] 是否動到關鍵使用者路徑？ 是 → **E2E（Playwright）**：☑ 跳過（Designer 既有無 E2E；手動驗證替代）
- [ ] 手動驗證（瀏覽器）：
  - P1：拖泵浦 → 選設備 → 預覽 → 確認 → 五槽位填入；取消仍建空元件；重新指定設備 confirm 覆蓋；gauge / DI / 表格 picker 不變
  - P2：畫一組（image + DI + 即時值 + 控制鈕 + 文字）綁冰水泵1 → 存為範本 → 審核列正確、文字建議 `{device}` → 工具箱出現 → 拖出選冰水泵2 → 展開且全部命中 → 儲存重載 → /ScadaPage 正常；內建 10 範本各拖一次；管理：改名 / 刪除使用者範本、內建拒絕
  - P3：右鍵實例 → 選取整組 / 重新指定設備；複製整頁 → 貼上 → 每組重綁 → 無殘留舊 SID
  - en 介面全英文
- [ ] 收尾：`dotnet test` 全綠

## 文件同步

- [ ] `docs/功能說明書_SCADA監控頁.md` — 新章節「設備級綁定 / 設備範本與實例」（流程、角色表、範本 JSON 格式、內建清單、限制）+ §3 檔案結構
- [ ] `docs/架構.md` — §資料表用途對照加 `ScadaDesignTemplate`
- [ ] `docs/i18n-glossary.md` — 範本 / 實例 / 角色 / 重新指定設備
- [ ] `CLAUDE.md` — 不需改（未引入新架構模式）

---

## 進度日誌

### 2026-10-14
- 承接 autobind plan（v1 → v3）討論；使用者問「業主堅持自繪畫風怎麼辦」→ Claude 提三層（換皮 / 整群換設備 / 範本庫）→ 使用者裁定直接做第三層，並要求先確認預設範本庫
- 讀碼確認：設計 = 每頁 widget 陣列 JSON（DB）；元件 Ctrl+C/V 保留綁定、頁面複製清綁定；表格列範本為檔案存放；內建圖庫 CT / Fan / RFan 三組
- 寫 plan 期間發現 P1 已由另一對話（session `341cf1ff-…`）依 autobind plan v3 實作完成並 commit `534a4af`（26.8.0），該 plan 已自行歸檔；本 plan 改為承接：P1 段落保留作介面依據、P2 / P3 對齊已出貨的角色名（冷卻水出水用 WATERTEMP，不另設 CWOUT）與函式名
- **停下等：決策 7 範本清單確認 + 開工指令**

## 討論過程與成本記錄

### 討論過程摘要

- 使用者：同事反映 Designer 一個個點很累，一般 SCADA 怎麼做？→ Claude：業界四層（UDT / Template / 參數化頁面 / 拖放建元件），版面手畫、綁定自動化；建議先做設備級綁定
- 使用者「好」→ 寫 autobind plan v1（名稱前綴推斷）→ 使用者：直接抓 Device、拖放先開 picker 照提議 → v2 → 使用者提議「拉到畫面直接問哪台 Device、直接帶進去」→ v3（停在設備層、預覽、整台帶入）
- 使用者：「業主不喜歡預設元件風格、堅持自己畫，這不就沒效了？」→ Claude：拆「是哪台」與「長什麼樣」；三層方案由便宜到完整，建議先做整群換設備
- 使用者：「直接寫 plan 做 3，先預設範本庫跟我確認」→ 本 plan：三階段（地基 / 範本庫 / 實例重綁），內建範本清單列於決策 7 待確認

### 成本記錄

| 階段 | 模型 | Input | Output | Cache Read | Cache Write | 備註 |
|------|------|-------|--------|------------|-------------|------|
| Plan 撰寫（本 session：autobind plan v1–v3 討論 + 本 plan） | claude-fable-5-1 | 1,872 | 192,336 | 9,072,144 | 550,650 | 2026-10-14 自 session `2167a581-…` 加總（66 則 assistant）；autobind plan 本身的撰寫成本已在歸檔 plan 列過，此列含之、勿重複加總 |
| P1 實作（另一對話，已記在歸檔 plan） | claude-fable-5-1 | 5,464 | 663,905 | 39,930,695 | 1,395,917 | session `341cf1ff-…`，含 26.6.0 / 26.7.0 分批 commit 工作，無法拆分 |
| P2 實作（commit 後追記） | | | | | | |
| P3 實作（commit 後追記） | | | | | | |

### Archive 總結（搬進 _archive 前回填）

- **總 token**：input __ / output __ / cache read __ / cache write __
- **模型**：
- **API 費用（當下牌價，附計算式）**：牌價查詢日 YYYY-MM-DD，來源 __
  - 計算式：

## 完成後補充

### 實際做法 vs 原計畫差異
-

### 踩到的雷
-

### 對 memory / CLAUDE.md 的更新建議
-
