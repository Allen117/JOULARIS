"""ScadaPage 功能測試腳本 - 第一階段：登入 + 頁面偵查"""
from playwright.sync_api import sync_playwright
import json, os

SCREENSHOT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "screenshots")
os.makedirs(SCREENSHOT_DIR, exist_ok=True)

BASE_URL = "http://localhost:5038"

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={"width": 1920, "height": 1080})

    # ===== 1. 登入 =====
    print("=== 步驟 1: 登入 ===")
    page.goto(f"{BASE_URL}/Login")
    page.wait_for_load_state("networkidle")
    page.screenshot(path=f"{SCREENSHOT_DIR}/01_login_page.png", full_page=True)
    print(f"登入頁面標題: {page.title()}")

    # 填入預設帳密 ITRI/ITRI
    username_input = page.locator("input[name='Username'], input[name='szUsername'], #Username, #szUsername")
    password_input = page.locator("input[name='Password'], input[name='szPassword'], #Password, #szPassword")

    if username_input.count() > 0:
        username_input.first.fill("ITRI")
        print("已填入帳號: ITRI")
    else:
        # 嘗試 type=text 的第一個 input
        page.locator("input[type='text']").first.fill("ITRI")
        print("已填入帳號 (fallback): ITRI")

    if password_input.count() > 0:
        password_input.first.fill("ITRI")
        print("已填入密碼: ITRI")
    else:
        page.locator("input[type='password']").first.fill("ITRI")
        print("已填入密碼 (fallback): ITRI")

    page.screenshot(path=f"{SCREENSHOT_DIR}/02_login_filled.png", full_page=True)

    # 提交登入
    submit_btn = page.locator("button[type='submit'], input[type='submit']")
    if submit_btn.count() > 0:
        submit_btn.first.click()
    else:
        page.locator("form").first.evaluate("form => form.submit()")

    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(2000)
    print(f"登入後 URL: {page.url}")
    page.screenshot(path=f"{SCREENSHOT_DIR}/03_after_login.png", full_page=True)

    # ===== 2. 導航至 ScadaPage =====
    print("\n=== 步驟 2: 導航至 ScadaPage ===")
    page.goto(f"{BASE_URL}/ScadaPage")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(3000)  # 等待 AJAX 載入設計資料
    print(f"ScadaPage URL: {page.url}")
    page.screenshot(path=f"{SCREENSHOT_DIR}/04_scadapage_initial.png", full_page=True)

    # ===== 3. 檢查頁面結構 =====
    print("\n=== 步驟 3: 頁面結構分析 ===")

    # 檢查左側頁面樹
    tree_items = page.locator("#scadaPageTree .list-group-item, #scadaPageTree li, #scadaPageTree [data-sid]")
    tree_count = tree_items.count()
    print(f"頁面樹節點數: {tree_count}")

    if tree_count > 0:
        for i in range(min(tree_count, 10)):
            item = tree_items.nth(i)
            text = item.inner_text().strip()
            sid = item.get_attribute("data-sid") or "N/A"
            print(f"  節點 {i}: text='{text[:50]}', sid={sid}")

    # 檢查畫布區域
    canvas = page.locator("#scadaCanvas")
    if canvas.count() > 0:
        canvas_html = canvas.inner_html()
        print(f"\n畫布 HTML 長度: {len(canvas_html)} chars")
        print(f"畫布是否有內容: {'是' if len(canvas_html) > 10 else '否（空白）'}")

    # 檢查 Widget
    widgets = page.locator("[data-widget-type], .scada-widget, [data-type]")
    widget_count = widgets.count()
    print(f"\nWidget 總數: {widget_count}")

    # 按類型統計
    for wtype in ["gauge", "realtimeValue", "diPoint", "table", "controlBtn", "aoPoint", "doPoint", "pump", "text"]:
        type_widgets = page.locator(f"[data-widget-type='{wtype}'], [data-type='{wtype}']")
        count = type_widgets.count()
        if count > 0:
            print(f"  {wtype}: {count} 個")

    # ===== 4. 檢查 API 端點 =====
    print("\n=== 步驟 4: API 端點測試 ===")

    # 測試 /api/realtime/latest
    api_response = page.evaluate("""
        async () => {
            try {
                const resp = await fetch('/api/realtime/latest');
                const status = resp.status;
                const data = await resp.json();
                return { status, dataLength: Array.isArray(data) ? data.length : Object.keys(data).length, sample: JSON.stringify(data).substring(0, 500) };
            } catch(e) {
                return { error: e.message };
            }
        }
    """)
    print(f"/api/realtime/latest: {json.dumps(api_response, ensure_ascii=False, indent=2)}")

    # 測試 /api/control/manual-values
    manual_response = page.evaluate("""
        async () => {
            try {
                const resp = await fetch('/api/control/manual-values');
                const status = resp.status;
                const data = await resp.json();
                return { status, dataLength: Object.keys(data).length, sample: JSON.stringify(data).substring(0, 500) };
            } catch(e) {
                return { error: e.message };
            }
        }
    """)
    print(f"/api/control/manual-values: {json.dumps(manual_response, ensure_ascii=False, indent=2)}")

    # 測試 /api/alarm-rules
    alarm_response = page.evaluate("""
        async () => {
            try {
                const resp = await fetch('/api/alarm-rules');
                const status = resp.status;
                const data = await resp.json();
                return { status, dataLength: Array.isArray(data) ? data.length : Object.keys(data).length, sample: JSON.stringify(data).substring(0, 500) };
            } catch(e) {
                return { error: e.message };
            }
        }
    """)
    print(f"/api/alarm-rules: {json.dumps(alarm_response, ensure_ascii=False, indent=2)}")

    # 測試 /Designer/Load
    designer_response = page.evaluate("""
        async () => {
            try {
                const resp = await fetch('/Designer/Load');
                const status = resp.status;
                const text = await resp.text();
                let parsed;
                try { parsed = JSON.parse(text); } catch { parsed = text.substring(0, 500); }
                return { status, dataType: typeof parsed, sample: JSON.stringify(parsed).substring(0, 800) };
            } catch(e) {
                return { error: e.message };
            }
        }
    """)
    print(f"/Designer/Load: {json.dumps(designer_response, ensure_ascii=False, indent=2)}")

    # ===== 5. 如果有頁面樹節點，點擊第一個 =====
    print("\n=== 步驟 5: 頁面切換測試 ===")
    clickable_pages = page.locator("#scadaPageTree [onclick], #scadaPageTree [data-sid]")
    click_count = clickable_pages.count()
    print(f"可點擊的頁面節點: {click_count}")

    if click_count > 0:
        first_page = clickable_pages.first
        page_text = first_page.inner_text().strip()
        print(f"點擊第一個頁面: '{page_text}'")
        first_page.click()
        page.wait_for_timeout(2000)
        page.screenshot(path=f"{SCREENSHOT_DIR}/05_first_page_selected.png", full_page=True)

        # 再檢查 widget
        widgets_after = page.locator("[data-widget-type], .scada-widget, [data-type]")
        print(f"切換後 Widget 數: {widgets_after.count()}")

    # ===== 6. 檢查 Console 錯誤 =====
    print("\n=== 步驟 6: 收集 Console 訊息 ===")
    console_messages = []
    page.on("console", lambda msg: console_messages.append({"type": msg.type, "text": msg.text}))

    # 重新載入頁面收集 console
    page.reload()
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(3000)

    errors = [m for m in console_messages if m["type"] == "error"]
    warnings = [m for m in console_messages if m["type"] == "warning"]
    print(f"Console 錯誤: {len(errors)} 個")
    for e in errors[:5]:
        print(f"  ERROR: {e['text'][:200]}")
    print(f"Console 警告: {len(warnings)} 個")
    for w in warnings[:5]:
        print(f"  WARN: {w['text'][:200]}")

    # 最終全頁截圖
    page.screenshot(path=f"{SCREENSHOT_DIR}/06_final_state.png", full_page=True)

    # ===== 7. 權限檢查 =====
    print("\n=== 步驟 7: 權限資訊 ===")
    perm_info = page.evaluate("""
        () => {
            return {
                isAdmin: typeof _isAdmin !== 'undefined' ? _isAdmin : 'undefined',
                hasPerms: typeof _scadaPagePerms !== 'undefined' ? JSON.stringify(_scadaPagePerms).substring(0, 500) : 'undefined',
                canViewFn: typeof _canViewPage !== 'undefined' ? 'exists' : 'undefined',
                canControlFn: typeof _canControlPage !== 'undefined' ? 'exists' : 'undefined'
            };
        }
    """)
    print(f"權限資訊: {json.dumps(perm_info, ensure_ascii=False, indent=2)}")

    # ===== 8. 檢查即時資料更新 =====
    print("\n=== 步驟 8: 即時資料更新測試 ===")
    # 等待幾秒看資料是否更新
    page.wait_for_timeout(3000)
    page.screenshot(path=f"{SCREENSHOT_DIR}/07_after_data_update.png", full_page=True)

    last_data = page.evaluate("""
        () => {
            return {
                hasLastData: typeof lastData !== 'undefined',
                lastDataLength: typeof lastData !== 'undefined' && Array.isArray(lastData) ? lastData.length : 0,
                sample: typeof lastData !== 'undefined' ? JSON.stringify(lastData).substring(0, 500) : 'N/A'
            };
        }
    """)
    print(f"即時資料: {json.dumps(last_data, ensure_ascii=False, indent=2)}")

    print("\n=== 測試完成 ===")
    browser.close()
