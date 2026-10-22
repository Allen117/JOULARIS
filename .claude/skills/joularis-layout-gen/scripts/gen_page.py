"""pipe-diagram layout.json → JOULARIS Designer 頁面檔（.joularis-page.json）+ 預覽圖 + 報告。

用法：
    python gen_page.py <layout.json> [--bg png|svg] [--out <資料夾>] [--pipe-diagram <build.py 路徑>] [--no-png]

做什麼：
  1. 每個 guide 依 `for`（沒有就取 text 第一個空白前的字串）對回 equipment
  2. 依設備型別套 roles.json 的固定座位，把 DI / AI / DO / AO widget 排進 guide 框內（不重疊、有斷言）
  3. 感測器旁、效率 / 氣象面板每列放未綁定的 AI 佔位（plan 決策 5：不猜）
  4. 底圖 = pipe-diagram build() 拿掉「預留區」後的圖；PNG 用 Edge 無頭模式轉（--bg svg 則直接內嵌 SVG）
  5. 寫 <案名>.joularis-page.json、<案名>_layout_preview.png、<案名>_report.md

綁定（SID）不在這裡解析：widget 只帶 props.objRoleBind（槽位 → 角色），由 Designer「匯入頁面檔」
依當地點位 + role-aliases.js 解析（plan 決策 3，別名只有一份真相）。

exit code：0 成功；2 guide 對不到設備 / 型別不支援；3 排位失敗（放不下或重疊）；4 底圖轉檔失敗。
"""
import argparse, base64, copy, datetime, importlib.util, json, math, os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FORMAT = "joularis-page"
VERSION = 1
EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


class GenError(Exception):
    def __init__(self, code, msg):
        super().__init__(msg)
        self.code = code


# ───────────────────────── 載入 ─────────────────────────

def load_roles(path=None):
    with open(path or os.path.join(HERE, "roles.json"), encoding="utf-8") as f:
        return json.load(f)


def load_pipe_diagram(build_path=None):
    """匯入 pipe-diagram 的 build.py（取 build() / TARGETS / load_manifest），不複製它的程式碼。"""
    build_path = build_path or os.path.join(os.path.expanduser("~"), ".claude", "skills", "pipe-diagram", "scripts", "build.py")
    if not os.path.isfile(build_path):
        raise GenError(4, f"找不到 pipe-diagram build.py：{build_path}（用 --pipe-diagram 指定）")
    spec = importlib.util.spec_from_file_location("pipe_diagram_build", build_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def site_name(layout_path):
    """與 build.py 相同的案名規則：資料夾名；layout-<系統>.json → <資料夾>-<系統>。"""
    base_dir = os.path.dirname(os.path.abspath(layout_path))
    site = os.path.basename(base_dir)
    stem = os.path.splitext(os.path.basename(layout_path))[0]
    if stem.lower().startswith("layout-"):
        site += "-" + stem[len("layout-"):]
    return site


# ───────────────────────── 幾何 ─────────────────────────

def rects_overlap(a, b, gap=0):
    """a、b = (x, y, w, h)；彼此距離 < gap 視為重疊（gap=1 → 至少要隔 1px）。"""
    return (a[0] < b[0] + b[2] + gap and b[0] < a[0] + a[2] + gap and
            a[1] < b[1] + b[3] + gap and b[1] < a[1] + a[3] + gap)


def inside(r, box):
    return r[0] >= box[0] and r[1] >= box[1] and r[0] + r[2] <= box[0] + box[2] and r[1] + r[3] <= box[1] + box[3]


def guide_box(g, target, targets):
    size = g.get("size", "full")
    tw, th = targets.get(target, targets["iauto"]).get(size, targets.get(target, targets["iauto"])["full"])
    return (g["x"], g["y"], g.get("w", tw), g.get("h", th))


def equip_bbox(e, manifest):
    """設備在畫布上的外框（含 mirror / rotate），算法與 build.py 擺設備時一致。"""
    m = manifest[e["type"]]
    s = e.get("scale", m["scale"])
    px, py = m["ports"][e.get("anchor", m.get("anchor", "in"))]
    if e.get("mirror"):
        px = m["w"] - px
    ax, ay = e["at"]
    tx, ty = ax - px * s, ay - py * s
    ang = math.radians(e.get("rotate", 0))
    pts = []
    for qx, qy in [(0, 0), (m["w"], 0), (0, m["h"]), (m["w"], m["h"])]:
        if ang:
            dx, dy = qx - px, qy - py
            qx = px + dx * math.cos(ang) - dy * math.sin(ang)
            qy = py + dx * math.sin(ang) + dy * math.cos(ang)
        pts.append((tx + qx * s, ty + qy * s))
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return (min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys))


