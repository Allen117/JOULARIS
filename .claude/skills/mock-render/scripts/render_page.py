# -*- coding: utf-8 -*-
"""渲染任一 Web 頁面的離線模擬 harness 並截圖（file://，不登入站台、不碰 DB）。

用法:
  python render_page.py --html recipes/ems-index.html --out ems.png
  python render_page.py --html <客製html> --out <png> [--width 1680] [--height 1050]

html 內的 {{WWWROOT}} 佔位符會自動代入本 repo 的 ScadaEngine.Web/wwwroot file:// 路徑。
"""
import argparse
import pathlib
import sys
import tempfile

from playwright.sync_api import sync_playwright

SKILL_DIR = pathlib.Path(__file__).resolve().parent.parent          # .../skills/mock-render
REPO_ROOT = SKILL_DIR.parents[2]                                    # .claude/skills/<name> → repo root
WWWROOT_URL = (REPO_ROOT / "ScadaEngine.Web" / "wwwroot").as_uri()  # file:///.../wwwroot
RECIPES_DIR = SKILL_DIR / "recipes"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--html", help="recipe/harness html 路徑（可含 {{WWWROOT}} 佔位符）")
    ap.add_argument("--out", default=str(pathlib.Path(tempfile.gettempdir()) / "mock-render.png"))
    ap.add_argument("--width", type=int, default=1680)
    ap.add_argument("--height", type=int, default=1050)
    a = ap.parse_args()

    if not a.html:
        print("請以 --html 指定 harness。現有 recipes：")
        for f in sorted(RECIPES_DIR.glob("*.html")):
            print("  ", f)
        sys.exit(1)

    html = pathlib.Path(a.html).read_text(encoding="utf-8").replace("{{WWWROOT}}", WWWROOT_URL)
    out = pathlib.Path(a.out)
    tmp = out.with_suffix(".render.html")
    tmp.write_text(html, encoding="utf-8")

    with sync_playwright() as p:
        try:
            browser = p.chromium.launch(headless=True)
        except Exception:
            # pip 版 playwright 要的 chromium 版號與本機已裝的不同 → 改用 ms-playwright 下現有的 chromium
            cands = sorted(pathlib.Path.home().glob("AppData/Local/ms-playwright/chromium-*/chrome-win*/chrome.exe"))
            if not cands:
                raise
            browser = p.chromium.launch(headless=True, executable_path=str(cands[-1]))
        page = browser.new_page(viewport={"width": a.width, "height": a.height},
                                device_scale_factor=1.5)
        logs = []
        page.on("console", lambda m: logs.append(f"[{m.type}] {m.text}"))
        page.goto(tmp.as_uri())
        page.wait_for_load_state("networkidle")
        page.wait_for_timeout(1500)
        page.screenshot(path=str(out), full_page=True)
        browser.close()

    print("saved:", out)
    for line in logs:
        print(line)


if __name__ == "__main__":
    main()
