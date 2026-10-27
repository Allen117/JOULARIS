"""gen_page.py 測試：python test_gen_page.py（需 pipe-diagram skill 在 ~/.claude/skills/pipe-diagram）。

以 pipe-diagram gray-pipe 範例 layout.json 為底，程式內補 guides，驗：
框內、不重疊（gap ≥ 1px）、guide ↔ 設備對應、文字推斷、窄框改排、佔位、exit code、檔案格式。
預設不呼叫 Edge（--no-png）；設環境變數 GEN_PAGE_TEST_PNG=1 會多跑一次真的轉 PNG。
"""
import copy, json, os, shutil, sys, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gen_page  # noqa: E402

PD = os.path.join(os.path.expanduser("~"), ".claude", "skills", "pipe-diagram")
EXAMPLE = os.path.join(PD, "examples", "gray-pipe", "layout.json")


def base_layout():
    with open(EXAMPLE, encoding="utf-8") as f:
        L = json.load(f)
    L["target"] = "joularis"
    L["guides"] = [
        {"x": 960, "y": 60, "for": "ZP-1", "text": "ZP-1 點位區"},
        {"x": 960, "y": 150, "text": "ZP-2 點位區"},                     # 無 for → 由文字推斷
        {"x": 150, "y": 560, "for": "CH-5", "size": "wide", "text": "CH-5 點位區"},
        {"x": 960, "y": 245, "for": "ZP-3", "size": "narrow", "text": "ZP-3 點位區（窄）"},
    ]
    return L


class GenPageTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="genpage_")
        self.site = os.path.join(self.tmp, "測試案")
        os.makedirs(self.site)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_gen(self, L, png=False):
        p = os.path.join(self.site, "layout.json")
        with open(p, "w", encoding="utf-8") as f:
            json.dump(L, f, ensure_ascii=False)
        return gen_page.generate(p, os.path.join(self.tmp, "out"), "png", render_png=png)

    # ── 正常流程 ──
    def test_widgets_inside_guides_and_no_overlap(self):
        r = self.run_gen(base_layout())
        boxes = {g["szEquipId"]: g["_box"] for g in r["groups"]}
        ws = r["widgets"]
        for w in ws:
            if w.get("szGroup"):
                self.assertTrue(gen_page.inside((w["nX"], w["nY"], w["nW"], w["nH"]), boxes[w["szGroup"]]), w)
        for i, a in enumerate(ws):
            for b in ws[i + 1:]:
                self.assertFalse(gen_page.rects_overlap((a["nX"], a["nY"], a["nW"], a["nH"]),
                                                        (b["nX"], b["nY"], b["nW"], b["nH"]), 1), (a, b))

    def test_guide_mapping_and_text_fallback(self):
        r = self.run_gen(base_layout())
        g = {x["szEquipId"]: x for x in r["groups"]}
        self.assertEqual(set(g), {"ZP-1", "ZP-2", "CH-5", "ZP-3"})
        self.assertEqual(g["ZP-1"]["szGuideSource"], "for")
        self.assertEqual(g["ZP-2"]["szGuideSource"], "text")
        self.assertEqual(g["ZP-1"]["szEquipType"], "pump")
        self.assertEqual(g["CH-5"]["szEquipType"], "chiller")

    def test_pump_fixed_seats(self):
        r = self.run_gen(base_layout())
        ws = {w["_seat"]: w for w in r["widgets"] if w.get("szGroup") == "ZP-1" and w["_kind"] != "LABEL"}
        lb = {w["_seat"]: w for w in r["widgets"] if w.get("szGroup") == "ZP-1" and w["_kind"] == "LABEL"}
        self.assertEqual(len(ws), 6)
        self.assertEqual(set(lb), {"FAULT", "MODE", "RUN", "FREQ"})   # DO / AO 自帶標題，不放標籤
        self.assertEqual((ws["FAULT"]["nCol"], ws["FAULT"]["nRow"]), (0, 0))
        self.assertEqual((ws["FREQ"]["nCol"], ws["FREQ"]["nRow"]), (2, 1))
        self.assertEqual(ws["FAULT"]["szType"], "diPoint")
        self.assertEqual(ws["STARTSTOP"]["szType"], "doPoint")
        self.assertEqual(ws["FREQSET"]["szType"], "aoPoint")
        self.assertEqual(ws["FREQ"]["szType"], "realtimeValue")
        self.assertEqual(ws["FAULT"]["props"]["objRoleBind"], {"szSid": "FAULT"})
        self.assertEqual(ws["STARTSTOP"]["props"]["objRoleBind"], {"szCid": "STARTSTOP"})
        self.assertTrue(ws["RUN"]["props"]["szTitle"].startswith("__i18n__:designer.import.role."))
        # 320×60 標準框：每格 100 = 標籤 39 + 1px + widget 60、欄距 110、列距 40
        self.assertEqual(ws["FAULT"]["nW"], 60)
        self.assertEqual(lb["FAULT"]["nW"], 39)
        self.assertEqual(ws["FAULT"]["nX"] - lb["FAULT"]["nX"], 40)
        self.assertEqual(ws["STARTSTOP"]["nW"], 100)
        self.assertEqual(ws["STARTSTOP"]["nX"] - lb["FAULT"]["nX"], 110)
        self.assertEqual(ws["MODE"]["nY"] - ws["FAULT"]["nY"], 40)
        # 標籤與座位同欄同列（匯入時同進退）
        self.assertEqual((lb["RUN"]["nCol"], lb["RUN"]["nRow"]), (ws["RUN"]["nCol"], ws["RUN"]["nRow"]))
        self.assertEqual(lb["RUN"]["szType"], "text")
        self.assertEqual(lb["RUN"]["props"]["szText"], "__i18n__:designer.import.label.RUN")
        self.assertNotIn("objRoleBind", lb["RUN"]["props"])

    def test_chiller_wide_seven_seats_mode_remote(self):
        r = self.run_gen(base_layout())
        ws = {w["_seat"]: w for w in r["widgets"] if w.get("szGroup") == "CH-5" and w["_kind"] != "LABEL"}
        self.assertEqual(len(ws), 7)
        self.assertEqual(ws["MODE_REMOTE"]["props"]["objRoleBind"], {"szSid": "MODE"})
        self.assertEqual((ws["LOAD"]["nCol"], ws["LOAD"]["nRow"]), (3, 1))
        self.assertEqual(ws["LOAD"]["nW"], 60)

    def test_narrow_pump_shrinks_width(self):
        r = self.run_gen(base_layout())
        ws = [w for w in r["widgets"] if w.get("szGroup") == "ZP-3"]
        self.assertEqual(len(ws), 6)   # 窄框：格寬 63 放不下標籤 → 只有角色 widget
        self.assertTrue(all(60 <= w["nW"] < 100 and w["_kind"] != "LABEL" for w in ws))
        g = next(g for g in r["groups"] if g["szEquipId"] == "ZP-3")
        self.assertFalse(g["bReflowed"])
        self.assertFalse(g["bLabeled"])

    def test_chiller_in_standard_box_shrinks(self):
        L = base_layout()
        L["guides"][2].pop("size")
        r = self.run_gen(L)
        ws = [w for w in r["widgets"] if w.get("szGroup") == "CH-5"]
        self.assertEqual(len(ws), 7)   # 72 寬放不下標籤
        self.assertTrue(all(w["nW"] >= 60 for w in ws))

    def test_placeholders_for_sensors_and_panels(self):
        r = self.run_gen(base_layout())
        ph = [w for w in r["widgets"] if w.get("_placeholder")]
        self.assertTrue(ph)
        for w in ph:
            self.assertNotIn("objRoleBind", w["props"])
            self.assertEqual(w["szType"], "realtimeValue")
        sensor_ids = {e["id"] for e in base_layout()["equipment"] if e["type"] in ("sensor-temp", "flow-meter", "gauge-pressure")}
        self.assertEqual({w["props"]["szTitle"] for w in ph if w["_placeholder"].startswith("sensor:")}, sensor_ids)

    def test_page_file_format(self):
        r = self.run_gen(base_layout())
        with open(r["json"], encoding="utf-8") as f:
            page = json.load(f)
        self.assertEqual(page["szFormat"], "joularis-page")
        self.assertEqual(page["nVersion"], 1)
        self.assertEqual(page["szName"], "測試案")
        self.assertEqual((page["nCanvasW"], page["nCanvasH"]), (1594, 765))
        self.assertTrue(page["szBgDataUrl"].startswith("data:image/png;base64,"))
        for w in page["arrWidgetState"]:
            self.assertFalse(any(k.startswith("_") for k in w), w)   # 內部欄位不外流
        self.assertTrue(all("nColPitch" in g for g in page["arrEquipGroups"]))

    # ── 錯誤 ──
    def test_unmatched_guide_exit_2(self):
        L = base_layout()
        L["guides"].append({"x": 10, "y": 700, "for": "NOPE-9", "text": "NOPE-9 點位區"})
        p = os.path.join(self.site, "layout.json")
        with open(p, "w", encoding="utf-8") as f:
            json.dump(L, f, ensure_ascii=False)
        code = gen_page.main([p, "--out", os.path.join(self.tmp, "out"), "--no-png"])
        self.assertEqual(code, 2)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "out", "測試案.joularis-page.json")))
        with open(os.path.join(self.tmp, "out", "測試案_report.md"), encoding="utf-8") as f:
            self.assertIn("NOPE-9", f.read())

    def test_unsupported_type_exit_2(self):
        L = base_layout()
        L["guides"].append({"x": 10, "y": 700, "for": "T-1", "text": "T-1"})   # 感測器沒有座位
        with self.assertRaises(gen_page.GenError) as cm:
            self.run_gen(L)
        self.assertEqual(cm.exception.code, 2)

    def test_too_small_box_exit_3(self):
        L = base_layout()
        L["guides"][0].update({"w": 150, "h": 30})
        with self.assertRaises(gen_page.GenError) as cm:
            self.run_gen(L)
        self.assertEqual(cm.exception.code, 3)

    def test_ok_exit_0(self):
        p = os.path.join(self.site, "layout.json")
        with open(p, "w", encoding="utf-8") as f:
            json.dump(base_layout(), f, ensure_ascii=False)
        self.assertEqual(gen_page.main([p, "--out", os.path.join(self.tmp, "out"), "--no-png"]), 0)

    @unittest.skipUnless(os.environ.get("GEN_PAGE_TEST_PNG") == "1", "GEN_PAGE_TEST_PNG=1 才跑 Edge 轉檔")
    def test_png_render(self):
        r = self.run_gen(base_layout(), png=True)
        self.assertTrue(os.path.getsize(r["preview"]) > 0)
        self.assertGreater(len(r["page"]["szBgDataUrl"]), 1000)


if __name__ == "__main__":
    unittest.main(verbosity=2)
