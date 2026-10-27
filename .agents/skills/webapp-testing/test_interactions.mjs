/**
 * ScadaPage Widget 互動 + 控制功能測試
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

const consoleErrors = [];
page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });
page.on('pageerror', err => consoleErrors.push(err.message));

try {
    // 登入
    await page.goto(`${BASE_URL}/Login`);
    await page.waitForLoadState('networkidle');
    await page.fill('#tbUserName', 'admin');
    await page.fill('#tbPassword', 'admin');
    await page.locator('button[type="submit"]').click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(3000);
    console.log(`已登入，目前在: ${page.url()}`);

    if (!page.url().includes('/ScadaPage')) {
        await page.goto(`${BASE_URL}/ScadaPage`);
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(3000);
    }

    // =============================
    // 1. Gauge Widget 詳細測試
    // =============================
    console.log('\n========= 1. Gauge Widget 測試 =========');
    const gauges = await page.locator('[data-type="gauge"]').all();
    console.log(`Gauge 數量: ${gauges.length}`);

    for (let i = 0; i < gauges.length; i++) {
        const g = gauges[i];
        const sid = await g.getAttribute('data-sid');
        const min = await g.getAttribute('data-fmin') || await g.getAttribute('data-fMin');
        const max = await g.getAttribute('data-fmax') || await g.getAttribute('data-fMax');
        const unit = await g.getAttribute('data-szunit') || await g.getAttribute('data-szUnit');
        const title = await g.getAttribute('data-sztitle') || await g.getAttribute('data-szTitle');
        const text = (await g.innerText()).trim().substring(0, 80);
        console.log(`  Gauge ${i}: sid=${sid}, range=[${min},${max}], unit="${unit}", title="${title}", display="${text}"`);

        // 檢查 SVG 渲染
        const hasSvg = await g.locator('svg').count() > 0;
        console.log(`    SVG 渲染: ${hasSvg ? '是' : '否'}`);
    }

    // Gauge 右鍵選單測試
    if (gauges.length > 0) {
        console.log('\n  測試 Gauge 右鍵選單...');
        await gauges[0].click({ button: 'right' });
        await page.waitForTimeout(500);

        const ctxMenu = page.locator('.context-menu, [class*="context"], .dropdown-menu:visible, .trend-context-menu');
        const menuVisible = await ctxMenu.count() > 0;
        console.log(`  右鍵選單出現: ${menuVisible}`);
        if (menuVisible) {
            const menuItems = await ctxMenu.locator('a, li, .menu-item, [onclick]').allTextContents();
            console.log(`  選單項目: ${menuItems.join(' | ')}`);
        }
        await page.screenshot({ path: `${SCREENSHOT_DIR}/10_gauge_context.png`, fullPage: true });
        await page.keyboard.press('Escape');
        await page.waitForTimeout(300);
    }

    // =============================
    // 2. RealtimeValue Widget 測試
    // =============================
    console.log('\n========= 2. RealtimeValue Widget 測試 =========');
    const rtValues = await page.locator('[data-type="realtimeValue"]').all();
    console.log(`RealtimeValue 數量: ${rtValues.length}`);

    for (let i = 0; i < rtValues.length; i++) {
        const rv = rtValues[i];
        const sid = await rv.getAttribute('data-sid');
        const fontSize = await rv.getAttribute('data-nfontsize') || await rv.getAttribute('data-nFontSize');
        const unit = await rv.getAttribute('data-szunit') || await rv.getAttribute('data-szUnit');
        const text = (await rv.innerText()).trim();
        console.log(`  RT Value ${i}: sid=${sid}, fontSize=${fontSize}, unit="${unit}", display="${text}"`);
    }

    // =============================
    // 3. DI Point Widget 測試
    // =============================
    console.log('\n========= 3. DI Point Widget 測試 =========');
    const diPoints = await page.locator('[data-type="diPoint"]').all();
    console.log(`DI Point 數量: ${diPoints.length}`);

    for (let i = 0; i < diPoints.length; i++) {
        const di = diPoints[i];
        const sid = await di.getAttribute('data-sid');
        const onColor = await di.getAttribute('data-szoncolor') || await di.getAttribute('data-szOnColor');
        const offColor = await di.getAttribute('data-szoffcolor') || await di.getAttribute('data-szOffColor');
        const onLabel = await di.getAttribute('data-szonlabel') || await di.getAttribute('data-szOnLabel');
        const offLabel = await di.getAttribute('data-szofflabel') || await di.getAttribute('data-szOffLabel');
        const text = (await di.innerText()).trim().substring(0, 50);
        console.log(`  DI ${i}: sid=${sid}, ON="${onLabel}"(${onColor}), OFF="${offLabel}"(${offColor}), display="${text}"`);

        // 檢查是否有閃爍動畫
        const hasAnim = await di.evaluate(el => {
            const style = window.getComputedStyle(el.querySelector('.di-indicator, [class*="indicator"]') || el);
            return style.animationName !== 'none' && style.animationName !== '';
        }).catch(() => false);
        if (hasAnim) console.log(`    有警報閃爍動畫`);
    }

    // =============================
    // 4. Table Widget 測試
    // =============================
    console.log('\n========= 4. Table Widget 測試 =========');
    const tables = await page.locator('[data-type="table"]').all();
    console.log(`Table 數量: ${tables.length}`);

    for (let i = 0; i < tables.length; i++) {
        const tbl = tables[i];
        const rows = await tbl.locator('tr').count();
        const cols = await tbl.locator('tr:first-child td, tr:first-child th').count();
        const headerText = await tbl.locator('thead, tr:first-child').first().innerText().catch(() => '');
        console.log(`  Table ${i}: ${rows} rows x ${cols} cols, header="${headerText.trim().substring(0, 80)}"`);

        // 檢查動態綁定的 cell
        const boundCells = await tbl.locator('[data-sid]').count();
        console.log(`    綁定 SID 的 cell: ${boundCells}`);
    }

    // =============================
    // 5. Control Button 測試
    // =============================
    console.log('\n========= 5. Control Button 測試 =========');
    const ctrlBtns = await page.locator('[data-type="controlBtn"]').all();
    console.log(`Control Button 數量: ${ctrlBtns.length}`);

    for (let i = 0; i < ctrlBtns.length; i++) {
        const btn = ctrlBtns[i];
        const cid = await btn.getAttribute('data-cid');
        const title = await btn.getAttribute('data-sztitle') || await btn.getAttribute('data-szTitle');
        const label = await btn.getAttribute('data-szbtnlabel') || await btn.getAttribute('data-szBtnLabel');
        const ctrlValue = await btn.getAttribute('data-fctrlvalue') || await btn.getAttribute('data-fCtrlValue');
        const text = (await btn.innerText()).trim();
        console.log(`  CtrlBtn ${i}: cid=${cid}, title="${title}", label="${label}", value=${ctrlValue}, display="${text}"`);

        // 檢查是否可點擊（有 onclick 綁定）
        const clickable = await btn.locator('.ctrl-btn-exec, button, [onclick]').count() > 0;
        console.log(`    可點擊: ${clickable}`);
    }

    // 注意：不實際點擊控制按鈕（會發送真正的 MQTT 指令）
    console.log('  ⚠ 跳過實際點擊控制按鈕（避免發送 MQTT 指令）');

    // =============================
    // 6. AO Point 測試
    // =============================
    console.log('\n========= 6. AO Point 測試 =========');
    const aoPoints = await page.locator('[data-type="aoPoint"]').all();
    console.log(`AO Point 數量: ${aoPoints.length}`);

    for (let i = 0; i < aoPoints.length; i++) {
        const ao = aoPoints[i];
        const cid = await ao.getAttribute('data-cid');
        const title = await ao.getAttribute('data-sztitle') || await ao.getAttribute('data-szTitle');
        const min = await ao.getAttribute('data-fmin') || await ao.getAttribute('data-fMin');
        const max = await ao.getAttribute('data-fmax') || await ao.getAttribute('data-fMax');
        const step = await ao.getAttribute('data-fstep') || await ao.getAttribute('data-fStep');
        const text = (await ao.innerText()).trim().substring(0, 60);
        console.log(`  AO ${i}: cid=${cid}, title="${title}", range=[${min},${max}], step=${step}, display="${text}"`);
    }

    // AO 右鍵選單測試
    if (aoPoints.length > 0) {
        console.log('\n  測試 AO 右鍵選單...');
        await aoPoints[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/11_ao_context.png`, fullPage: true });

        // 找右鍵選單
        const aoMenu = page.locator('.ao-context-menu, [class*="context-menu"]:visible, .dropdown-menu:visible');
        const aoMenuVisible = await aoMenu.count() > 0;
        console.log(`  AO 右鍵選單: ${aoMenuVisible ? '出現' : '未出現'}`);

        if (!aoMenuVisible) {
            // 嘗試找所有可見的 menu 元素
            const allMenus = await page.evaluate(() => {
                const menus = document.querySelectorAll('[style*="display: block"], [style*="display:block"], .show, [class*="menu"]');
                return Array.from(menus).map(m => ({
                    tag: m.tagName,
                    cls: m.className,
                    vis: m.offsetHeight > 0,
                    text: m.innerText?.substring(0, 100)
                }));
            });
            if (allMenus.length > 0) {
                console.log(`  找到其他選單元素:`);
                allMenus.forEach(m => console.log(`    <${m.tag}> cls="${m.cls}" vis=${m.vis} text="${m.text}"`));
            }
        }

        await page.keyboard.press('Escape');
        await page.click('body', { position: { x: 10, y: 10 } });
        await page.waitForTimeout(300);
    }

    // =============================
    // 7. DO Point 測試
    // =============================
    console.log('\n========= 7. DO Point 測試 =========');
    const doPoints = await page.locator('[data-type="doPoint"]').all();
    console.log(`DO Point 數量: ${doPoints.length}`);

    for (let i = 0; i < doPoints.length; i++) {
        const doP = doPoints[i];
        const cid = await doP.getAttribute('data-cid');
        const onValue = await doP.getAttribute('data-nonvalue') || await doP.getAttribute('data-nOnValue');
        const offValue = await doP.getAttribute('data-noffvalue') || await doP.getAttribute('data-nOffValue');
        const text = (await doP.innerText()).trim().substring(0, 60);
        console.log(`  DO ${i}: cid=${cid}, onVal=${onValue}, offVal=${offValue}, display="${text}"`);
    }

    // DO 右鍵選單
    if (doPoints.length > 0) {
        console.log('\n  測試 DO 右鍵選單...');
        await doPoints[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/12_do_context.png`, fullPage: true });
        await page.keyboard.press('Escape');
        await page.click('body', { position: { x: 10, y: 10 } });
        await page.waitForTimeout(300);
    }

    // =============================
    // 8. Pump Widget 測試
    // =============================
    console.log('\n========= 8. Pump Widget 測試 =========');
    const pumps = await page.locator('[data-type="pump"]').all();
    console.log(`Pump 數量: ${pumps.length}`);

    for (let i = 0; i < pumps.length; i++) {
        const pump = pumps[i];
        const sidRun = await pump.getAttribute('data-sidrun') || await pump.getAttribute('data-sidRun');
        const sidFault = await pump.getAttribute('data-sidfault') || await pump.getAttribute('data-sidFault');
        const sidFreq = await pump.getAttribute('data-sidfreq') || await pump.getAttribute('data-sidFreq');
        const cidStartStop = await pump.getAttribute('data-cidstartstop') || await pump.getAttribute('data-cidStartStop');
        const cidFreq = await pump.getAttribute('data-cidfreqset') || await pump.getAttribute('data-cidFreqSet');
        const text = (await pump.innerText()).trim().substring(0, 80);
        console.log(`  Pump ${i}: sidRun=${sidRun}, sidFault=${sidFault}, sidFreq=${sidFreq}`);
        console.log(`    cidStartStop=${cidStartStop}, cidFreqSet=${cidFreq}`);
        console.log(`    display="${text}"`);

        // 檢查 SVG / 動畫
        const hasSvg = await pump.locator('svg').count() > 0;
        const hasSpinAnim = await pump.evaluate(el => {
            const spinning = el.querySelector('[class*="spin"], [style*="animation"]');
            return !!spinning;
        });
        console.log(`    SVG: ${hasSvg}, 旋轉動畫: ${hasSpinAnim}`);
    }

    // Pump 右鍵選單
    if (pumps.length > 0) {
        console.log('\n  測試 Pump 右鍵選單...');
        await pumps[0].click({ button: 'right' });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${SCREENSHOT_DIR}/13_pump_context.png`, fullPage: true });
        await page.keyboard.press('Escape');
        await page.click('body', { position: { x: 10, y: 10 } });
        await page.waitForTimeout(300);
    }

    // =============================
    // 9. Text Widget 測試
    // =============================
    console.log('\n========= 9. Text Widget 測試 =========');
    const texts = await page.locator('[data-type="text"]').all();
    console.log(`Text 數量: ${texts.length}`);
    for (let i = 0; i < texts.length; i++) {
        const t = texts[i];
        const text = (await t.innerText()).trim();
        console.log(`  Text ${i}: "${text}"`);
    }

    // =============================
    // 10. 即時資料更新驗證
    // =============================
    console.log('\n========= 10. 即時資料更新驗證 =========');

    // 取第一次的值
    const firstValues = await page.evaluate(() => {
        const result = {};
        document.querySelectorAll('[data-type="gauge"] .gauge-value, [data-type="realtimeValue"] .rt-value').forEach(el => {
            const parent = el.closest('[data-sid]');
            if (parent) result[parent.getAttribute('data-sid')] = el.textContent.trim();
        });
        return result;
    });
    console.log(`第一次採樣: ${JSON.stringify(firstValues).substring(0, 200)}`);

    // 等待 5 秒
    await page.waitForTimeout(5000);

    // 取第二次的值
    const secondValues = await page.evaluate(() => {
        const result = {};
        document.querySelectorAll('[data-type="gauge"] .gauge-value, [data-type="realtimeValue"] .rt-value').forEach(el => {
            const parent = el.closest('[data-sid]');
            if (parent) result[parent.getAttribute('data-sid')] = el.textContent.trim();
        });
        return result;
    });
    console.log(`第二次採樣: ${JSON.stringify(secondValues).substring(0, 200)}`);

    // 比較
    let updated = 0, same = 0;
    for (const sid of Object.keys(firstValues)) {
        if (secondValues[sid] !== firstValues[sid]) {
            updated++;
            console.log(`  ${sid}: ${firstValues[sid]} → ${secondValues[sid]} (已更新)`);
        } else {
            same++;
        }
    }
    console.log(`更新統計: ${updated} 個變化, ${same} 個不變`);

    // =============================
    // 11. 警報狀態視覺化檢查
    // =============================
    console.log('\n========= 11. 警報狀態檢查 =========');
    const alarmState = await page.evaluate(() => {
        const results = [];
        // 檢查有警報色變化的 widget
        document.querySelectorAll('[data-type]').forEach(el => {
            const sid = el.getAttribute('data-sid') || el.getAttribute('data-cid');
            const type = el.getAttribute('data-type');
            const style = window.getComputedStyle(el);
            const bgColor = style.backgroundColor;
            const color = style.color;
            const borderColor = style.borderColor;

            // 檢查是否有非預設的顏色
            const hasAlarmIndicator = el.querySelector('[style*="color: red"], [style*="color: #dc3545"], .text-danger, [class*="alarm"]');
            if (hasAlarmIndicator) {
                results.push({ sid, type, alarm: true, text: hasAlarmIndicator.textContent?.substring(0, 30) });
            }
        });
        return results;
    });
    console.log(`有警報指示的 Widget: ${alarmState.length}`);
    alarmState.forEach(a => console.log(`  ${a.type}(${a.sid}): "${a.text}"`));

    // =============================
    // 12. 頁面佈局 & 背景圖
    // =============================
    console.log('\n========= 12. 頁面佈局檢查 =========');
    const layoutInfo = await page.evaluate(() => {
        const canvas = document.getElementById('scadaCanvas');
        if (!canvas) return { error: 'no canvas' };

        const style = window.getComputedStyle(canvas);
        const bgImage = style.backgroundImage;

        const widgets = canvas.children;
        const positions = [];
        for (const w of widgets) {
            const wStyle = window.getComputedStyle(w);
            positions.push({
                type: w.getAttribute('data-type'),
                x: wStyle.left,
                y: wStyle.top,
                w: wStyle.width,
                h: wStyle.height
            });
        }

        return {
            canvasSize: `${canvas.offsetWidth}x${canvas.offsetHeight}`,
            hasBg: bgImage !== 'none',
            bgSize: style.backgroundSize,
            widgetCount: widgets.length,
            positions: positions.slice(0, 5)
        };
    });
    console.log(`畫布大小: ${layoutInfo.canvasSize}`);
    console.log(`背景圖: ${layoutInfo.hasBg ? '有' : '無'}, 大小: ${layoutInfo.bgSize}`);
    console.log(`Widget 位置(前5): ${JSON.stringify(layoutInfo.positions, null, 2)}`);

    // 最終全頁截圖
    await page.screenshot({ path: `${SCREENSHOT_DIR}/14_final.png`, fullPage: true });

    // =============================
    // 13. 404 資源檢查
    // =============================
    console.log('\n========= 13. 404 資源檢查 =========');
    const failedRequests = [];
    page.on('response', resp => {
        if (resp.status() === 404) failedRequests.push(resp.url());
    });
    await page.reload();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);
    console.log(`404 資源: ${failedRequests.length} 個`);
    failedRequests.forEach(u => console.log(`  ${u}`));

    // Console 錯誤彙整
    console.log('\n========= Console 錯誤彙整 =========');
    console.log(`總共 ${consoleErrors.length} 個錯誤`);
    consoleErrors.forEach((e, i) => console.log(`  [${i}] ${e.substring(0, 200)}`));

    console.log('\n========= 測試完成 =========');

} catch (error) {
    console.error(`測試出錯: ${error.message}`);
    console.error(error.stack);
    await page.screenshot({ path: `${SCREENSHOT_DIR}/error_interaction.png`, fullPage: true });
} finally {
    await browser.close();
}
