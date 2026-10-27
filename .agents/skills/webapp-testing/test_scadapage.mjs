/**
 * ScadaPage 功能測試腳本
 */
import { chromium } from 'playwright';
import fs from 'fs';
import { fileURLToPath } from 'url';
import path from 'path';

const SCREENSHOT_DIR = fileURLToPath(new URL('./screenshots', import.meta.url));
fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });

const BASE_URL = 'http://localhost:5038';

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });

// 收集 console 訊息
const consoleMessages = [];
page.on('console', msg => consoleMessages.push({ type: msg.type(), text: msg.text() }));
page.on('pageerror', err => consoleMessages.push({ type: 'pageerror', text: err.message }));

try {
    // ===== 1. 登入 =====
    console.log('=== 步驟 1: 登入 ===');
    await page.goto(`${BASE_URL}/Login`);
    await page.waitForLoadState('networkidle');
    await page.screenshot({ path: `${SCREENSHOT_DIR}/01_login_page.png`, fullPage: true });
    console.log(`登入頁面 URL: ${page.url()}`);

    // 找輸入框
    const inputs = await page.locator('input').all();
    console.log(`找到 ${inputs.length} 個 input 元素`);
    for (const inp of inputs) {
        const type = await inp.getAttribute('type');
        const name = await inp.getAttribute('name');
        const id = await inp.getAttribute('id');
        console.log(`  input: type=${type}, name=${name}, id=${id}`);
    }

    // 填入帳號密碼
    const usernameField = page.locator('input[type="text"], input[name*="ser"], input[name*="ame"]').first();
    const passwordField = page.locator('input[type="password"]').first();

    await usernameField.fill('ITRI');
    await passwordField.fill('ITRI');
    console.log('已填入帳密 ITRI/ITRI');
    await page.screenshot({ path: `${SCREENSHOT_DIR}/02_login_filled.png`, fullPage: true });

    // 點擊登入按鈕
    const submitBtn = page.locator('button[type="submit"], input[type="submit"]').first();
    await submitBtn.click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);
    console.log(`登入後 URL: ${page.url()}`);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/03_after_login.png`, fullPage: true });

    const loginSuccess = !page.url().includes('/Login');
    console.log(`登入結果: ${loginSuccess ? '成功' : '失敗'}`);
    if (!loginSuccess) {
        console.log('登入失敗，嘗試截圖錯誤訊息...');
        const errorText = await page.locator('.alert, .text-danger, .error, .validation-summary-errors').allTextContents();
        console.log(`錯誤訊息: ${errorText.join(', ')}`);
    }

    // ===== 2. 導航至 ScadaPage =====
    console.log('\n=== 步驟 2: 導航至 ScadaPage ===');
    await page.goto(`${BASE_URL}/ScadaPage`);
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(3000);
    console.log(`ScadaPage URL: ${page.url()}`);

    if (page.url().includes('/Login')) {
        console.log('被重導至登入頁，認證可能失敗');
    }
    await page.screenshot({ path: `${SCREENSHOT_DIR}/04_scadapage_initial.png`, fullPage: true });

    // ===== 3. 頁面結構分析 =====
    console.log('\n=== 步驟 3: 頁面結構分析 ===');

    // 檢查左側頁面樹
    const treeItems = await page.locator('#scadaPageTree .list-group-item, #scadaPageTree li, #scadaPageTree [data-sid]').all();
    console.log(`頁面樹節點數: ${treeItems.length}`);
    for (let i = 0; i < Math.min(treeItems.length, 10); i++) {
        const text = (await treeItems[i].innerText()).trim().substring(0, 60);
        const sid = await treeItems[i].getAttribute('data-sid');
        console.log(`  節點 ${i}: "${text}" (sid=${sid || 'N/A'})`);
    }

    // 檢查畫布
    const canvas = page.locator('#scadaCanvas');
    if (await canvas.count() > 0) {
        const html = await canvas.innerHTML();
        console.log(`\n畫布 HTML 長度: ${html.length} chars`);
        console.log(`畫布有內容: ${html.length > 10 ? '是' : '否（空白）'}`);
    } else {
        console.log('找不到 #scadaCanvas 元素');
    }

    // 按 Widget 類型統計
    console.log('\nWidget 統計:');
    for (const wtype of ['gauge', 'realtimeValue', 'diPoint', 'table', 'controlBtn', 'aoPoint', 'doPoint', 'pump', 'text']) {
        const count = await page.locator(`[data-widget-type="${wtype}"], [data-type="${wtype}"]`).count();
        if (count > 0) console.log(`  ${wtype}: ${count} 個`);
    }

    // 通用 widget 選擇器
    const allWidgets = await page.locator('[data-widget-type], [data-type], .scada-widget').count();
    console.log(`Widget 總數 (通用選擇器): ${allWidgets}`);

    // ===== 4. API 端點測試 =====
    console.log('\n=== 步驟 4: API 端點測試 ===');

    // /api/realtime/latest
    const realtimeResult = await page.evaluate(async () => {
        try {
            const resp = await fetch('/api/realtime/latest');
            const data = await resp.json();
            return {
                status: resp.status,
                count: Array.isArray(data) ? data.length : Object.keys(data).length,
                sample: JSON.stringify(data).substring(0, 600)
            };
        } catch (e) { return { error: e.message }; }
    });
    console.log(`/api/realtime/latest: status=${realtimeResult.status}, count=${realtimeResult.count}`);
    if (realtimeResult.sample) console.log(`  樣本: ${realtimeResult.sample.substring(0, 300)}`);

    // /api/control/manual-values
    const manualResult = await page.evaluate(async () => {
        try {
            const resp = await fetch('/api/control/manual-values');
            const data = await resp.json();
            return {
                status: resp.status,
                count: Object.keys(data).length,
                sample: JSON.stringify(data).substring(0, 400)
            };
        } catch (e) { return { error: e.message }; }
    });
    console.log(`/api/control/manual-values: status=${manualResult.status}, count=${manualResult.count}`);
    if (manualResult.sample) console.log(`  樣本: ${manualResult.sample.substring(0, 300)}`);

    // /api/alarm-rules
    const alarmResult = await page.evaluate(async () => {
        try {
            const resp = await fetch('/api/alarm-rules');
            const data = await resp.json();
            return {
                status: resp.status,
                count: Array.isArray(data) ? data.length : Object.keys(data).length,
                sample: JSON.stringify(data).substring(0, 400)
            };
        } catch (e) { return { error: e.message }; }
    });
    console.log(`/api/alarm-rules: status=${alarmResult.status}, count=${alarmResult.count}`);

    // /Designer/Load
    const designerResult = await page.evaluate(async () => {
        try {
            const resp = await fetch('/Designer/Load');
            const text = await resp.text();
            let parsed;
            try { parsed = JSON.parse(text); } catch { parsed = text; }
            return {
                status: resp.status,
                type: typeof parsed,
                isArray: Array.isArray(parsed),
                length: Array.isArray(parsed) ? parsed.length : (typeof parsed === 'object' ? Object.keys(parsed).length : text.length),
                sample: JSON.stringify(parsed).substring(0, 600)
            };
        } catch (e) { return { error: e.message }; }
    });
    console.log(`/Designer/Load: status=${designerResult.status}, type=${designerResult.type}, isArray=${designerResult.isArray}, length=${designerResult.length}`);
    if (designerResult.sample) console.log(`  樣本: ${designerResult.sample.substring(0, 400)}`);

    // ===== 5. 頁面切換測試 =====
    console.log('\n=== 步驟 5: 頁面切換測試 ===');
    const clickablePages = await page.locator('#scadaPageTree [onclick], #scadaPageTree [data-sid]').all();
    console.log(`可點擊的頁面: ${clickablePages.length}`);

    if (clickablePages.length > 0) {
        const firstText = (await clickablePages[0].innerText()).trim();
        console.log(`點擊第一個頁面: "${firstText}"`);
        await clickablePages[0].click();
        await page.waitForTimeout(2000);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/05_first_page_selected.png`, fullPage: true });

        const widgetsAfter = await page.locator('[data-widget-type], [data-type], .scada-widget').count();
        console.log(`切換後 Widget 數: ${widgetsAfter}`);

        // 如果有第二個頁面，也測試切換
        if (clickablePages.length > 1) {
            const secondText = (await clickablePages[1].innerText()).trim();
            console.log(`點擊第二個頁面: "${secondText}"`);
            await clickablePages[1].click();
            await page.waitForTimeout(2000);
            await page.screenshot({ path: `${SCREENSHOT_DIR}/06_second_page.png`, fullPage: true });

            const widgetsSecond = await page.locator('[data-widget-type], [data-type], .scada-widget').count();
            console.log(`第二頁 Widget 數: ${widgetsSecond}`);
        }
    }

    // ===== 6. 權限檢查 =====
    console.log('\n=== 步驟 6: 權限檢查 ===');
    const permInfo = await page.evaluate(() => {
        return {
            isAdmin: typeof _isAdmin !== 'undefined' ? _isAdmin : 'undefined',
            hasPerms: typeof _scadaPagePerms !== 'undefined' ? JSON.stringify(_scadaPagePerms).substring(0, 500) : 'undefined',
            canViewFn: typeof _canViewPage === 'function' ? 'exists' : 'missing',
            canControlFn: typeof _canControlPage === 'function' ? 'exists' : 'missing'
        };
    });
    console.log(`isAdmin: ${permInfo.isAdmin}`);
    console.log(`canViewPage 函式: ${permInfo.canViewFn}`);
    console.log(`canControlPage 函式: ${permInfo.canControlFn}`);
    if (permInfo.hasPerms !== 'undefined') console.log(`權限資料: ${permInfo.hasPerms}`);

    // ===== 7. 即時資料更新測試 =====
    console.log('\n=== 步驟 7: 即時資料更新測試 ===');
    const dataInfo = await page.evaluate(() => {
        return {
            hasLastData: typeof lastData !== 'undefined',
            lastDataLength: typeof lastData !== 'undefined' && Array.isArray(lastData) ? lastData.length : 0,
            scadaPageTree: typeof scadaPageTree !== 'undefined' ? JSON.stringify(scadaPageTree).substring(0, 500) : 'undefined',
            currentId: typeof scadaCurrentId !== 'undefined' ? scadaCurrentId : 'undefined'
        };
    });
    console.log(`lastData 存在: ${dataInfo.hasLastData}, 長度: ${dataInfo.lastDataLength}`);
    console.log(`scadaCurrentId: ${dataInfo.currentId}`);
    if (dataInfo.scadaPageTree !== 'undefined') console.log(`pageTree: ${dataInfo.scadaPageTree}`);

    // 等待 3 秒再次檢查資料是否更新
    await page.waitForTimeout(3000);
    const dataInfo2 = await page.evaluate(() => {
        return {
            lastDataLength: typeof lastData !== 'undefined' && Array.isArray(lastData) ? lastData.length : 0
        };
    });
    console.log(`等待 3 秒後 lastData 長度: ${dataInfo2.lastDataLength}`);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/07_after_data_update.png`, fullPage: true });

    // ===== 8. Widget 互動測試 =====
    console.log('\n=== 步驟 8: Widget 互動測試 ===');

    // 測試 gauge 右鍵選單
    const gauges = await page.locator('[data-widget-type="gauge"], [data-type="gauge"]').all();
    if (gauges.length > 0) {
        console.log(`找到 ${gauges.length} 個 Gauge，測試右鍵選單...`);
        await gauges[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/08_gauge_context_menu.png`, fullPage: true });
        // 關閉右鍵選單
        await page.click('body');
        await page.waitForTimeout(300);
    }

    // 測試 controlBtn
    const ctrlBtns = await page.locator('[data-widget-type="controlBtn"], [data-type="controlBtn"], .ctrl-btn-exec').all();
    if (ctrlBtns.length > 0) {
        console.log(`找到 ${ctrlBtns.length} 個控制按鈕`);
        for (let i = 0; i < Math.min(ctrlBtns.length, 3); i++) {
            const text = (await ctrlBtns[i].innerText()).trim();
            console.log(`  按鈕 ${i}: "${text}"`);
        }
    }

    // 測試 aoPoint 右鍵選單
    const aoPoints = await page.locator('[data-widget-type="aoPoint"], [data-type="aoPoint"]').all();
    if (aoPoints.length > 0) {
        console.log(`找到 ${aoPoints.length} 個 AO 點位`);
        await aoPoints[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/09_ao_context_menu.png`, fullPage: true });
        await page.click('body');
        await page.waitForTimeout(300);
    }

    // 測試 doPoint 右鍵選單
    const doPoints = await page.locator('[data-widget-type="doPoint"], [data-type="doPoint"]').all();
    if (doPoints.length > 0) {
        console.log(`找到 ${doPoints.length} 個 DO 點位`);
        await doPoints[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/10_do_context_menu.png`, fullPage: true });
        await page.click('body');
        await page.waitForTimeout(300);
    }

    // 測試 pump
    const pumps = await page.locator('[data-widget-type="pump"], [data-type="pump"]').all();
    if (pumps.length > 0) {
        console.log(`找到 ${pumps.length} 個 Pump widget`);
        await pumps[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/11_pump_context_menu.png`, fullPage: true });
        await page.click('body');
    }

    // ===== 9. Console 錯誤統計 =====
    console.log('\n=== 步驟 9: Console 錯誤統計 ===');
    const errors = consoleMessages.filter(m => m.type === 'error' || m.type === 'pageerror');
    const warnings = consoleMessages.filter(m => m.type === 'warning');
    console.log(`Console 錯誤: ${errors.length} 個`);
    errors.slice(0, 10).forEach((e, i) => console.log(`  [${i}] ${e.text.substring(0, 200)}`));
    console.log(`Console 警告: ${warnings.length} 個`);
    warnings.slice(0, 5).forEach((w, i) => console.log(`  [${i}] ${w.text.substring(0, 200)}`));

    // ===== 10. 響應式測試 =====
    console.log('\n=== 步驟 10: 響應式佈局測試 ===');
    await page.setViewportSize({ width: 768, height: 1024 });
    await page.waitForTimeout(1000);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/12_tablet_view.png`, fullPage: true });

    await page.setViewportSize({ width: 375, height: 667 });
    await page.waitForTimeout(1000);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/13_mobile_view.png`, fullPage: true });

    console.log('\n=== 測試完成 ===');

} catch (error) {
    console.error(`測試出錯: ${error.message}`);
    console.error(error.stack);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/error_screenshot.png`, fullPage: true });
} finally {
    await browser.close();
}