# ───────────────────────── guide ↔ 設備 ─────────────────────────

def match_guides(L, roles):
    """回 (matched, errors)。matched = [{guide, equip, group_type, source}]。"""
    equip_by_id = {e.get("id"): e for e in L.get("equipment", [])}
    matched, errors, seen = [], [], {}
    for g in L.get("guides", []):
        szText = g.get("text", "")
        if g.get("for"):
            eid, source = g["for"], "for"
        else:
            eid, source = (szText.split()[0] if szText.split() else ""), "text"
        e = equip_by_id.get(eid)
        if not e:
            errors.append(f"預留區「{szText}」({g['x']},{g['y']}) 對不到設備：{'for' if source == 'for' else '由文字推斷'}={eid!r}")
            continue
        gtype = roles["equip_types"].get(e["type"])
        if not gtype:
            errors.append(f"預留區「{szText}」對到 {eid}，但型別 {e['type']} 沒有座位定義（roles.json equip_types）")
            continue
        if eid in seen:
            errors.append(f"設備 {eid} 有兩塊預留區（「{seen[eid]}」與「{szText}」）")
            continue
        seen[eid] = szText
        matched.append({"guide": g, "equip": e, "group_type": gtype, "source": source})
    return matched, errors


# ───────────────────────── 座位排列 ─────────────────────────

def fit_grid(box, seats, wdef):
    """把座位表塞進 box。先試原排列（縮 widget 寬到 min_w 為止），放不下再改少欄多列（略過空座位）。
    回 (grid, w, col_pitch, row_pitch, reflowed)；grid = [[seat 或 None]]。放不下 → None。"""
    bx, by, bw, bh = box
    H = wdef["h"]

    def try_grid(grid):
        nC = max(len(r) for r in grid)
        nR = len(grid)
        w = min(wdef["w"], math.floor((bw - (nC - 1) * wdef["col_gap"]) / nC)) if nC > 0 else wdef["w"]
        if w < wdef["min_w"]:
            return None
        if nR == 1:
            rp = 0
        else:
            rp = min(wdef["row_pitch"], math.floor((bh - H) / (nR - 1)))
            if rp < H + wdef["min_row_gap"]:
                return None
        if H + (nR - 1) * rp > bh:
            return None
        return (grid, w, w + wdef["col_gap"], rp)

    r = try_grid(seats)
    if r:
        return r + (False,)
    flat = [s for row in seats for s in row if s]
    for nC in range(max(len(r) for r in seats) - 1, 0, -1):
        grid = [flat[i:i + nC] for i in range(0, len(flat), nC)]
        r = try_grid(grid)
        if r:
            return r + (True,)
    return None


