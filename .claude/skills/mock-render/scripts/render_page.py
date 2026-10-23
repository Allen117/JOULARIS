# -*- coding: utf-8 -*-
"""渲染任一 Web 頁面的離線模擬 harness 並截圖（file://，不登入站台、不碰 DB）。

用法:
  python render_page.py --html recipes/ems-index.html --out ems.png
  python render_page.py --html <客製html> --out <png> [--width 1680] [--height 1050]
  python render_page.py --html <recipe> --out <png> --hash ctx-menu --selector "#panel"

--hash     附在 file:// URL 後的 #片段，供同一份 recipe 切換情境（recipe 讀 location.hash）
--selector 只截該 CSS 選擇器的元素（預設整頁 full_page）

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
    ap.add_argument("--hash", default="", help="URL #片段（recipe 以 location.hash 切情境）")
    ap.add_argument("--selector", default="", help="只截此 CSS 選擇器的元素")
    a = ap.parse_args()

    if not a.html:
        print("請以 --html 指定 harness。現有 recipes：")
        for f in sorted(RECIPES_DIR.glob("*.html")):
            print("  ", f)
        sys.exit(1)

    html = pathlib.Path(a.html).read_text(encoding="utf-8").replace("{{WWWROOT}}", WWWROOT_URL)
    out = pathlib.Path(a.out).resolve()
    # 暫存 html 放系統 temp（輸出到 repo 內時才不會留下 .render.html）
    tmp = pathlib.Path(tempfile.gettempdir()) / (out.stem + ".render.html")
    tmp.write_text(html, encoding="utf-8")

    with sync_playwright() as p:
        try:
            browser = p.chromium.launch(headless=True)
        except Exception:
            # pip 版 playwright 要的 chromium 版號與本機已裝的不同 → 改用 ms-playwright 下現有的 chromium
            cands = sorted(pathlib.Path.home().glob("AppData/Local/ms-playwright/chromium-*/chrome-win*/chrome.exe"))
            if cands:
                browser = p.chromium.launch(headless=True, executable_path=str(cands[-1]))
            else:
                # macOS 等未下載 playwright chromium 的機器 → 用系統安裝的 Google Chrome
                browser = p.chromium.launch(headless=True, channel="chrome")
        page = browser.new_page(viewport={"width": a.width, "height": a.height},
                                device_scale_factor=1.5)
        logs = []
        page.on("console", lambda m: logs.append(f"[{m.type}] {m.text}"))
        # 未捕捉例外（含頁面 JS 語法錯誤）不會進 console，要另外接
        page.on("pageerror", lambda e: logs.append(f"[pageerror] {e}"))
        page.goto(tmp.as_uri() + ("#" + a.hash if a.hash else ""))
        page.wait_for_load_state("networkidle")
        page.wait_for_timeout(1500)
        if a.selector:
            page.locator(a.selector).first.screenshot(path=str(out))
        else:
            page.screenshot(path=str(out), full_page=True)
        browser.close()

    print("saved:", out)
    for line in logs:
        print(line)


if __name__ == "__main__":
    main()
