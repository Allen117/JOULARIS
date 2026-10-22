/**
 * ScadaPage 功能測試腳本 v2 - 修正登入流程
 */
import { chromium } from 'playwright';
import fs from 'fs';
import { fileURLToPath } from 'url';

const SCREENSHOT_DIR = fileURLToPath(new URL('./screenshots', import.meta.url));
fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });

const BASE_URL = 'http://localhost:5038';

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
const page = await context.newPage();

const consoleMessages = [];
page.on('console', msg => consoleMessages.push({ type: msg.type(), text: msg.text() }));
page.on('pageerror', err => consoleMessages.push({ type: 'pageerror', text: err.message }));

try {
    // ===== 1. 登入測試 =====
    console.log('=== 步驟 1: 登入 ===');
    await page.goto(`${BASE_URL}/Login`);
    await page.waitForLoadState('networkidle');

    // 顯示提示文字，確認是否有預設帳密提示
    const hintText = await page.locator('.alert-info, .text-info, .hint, .default-hint').allTextContents();
    console.log(`提示文字: ${hintText.join(' | ') || '無'}`);

    // 嘗試 admin/admin
    console.log('嘗試 admin/admin...');
    await page.fill('#tbUserName', 'admin');
    await page.fill('#tbPassword', 'admin');
    await page.screenshot({ path: `${SCREENSHOT_DIR}/01_login_filled.png`, fullPage: true });

    // 點擊登入
    await page.locator('button[type="submit"]').click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);

    let currentUrl = page.url();
    console.log(`登入後 URL: ${currentUrl}`);

    // 檢查 cookies
    let cookies = await context.cookies();
    const authCookie = cookies.find(c => c.name.includes('ScadaAuth') || c.name.includes('.AspNetCore'));
    console.log(`認證 Cookie: ${authCookie ? authCookie.name + '=' + authCookie.value.substring(0, 20) + '...' : '無'}`);
    console.log(`所有 Cookies: ${cookies.map(c => c.name).join(', ')}`);

    // 如果還在登入頁面，檢查錯誤並嘗試 ITRI/ITRI
    if (currentUrl.includes('/Login')) {
        console.log('admin/admin 登入失敗，檢查錯誤...');
        const errors = await page.locator('.validation-summary-errors li, .text-danger, .alert-danger').allTextContents();
        console.log(`錯誤訊息: ${errors.join(' | ')}`);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/02_login_failed_admin.png`, fullPage: true });

        // 嘗試 ITRI/ITRI
        console.log('\n嘗試 ITRI/ITRI...');
        await page.fill('#tbUserName', 'ITRI');
        await page.fill('#tbPassword', 'ITRI');
        await page.locator('button[type="submit"]').click();
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(2000);

        currentUrl = page.url();
        console.log(`ITRI 登入後 URL: ${currentUrl}`);

        cookies = await context.cookies();
        const authCookie2 = cookies.find(c => c.name.includes('ScadaAuth') || c.name.includes('.AspNetCore'));
        console.log(`認證 Cookie: ${authCookie2 ? authCookie2.name : '無'}`);

        if (currentUrl.includes('/Login')) {
            console.log('ITRI/ITRI 也失敗了');
            const errors2 = await page.locator('.validation-summary-errors li, .text-danger, .alert-danger').allTextContents();
            console.log(`錯誤訊息: ${errors2.join(' | ')}`);
            await page.screenshot({ path: `${SCREENSHOT_DIR}/02_login_failed_itri.png`, fullPage: true });

            // 嘗試第三組: 查看頁面上有什麼提示
            const bodyText = await page.locator('body').innerText();
            console.log(`頁面文字: ${bodyText.substring(0, 500)}`);
        }
    }

    // 不論登入是否成功，截圖當前狀態
    await page.screenshot({ path: `${SCREENSHOT_DIR}/03_after_login.png`, fullPage: true });

    const isLoggedIn = !page.url().includes('/Login');
    console.log(`\n登入狀態: ${isLoggedIn ? '成功' : '失敗'}`);

    if (!isLoggedIn) {
        console.log('\n⚠ 登入失敗，嘗試直接訪問 API 端點...');

        // 即使登入失敗，測試 API 的回應狀態
        const apiRes = await page.evaluate(async () => {
            const endpoints = [
                '/api/realtime/latest',
                '/api/control/manual-values',
                '/api/alarm-rules',
                '/Designer/Load'
            ];
            const results = {};
            for (const ep of endpoints) {
                try {
                    const r = await fetch(ep);
                    results[ep] = { status: r.status, statusText: r.statusText };
                } catch (e) {
                    results[ep] = { error: e.message };
                }
            }
            return results;
        });
        console.log('API 端點狀態 (未認證):');
        for (const [ep, res] of Object.entries(apiRes)) {
            console.log(`  ${ep}: ${res.status || res.error}`);
        }

        // 即使未認證，嘗試直接導航到 ScadaPage 看看是否被擋
        console.log('\n嘗試直接訪問 /ScadaPage...');
        const resp = await page.goto(`${BASE_URL}/ScadaPage`);
        console.log(`/ScadaPage 回應: ${resp.status()}, URL: ${page.url()}`);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/04_scadapage_unauth.png`, fullPage: true });

        console.log('\n=== 登入失敗，無法完整測試 ScadaPage 功能 ===');
        console.log('可能原因：');
        console.log('1. DB 中已有帳號，預設帳密 admin/admin 無效');
        console.log('2. 需要知道 DB 中實際帳號');
        console.log('3. SQL Server 可能未連接');
    }

    // ===== 如果登入成功，繼續完整測試 =====
    if (isLoggedIn) {
        console.log('\n=== 登入成功，開始 ScadaPage 完整測試 ===');

        // 導航至 ScadaPage
        if (!page.url().includes('/ScadaPage')) {
            await page.goto(`${BASE_URL}/ScadaPage`);
            await page.waitForLoadState('networkidle');
            await page.waitForTimeout(3000);
        }

        console.log(`ScadaPage URL: ${page.url()}`);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/04_scadapage.png`, fullPage: true });

        // 頁面結構
        console.log('\n--- 頁面結構 ---');
        const treeItems = await page.locator('#scadaPageTree .list-group-item, #scadaPageTree [data-sid]').all();
        console.log(`頁面樹節點: ${treeItems.length}`);
        for (let i = 0; i < Math.min(treeItems.length, 10); i++) {
            const text = (await treeItems[i].innerText()).trim().substring(0, 60);
            const sid = await treeItems[i].getAttribute('data-sid');
            console.log(`  ${i}: "${text}" (sid=${sid || 'N/A'})`);
        }

        // 畫布
        const canvas = page.locator('#scadaCanvas');
        if (await canvas.count() > 0) {
            const html = await canvas.innerHTML();
            console.log(`畫布 HTML: ${html.length} chars, 有內容: ${html.length > 10}`);
        }

        // Widget 統計
        console.log('\n--- Widget 統計 ---');
        for (const wtype of ['gauge', 'realtimeValue', 'diPoint', 'table', 'controlBtn', 'aoPoint', 'doPoint', 'pump', 'text']) {
            const count = await page.locator(`[data-widget-type="${wtype}"], [data-type="${wtype}"]`).count();
            if (count > 0) console.log(`  ${wtype}: ${count}`);
        }

        // 嘗試用不同的選擇器找 widgets
        const anyWidget = await page.locator('.widget, .scada-widget, [class*="widget"], [style*="position: absolute"]').count();
        console.log(`  通用選擇器找到: ${anyWidget}`);

        // 直接檢查畫布子元素
        const canvasChildren = await page.locator('#scadaCanvas > *').count();
        console.log(`  畫布直接子元素: ${canvasChildren}`);

        if (canvasChildren > 0) {
            const children = await page.locator('#scadaCanvas > *').all();
            for (let i = 0; i < Math.min(children.length, 5); i++) {
                const tag = await children[i].evaluate(el => el.tagName);
                const cls = await children[i].getAttribute('class');
                const dataType = await children[i].getAttribute('data-type') || await children[i].getAttribute('data-widget-type');
                console.log(`    子元素 ${i}: <${tag}> class="${cls}" data-type="${dataType}"`);
            }
        }

        // API 測試
        console.log('\n--- API 端點 ---');
        const realtimeData = await page.evaluate(async () => {
            const r = await fetch('/api/realtime/latest');
            return { status: r.status, data: await r.json() };
        });
        console.log(`/api/realtime/latest: status=${realtimeData.status}`);
        if (realtimeData.data?.data) {
            console.log(`  資料筆數: ${realtimeData.data.data.length}`);
            if (realtimeData.data.data[0]) console.log(`  範例: ${JSON.stringify(realtimeData.data.data[0])}`);
        }

        const manualData = await page.evaluate(async () => {
            try {
                const r = await fetch('/api/control/manual-values');
                return { status: r.status, data: await r.json() };
            } catch(e) { return { error: e.message }; }
        });
        console.log(`/api/control/manual-values: ${JSON.stringify(manualData).substring(0, 200)}`);

        const alarmData = await page.evaluate(async () => {
            try {
                const r = await fetch('/api/alarm-rules');
                return { status: r.status, data: await r.json() };
            } catch(e) { return { error: e.message }; }
        });
        console.log(`/api/alarm-rules: ${JSON.stringify(alarmData).substring(0, 200)}`);

        const designerData = await page.evaluate(async () => {
            const r = await fetch('/Designer/Load');
            const t = await r.text();
            try { return { status: r.status, data: JSON.parse(t) }; }
            catch { return { status: r.status, isHtml: t.includes('<!DOCTYPE'), length: t.length }; }
        });
        console.log(`/Designer/Load: ${JSON.stringify(designerData).substring(0, 300)}`);

        // 權限
        console.log('\n--- 權限 ---');
        const perms = await page.evaluate(() => ({
            isAdmin: typeof _isAdmin !== 'undefined' ? _isAdmin : 'N/A',
            perms: typeof _scadaPagePerms !== 'undefined' ? JSON.stringify(_scadaPagePerms).substring(0, 300) : 'N/A',
            viewFn: typeof _canViewPage === 'function',
            ctrlFn: typeof _canControlPage === 'function'
        }));
        console.log(`isAdmin: ${perms.isAdmin}`);
        console.log(`權限函式: view=${perms.viewFn}, control=${perms.ctrlFn}`);
        if (perms.perms !== 'N/A') console.log(`權限資料: ${perms.perms}`);

        // 即時資料
        console.log('\n--- 即時資料狀態 ---');
        const rtState = await page.evaluate(() => ({
            lastData: typeof lastData !== 'undefined' ? (Array.isArray(lastData) ? lastData.length : 'not array') : 'undefined',
            currentId: typeof scadaCurrentId !== 'undefined' ? scadaCurrentId : 'undefined',
            tree: typeof scadaPageTree !== 'undefined' ? JSON.stringify(scadaPageTree).substring(0, 400) : 'undefined'
        }));
        console.log(`lastData: ${rtState.lastData}`);
        console.log(`currentId: ${rtState.currentId}`);
        console.log(`pageTree: ${rtState.tree}`);

        // 頁面切換
        if (treeItems.length > 0) {
            console.log('\n--- 頁面切換 ---');
            for (let i = 0; i < Math.min(treeItems.length, 3); i++) {
                const text = (await treeItems[i].innerText()).trim();
                await treeItems[i].click();
                await page.waitForTimeout(2000);
                const widgetCount = await page.locator('#scadaCanvas > *').count();
                console.log(`頁面 "${text}": ${widgetCount} 個元素`);
                await page.screenshot({ path: `${SCREENSHOT_DIR}/05_page_${i}.png`, fullPage: true });
            }
        }

        // 響應式測試
        console.log('\n--- 響應式 ---');
        await page.setViewportSize({ width: 768, height: 1024 });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/06_tablet.png`, fullPage: true });

        await page.setViewportSize({ width: 375, height: 667 });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/07_mobile.png`, fullPage: true });
    }

    // Console 錯誤
    console.log('\n=== Console 錯誤 ===');
    const errors = consoleMessages.filter(m => m.type === 'error' || m.type === 'pageerror');
    console.log(`錯誤: ${errors.length} 個`);
    errors.forEach((e, i) => console.log(`  [${i}] ${e.text.substring(0, 250)}`));

    const warns = consoleMessages.filter(m => m.type === 'warning');
    console.log(`警告: ${warns.length} 個`);
    warns.slice(0, 5).forEach((w, i) => console.log(`  [${i}] ${w.text.substring(0, 200)}`));

} catch (error) {
    console.error(`\n測試出錯: ${error.message}`);
    console.error(error.stack);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/error.png`, fullPage: true });
} finally {
    await browser.close();
    console.log('\n=== 測試結束 ===');
}