def place_equipment(match, roles, target, targets):
    g, e, gtype = match["guide"], match["equip"], match["group_type"]
    box = guide_box(g, target, targets)
    wdef = roles["widget"]
    fit = fit_grid(box, roles["seats"][gtype], wdef)
    if not fit:
        raise GenError(3, f"{e['id']} 的預留區 {box[2]}×{box[3]} 放不下 {gtype} 的座位"
                          f"（{sum(1 for r in roles['seats'][gtype] for s in r if s)} 個點位，widget 最小 {wdef['min_w']}×{wdef['h']}）；"
                          f"請在 layout.json 把該 guide 改成 \"size\": \"wide\" 或加大 w / h")
    grid, w, col_pitch, row_pitch, reflowed = fit
    # DI / AI 每格左側放角色短名 text widget（它們在監控頁不顯示標題）；DO / AO 自帶標題不放；格太窄就不放
    lw = wdef["label_w"]
    labeled = w - lw >= wdef["min_w"]
    widgets = []
    for ri, row in enumerate(grid):
        for ci, seat in enumerate(row):
            if not seat:
                continue
            sd = roles["seats_def"][seat]
            kind = roles["kinds"][sd["kind"]]
            x = int(box[0] + ci * col_pitch)
            y = int(box[1] + ri * row_pitch)
            seat_pos = {"szGroup": e["id"], "nCol": ci, "nRow": ri}
            lbl = labeled and not kind.get("self_titled")
            if lbl:
                widgets.append({
                    "szType": "text", "nX": x, "nY": y, "nW": lw - 1, "nH": wdef["h"], **seat_pos,
                    "props": {
                        "szText": "__i18n__:" + sd["label"],
                        "szTitle": "__i18n__:" + sd["title"],
                        "nFontSize": wdef["label_font"],
                        "szFontColor": wdef["label_color"],
                    },
                    "_kind": "LABEL", "_seat": seat,
                })
            widgets.append({
                "szType": kind["type"],
                "nX": x + (lw if lbl else 0), "nY": y,
                "nW": int(w - (lw if lbl else 0)), "nH": wdef["h"], **seat_pos,
                "props": {
                    "szTitle": "__i18n__:" + sd["title"],
                    "objRoleBind": {kind["slot"]: sd["role"]},
                },
                "_kind": sd["kind"], "_seat": seat,
            })
    group = {
        "szEquipId": e["id"],
        "szEquipType": gtype,
        "szSourceType": e["type"],
        "nColPitch": int(col_pitch),
        "szGuideSource": match["source"],
        "bReflowed": reflowed,
        "bLabeled": labeled,
        "_box": box,
    }
    return group, widgets


# ───────────────────────── 佔位（感測器 / 面板） ─────────────────────────

def panel_rows(L):
    """效率 / 氣象面板的列：有 text 的 box，且位於某個有 title 的 box 內。回 [(row_box, 面板標題)]。"""
    boxes = L.get("boxes", [])
    panels = [b for b in boxes if b.get("title")]
    out = []
    for b in boxes:
        if b.get("title") or not b.get("text"):
            continue
        r = (b["x"], b["y"], b["w"], b["h"])
        for p in panels:
            if inside(r, (p["x"], p["y"], p["w"], p["h"])):
                out.append((b, p["title"]))
                break
    return out


def place_placeholders(L, manifest, roles, occupied, canvas_wh):
    """回 (widgets, notes)。occupied = 已佔用的矩形（設備點位 widget + guide 框 + 設備外框）。"""
    wdef = roles["widget"]
    W, H = wdef["w"], wdef["h"]
    cw, ch = canvas_wh
    ai = roles["kinds"]["AI"]
    widgets, notes = [], []

    def free(r):
        return (r[0] >= 0 and r[1] >= 0 and r[0] + r[2] <= cw and r[1] + r[3] <= ch and
                not any(rects_overlap(r, o, 1) for o in occupied))

    # 面板列：widget 靠列框右側、垂直置中
    for b, szPanel in panel_rows(L):
        r = (int(b["x"] + b["w"] - W - 3), int(b["y"] + (b["h"] - H) / 2), W, H)
        if not free(r):
            notes.append(f"面板「{szPanel}」列「{b['text']}」的佔位與其他元件太近，請在 Designer 手動調整")
        widgets.append(_placeholder(ai, r, b["text"], f"panel:{szPanel}"))
        occupied.append(r)

    # 感測器：依序試右、左、下、上
    for e in L.get("equipment", []):
        if e["type"] not in roles["sensor_types"]:
            continue
        bx, by, bw, bh = equip_bbox(e, manifest)
        cx, cy = bx + bw / 2, by + bh / 2
        cands = []
        for d in (6, 30):   # 先貼近，不行再退一圈
            ring = [
                (bx + bw + d, cy - H / 2),
                (bx - d - W, cy - H / 2),
                (cx - W / 2, by + bh + d - 2),
                (cx - W / 2, by - d + 2 - H),
            ]
            if e.get("mirror"):   # 感測器在管右側時鏡像，空間通常在左邊
                ring[0], ring[1] = ring[1], ring[0]
            cands += ring
        pick = None
        for x, y in cands:
            r = (int(round(x)), int(round(y)), W, H)
            if free(r):
                pick = r
                break
        if not pick:
            raise GenError(3, f"感測器 {e['id']} 四周都沒有 {W}×{H} 的空位放佔位 widget，請在 layout.json 挪開或刪掉該感測器")
        widgets.append(_placeholder(ai, pick, e["id"], f"sensor:{e['id']}"))
        occupied.append(pick)
    return widgets, notes


def _placeholder(kind, r, szTitle, szSource):
    return {
        "szType": kind["type"], "nX": r[0], "nY": r[1], "nW": r[2], "nH": r[3],
        "props": {"szTitle": szTitle},
        "_kind": "AI", "_seat": None, "_placeholder": szSource,
    }


# ───────────────────────── 底圖 ─────────────────────────

def background_svg(L, base_dir, pd):
    """pipe-diagram 原圖拿掉「預留區」（guides 只是給人看的虛線框，上線畫面不要）。"""
    L2 = copy.deepcopy(L)
    L2.pop("guides", None)
    return pd.build(L2, base_dir)


def find_edge():
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    return None


def svg_to_png(svg_text, w, h, out_png):
    edge = find_edge()
    if not edge:
        raise GenError(4, "找不到 Microsoft Edge，無法把底圖轉 PNG（改用 --bg svg，或裝 Edge）")
    out_png = os.path.abspath(out_png)   # Edge 的 --screenshot 吃相對路徑會寫到它自己的工作目錄
    with tempfile.TemporaryDirectory() as td:
        src = os.path.join(td, "bg.svg")
        with open(src, "w", encoding="utf-8") as f:
            f.write(svg_text)
        url = "file:///" + src.replace("\\", "/")
        cmd = [edge, "--headless", "--disable-gpu", "--hide-scrollbars", f"--window-size={w},{h}",
               f"--screenshot={out_png}", url]
        subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    if not os.path.isfile(out_png) or os.path.getsize(out_png) == 0:
        raise GenError(4, f"Edge 轉 PNG 失敗：{out_png}")


def overlay_svg(svg_text, widgets, roles):
    """預覽：底圖疊 widget 框（DI 綠 / AI 黃 / DO 橘 / AO 紫；佔位虛線）。"""
    parts = []
    for ws in widgets:
        col = roles["kinds"][ws["_kind"]]["color"] if ws["_kind"] in roles["kinds"] else "#888888"
        dash = ' stroke-dasharray="4 3"' if ws.get("_placeholder") else ""
        parts.append(f'<rect x="{ws["nX"]}" y="{ws["nY"]}" width="{ws["nW"]}" height="{ws["nH"]}" '
                     f'fill="{col}" fill-opacity="0.35" stroke="{col}" stroke-width="2"{dash}/>')
        label = ("·" if ws["_kind"] == "LABEL" else ws.get("_seat")) or ws["props"]["szTitle"]
        parts.append(f'<text x="{ws["nX"] + 3}" y="{ws["nY"] + 14}" font-size="11" fill="#111">{_esc(label)}</text>')
    return svg_text.replace("</svg>", '<g id="widget-preview">' + "".join(parts) + "</g></svg>")


def _esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


# ───────────────────────── 主流程 ─────────────────────────

def generate(layout_path, out_dir=None, bg="png", build_path=None, roles_path=None, render_png=True):
    """回 dict（page / report_lines / paths）。失敗丟 GenError。render_png=False 時不呼叫 Edge（測試用）。"""
    roles = load_roles(roles_path)
    pd = load_pipe_diagram(build_path)
    base_dir = os.path.dirname(os.path.abspath(layout_path))
    out_dir = out_dir or base_dir
    os.makedirs(out_dir, exist_ok=True)
    site = site_name(layout_path)
    with open(layout_path, encoding="utf-8") as f:
        L = json.load(f)

    target = L.get("target", "iauto")
    manifest = pd.load_manifest()
    cw, ch = L["canvas"]["width"], L["canvas"]["height"]
    report = [f"# {site} — JOULARIS 頁面檔產生報告", "",
              f"- 來源：`{os.path.abspath(layout_path)}`",
              f"- 產生時間：{datetime.datetime.now():%Y-%m-%d %H:%M:%S}",
              f"- target：`{target}`" + ("" if target == "joularis" else "　⚠️ 不是 joularis，預留區尺寸依 iAUTO 規格，點位仍排在框內"),
              f"- 畫布：{cw} × {ch}，底圖：{bg.upper()}", ""]

    matched, errors = match_guides(L, roles)
    if errors:
        report += ["## ❌ 預留區對不到設備", ""] + [f"- {x}" for x in errors]
        _write_report(out_dir, site, report)
        raise GenError(2, "\n".join(errors))

    groups, widgets, occupied = [], [], []
    for m in matched:
        grp, ws = place_equipment(m, roles, target, pd.TARGETS)
        groups.append(grp)
        widgets += ws
        occupied.append(grp["_box"])
    for e in L.get("equipment", []):
        occupied.append(equip_bbox(e, manifest))
    ph, notes = place_placeholders(L, manifest, roles, occupied, (cw, ch))
    widgets += ph

    # 斷言：設備點位在自己的 guide 內、全部 widget 彼此不重疊（gap ≥ 1px）
    boxes = {g["szEquipId"]: g["_box"] for g in groups}
    for ws in widgets:
        if ws.get("szGroup") and not inside((ws["nX"], ws["nY"], ws["nW"], ws["nH"]), boxes[ws["szGroup"]]):
            raise GenError(3, f"{ws['szGroup']} 的 {ws['_seat']} 超出預留區")
    for i, a in enumerate(widgets):
        ra = (a["nX"], a["nY"], a["nW"], a["nH"])
        for b in widgets[i + 1:]:
            if rects_overlap(ra, (b["nX"], b["nY"], b["nW"], b["nH"]), 1):
                raise GenError(3, f"widget 重疊：{a.get('szGroup') or a['props']['szTitle']}/{a['_seat']} 與 "
                                  f"{b.get('szGroup') or b['props']['szTitle']}/{b['_seat']}")

    # 沒有預留區的設備（有座位定義卻沒 guide）→ 提醒
    have = {g["szEquipId"] for g in groups}
    orphan = [e["id"] for e in L.get("equipment", []) if e["type"] in roles["equip_types"] and e["id"] not in have]

    # 底圖
    svg_bg = background_svg(L, base_dir, pd)
    preview_png = os.path.join(out_dir, f"{site}_layout_preview.png")
    if bg == "svg":
        bg_url = "data:image/svg+xml;base64," + base64.b64encode(svg_bg.encode("utf-8")).decode("ascii")
        bg_name = f"{site}.svg"
    else:
        bg_png = os.path.join(out_dir, f"{site}_bg.png")
        if render_png:
            svg_to_png(svg_bg, cw, ch, bg_png)
            with open(bg_png, "rb") as f:
                bg_url = "data:image/png;base64," + base64.b64encode(f.read()).decode("ascii")
            os.remove(bg_png)
        else:
            bg_url = "data:image/png;base64,"
        bg_name = f"{site}.png"
    if render_png:
        svg_to_png(overlay_svg(svg_bg, widgets, roles), cw, ch, preview_png)

    page = {
        "szFormat": FORMAT,
        "nVersion": VERSION,
        "szName": site,
        "szGenerator": "joularis-layout-gen",
        "dtGenerated": datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
        "nCanvasW": cw,
        "nCanvasH": ch,
        "szBgFileName": bg_name,
        "szBgDataUrl": bg_url,
        "arrEquipGroups": [{k: v for k, v in g.items() if not k.startswith("_")} for g in groups],
        "arrWidgetState": [{k: v for k, v in w.items() if not k.startswith("_")} for w in widgets],
    }
    out_json = os.path.join(out_dir, f"{site}.joularis-page.json")
    with open(out_json, "w", encoding="utf-8", newline="\n") as f:
        json.dump(page, f, ensure_ascii=False, indent=1)

    # 報告
    report += ["## 設備 → 座位", "", "| 設備 | 型別 | 預留區對應 | 座位（欄,列 → 角色 / widget） |", "|---|---|---|---|"]
    for g in groups:
        ws = [w for w in widgets if w.get("szGroup") == g["szEquipId"] and w["_kind"] != "LABEL"]
        seats = "；".join(f"({w['nCol']},{w['nRow']}) {w['_seat']}→{w['szType']}" for w in ws)
        src = "for" if g["szGuideSource"] == "for" else "**由文字推斷**"
        if g["bReflowed"]:
            src += "（框太窄，已改少欄多列）"
        if not g["bLabeled"]:
            src += "（框太窄，**未放角色標籤**，監控頁只能靠 tooltip 辨識；建議 guide 改 size wide）"
        report.append(f"| {g['szEquipId']} | {g['szEquipType']} | {src} | {seats} |")
    report += ["", "## 未綁定佔位（匯入後請手動綁點）", ""]
    report += [f"- {w['_placeholder']} → 「{w['props']['szTitle']}」 @({w['nX']},{w['nY']})" for w in ph] or ["- （無）"]
    if orphan or notes:
        report += ["", "## ⚠️ 提醒", ""]
        report += [f"- {x} 沒有預留區，不會產生點位（要的話在 layout.json 加 guide 並寫 for）" for x in orphan]
        report += [f"- {x}" for x in notes]
    report += ["", "## 輸出", "", f"- 頁面檔：`{out_json}`（{os.path.getsize(out_json) / 1024:.0f} KB）"]
    if render_png:
        report.append(f"- 預覽圖：`{preview_png}`（綠 DI / 黃 AI / 橘 DO / 紫 AO；虛線 = 未綁定佔位）")
    report += ["", f"共 {len(groups)} 台設備、{sum(1 for w in widgets if w.get('szGroup') and w['_kind'] != 'LABEL')} 個角色座位、{len(ph)} 個佔位。",
               "匯入：Designer 頁面樹右鍵 →「匯入頁面檔…」，確認每台設備對到的 Device 後建頁，按「儲存」才寫入資料庫。"]
    rp = _write_report(out_dir, site, report)
    return {"page": page, "json": out_json, "preview": preview_png if render_png else None, "report": rp,
            "groups": groups, "widgets": widgets, "orphan": orphan, "notes": notes}


def _write_report(out_dir, site, lines):
    p = os.path.join(out_dir, f"{site}_report.md")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return p


def main(argv=None):
    ap = argparse.ArgumentParser(description="pipe-diagram layout.json → JOULARIS Designer 頁面檔")
    ap.add_argument("layout")
    ap.add_argument("--bg", choices=["png", "svg"], default="png")
    ap.add_argument("--out", help="輸出資料夾（預設 = layout.json 所在資料夾）")
    ap.add_argument("--pipe-diagram", dest="build_path", help="pipe-diagram 的 build.py 路徑")
    ap.add_argument("--no-png", action="store_true", help="不呼叫 Edge（底圖留空、不產預覽圖；測試用）")
    a = ap.parse_args(argv)
    try:
        r = generate(a.layout, a.out, a.bg, a.build_path, render_png=not a.no_png)
    except GenError as ex:
        print(f"錯誤：{ex}", file=sys.stderr)
        return ex.code
    for x in r["orphan"]:
        print(f"提醒：{x} 沒有預留區，不會產生點位", file=sys.stderr)
    for x in r["notes"]:
        print(f"提醒：{x}", file=sys.stderr)
    print("wrote", r["json"])
    if r["preview"]:
        print("wrote", r["preview"])
    print("wrote", r["report"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
