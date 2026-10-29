(function () {
    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var coordinators = window._modbusCoordinatorData || [];

    /** DB 有 Coordinator 但 JSON 已移除的名稱（小寫）— 顯示「設定檔已移除」、隱藏點位設定與刪除按鈕 */
    var aMissingConfig = (window._modbusMissingConfig || []).map(function (s) { return String(s).toLowerCase(); });

    /** 設備名稱 → { ip, port }（來自 JSON；設定檔已移除者查不到 → 顯示空白） */
    var endpoints = window._modbusEndpoints || {};

    var nCurrentIndex = -1;
    var nCurrentSubIndex = -1;

    function endpointOf(szName) {
        var e = szName ? endpoints[szName] : null;
        return { ip: e ? (e.ip || '') : '', port: (e && e.port != null) ? String(e.port) : '' };
    }

    function clearAllActive() {
        document.querySelectorAll('.coordinator-item, .coordinator-toggle, .sub-item, .pending-item')
            .forEach(function (el) { el.classList.remove('active'); });
    }

    function showFullDetail(index) {
        var c = coordinators[index];
        if (!c) return;
        nCurrentIndex = index;
        nCurrentSubIndex = -1;

        document.getElementById('fullDetail').style.display = '';
        document.getElementById('subDetail').style.display = 'none';
        document.getElementById('detailTitle').innerHTML =
            '<i class="fas fa-info-circle me-1"></i>' + t('modbuscoordinator.card.device_detail') + ' — ' + (c.szName || '');

        var ep = endpointOf(c.szName);
        document.getElementById('fieldName').value           = c.szName || '';
        document.getElementById('fieldIp').value             = ep.ip;
        document.getElementById('fieldPort').value           = ep.port;
        document.getElementById('fieldModbusID').value       = c.szModbusID || '';
        document.getElementById('fieldDelayTime').value      = c.nDelayTime;
        document.getElementById('fieldMonitorEnabled').value = c.isMonitorEnabled ? t('modbuscoordinator.value.yes') : t('modbuscoordinator.value.no');

        showOpenPointsButton(c.szName || '');
        updateSourceControls(c.szName || '');
        updateRenameControls(c.szName || '');
    }

    function showSubDetail(id, modbusId, name, index, subIndex) {
        nCurrentIndex = index;
        nCurrentSubIndex = subIndex;

        document.getElementById('fullDetail').style.display = 'none';
        document.getElementById('subDetail').style.display = '';
        document.getElementById('detailTitle').innerHTML =
            '<i class="fas fa-microchip me-1"></i>' + t('modbuscoordinator.card.device_info') + ' — ' + modbusId;

        var ep = endpointOf((coordinators[index] || {}).szName);
        document.getElementById('subFieldIp').value       = ep.ip;
        document.getElementById('subFieldPort').value     = ep.port;
        document.getElementById('subFieldModbusID').value = modbusId;
        document.getElementById('subFieldName').value     = name || '';
        document.getElementById('saveStatus').style.display = 'none';

        hideOpenPointsButton();
        updateSourceControls(null);
    }

    // 單一 Coordinator 點擊
    document.querySelectorAll('.coordinator-item').forEach(function (item) {
        item.addEventListener('click', function (e) {
            e.preventDefault();
            clearAllActive();
            this.classList.add('active');
            showFullDetail(parseInt(this.dataset.index));
        });
    });

    // 展開 / 收合子選單
    document.querySelectorAll('.coordinator-toggle').forEach(function (toggle) {
        toggle.addEventListener('click', function (e) {
            e.preventDefault();
            var subMenu = this.nextElementSibling;
            var icon = this.querySelector('.toggle-icon');
            if (subMenu.style.display === 'none') {
                subMenu.style.display = '';
                icon.classList.add('open');
            } else {
                subMenu.style.display = 'none';
                icon.classList.remove('open');
            }
            clearAllActive();
            this.classList.add('active');
            showFullDetail(parseInt(this.dataset.index));
        });
    });

    // 子項目點擊
    document.querySelectorAll('.sub-item').forEach(function (item) {
        item.addEventListener('click', function (e) {
            e.preventDefault();
            clearAllActive();
            this.classList.add('active');
            showSubDetail(this.dataset.subId, this.dataset.subModbusid, this.dataset.subName,
                parseInt(this.dataset.index), parseInt(this.dataset.subIndex));
        });
    });

    // 儲存名稱按鈕
    var btnSave = document.getElementById('btnSaveName');
    if (btnSave) {
        btnSave.addEventListener('click', async function () {
            if (nCurrentIndex < 0 || nCurrentSubIndex < 0) return;

            var c = coordinators[nCurrentIndex];
            var deviceNames = (c.szDeviceName || '').split(',').map(function (s) { return s.trim(); });
            var modbusIds = (c.szModbusID || '').split(',');
            while (deviceNames.length < modbusIds.length) deviceNames.push('');

            deviceNames[nCurrentSubIndex] = document.getElementById('subFieldName').value.trim();

            var szNewDeviceName = deviceNames.join(',');
            var statusEl = document.getElementById('saveStatus');

            try {
                var resp = await fetch('/ModbusCoordinator/UpdateDeviceName', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ id: c.Id, deviceName: szNewDeviceName })
                });
                var result = await resp.json();
                if (result.success) {
                    // 後端正規化後的名稱串（JSON 與 DB 同一份）
                    if (typeof result.deviceName === 'string') {
                        szNewDeviceName = result.deviceName;
                        deviceNames = szNewDeviceName.split(',').map(function (s) { return s.trim(); });
                    }
                    c.szDeviceName = szNewDeviceName;
                    if (result.workbookNotice) window.alert(result.workbookNotice);
                    var subItems = document.querySelectorAll('.sub-item[data-index="' + nCurrentIndex + '"]');
                    subItems.forEach(function (el, j) {
                        var name = deviceNames[j] || '';
                        el.dataset.subName = name;
                        var span = el.querySelector('span');
                        if (span) span.textContent = name || el.dataset.subModbusid;
                    });
                    statusEl.textContent = t('modbuscoordinator.msg.save_success');
                    statusEl.className = 'ms-2 small text-success';
                } else {
                    statusEl.textContent = result.message || t('modbuscoordinator.msg.save_failed');
                    statusEl.className = 'ms-2 small text-danger';
                }
            } catch (ex) {
                statusEl.textContent = t('modbuscoordinator.msg.save_failed_with', { error: ex.message });
                statusEl.className = 'ms-2 small text-danger';
            }
            statusEl.style.display = '';
        });
    }

    /* ============================================================
     * 設備名稱改名（限 Engineer — 非 Engineer 時 #btnRenameCoordinator 不存在，以下短路）
     *
     * 後端 DB 先改（Id 不變 → SID / 歷史不變）再原子改 JSON 檔名，Engine 自動停舊名迴圈、以新名載入；
     * 成功後重新整理頁面（左側清單 / 選取狀態以新名重建）。設定檔已移除的設備不可改名。
     * ============================================================ */

    var btnRename = document.getElementById('btnRenameCoordinator');
    var SELECT_AFTER_RELOAD_KEY = 'modbuscoordinator.selectAfterReload';

    function setRenameStatus(szText, isError) {
        var el = document.getElementById('renameStatus');
        if (!el) return;
        el.textContent = szText;
        el.className = 'small mt-1 ' + (isError ? 'text-danger' : 'text-success');
        el.style.display = szText ? '' : 'none';
    }

    function updateRenameControls(szName) {
        if (!btnRename) return;
        var isMissing = !!szName && aMissingConfig.indexOf(szName.toLowerCase()) >= 0;
        btnRename.disabled = !szName || isMissing;
        var input = document.getElementById('fieldName');
        if (input) input.readOnly = !szName || isMissing;
        setRenameStatus('', false);
    }

    if (btnRename) {
        btnRename.addEventListener('click', async function () {
            var c = coordinators[nCurrentIndex];
            if (!c) return;
            var szOld = c.szName || '';
            var szNew = (document.getElementById('fieldName').value || '').trim();

            if (!szNew) { setRenameStatus(t('modbuscoordinator.msg.rename_empty'), true); return; }
            if (szNew.toLowerCase() === szOld.toLowerCase()) { setRenameStatus(t('modbuscoordinator.msg.rename_unchanged'), true); return; }
            if (!window.confirm(t('modbuscoordinator.confirm.rename', { old: szOld, new: szNew }))) return;

            btnRename.disabled = true;
            try {
                var resp = await fetch('/ModbusCoordinator/RenameCoordinator', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ oldName: szOld, newName: szNew })
                });
                var result = await resp.json();
                if (resp.ok && result.success) {
                    setRenameStatus(t('modbuscoordinator.msg.rename_success'), false);
                    if (result.workbookNotice) window.alert(result.workbookNotice);
                    try { sessionStorage.setItem(SELECT_AFTER_RELOAD_KEY, result.newName || szNew); } catch (e) { /* 無痕 / 封鎖儲存 → 重新整理後不自動選取 */ }
                    window.location.reload();
                    return;
                }
                setRenameStatus(result.message || t('modbuscoordinator.msg.save_failed'), true);
            } catch (ex) {
                setRenameStatus(t('modbuscoordinator.msg.save_failed_with', { error: ex.message }), true);
            }
            btnRename.disabled = false;
        });
    }

    /** 改名後重新整理：自動選回改名的那台設備 */
    function selectAfterReload() {
        var szName = null;
        try {
            szName = sessionStorage.getItem(SELECT_AFTER_RELOAD_KEY);
            sessionStorage.removeItem(SELECT_AFTER_RELOAD_KEY);
        } catch (e) { return false; }
        if (!szName) return false;

        for (var i = 0; i < coordinators.length; i++) {
            if ((coordinators[i].szName || '').toLowerCase() !== szName.toLowerCase()) continue;
            var el = document.querySelector('.coordinator-item[data-index="' + i + '"], .coordinator-toggle[data-index="' + i + '"]');
            if (!el) return false;
            var nIndex = i;
            clearAllActive();
            el.classList.add('active');
            var run = function () { nCurrentIndex = nIndex; showFullDetail(nIndex); };
            if (window.i18n && window.i18n.ready) window.i18n.ready(run); else run();
            return true;
        }
        return false;
    }

    /* ============================================================
     * 點位熱編輯（限 Admin — 非 Admin 時 #pointsModal / #btnOpenPoints 不存在，以下全部短路）
     *
     * 入口：設備詳情卡片標題列的「點位設定」按鈕 → 彈出 Modal。
     * 只准原地改 Name / Address / Ratio / Unit / Min / Max；
     * DataType 唯讀、禁止增刪與排序（SID 由陣列索引產生，結構一變即歷史錯位）。
     * ============================================================ */

    var pointsModalEl = document.getElementById('pointsModal');
    var btnOpenPoints = document.getElementById('btnOpenPoints');
    var szPointsCoordinator = null;   // 目前選中的 Coordinator 名稱（= JSON 檔名）
    var aOriginalPoints = [];         // 原始點位（存檔時 diff 用）

    function showOpenPointsButton(szName) {
        szPointsCoordinator = szName || null;
        if (btnOpenPoints) btnOpenPoints.style.display = szName ? '' : 'none';
    }

    function hideOpenPointsButton() {
        if (btnOpenPoints) btnOpenPoints.style.display = 'none';
    }

    /* ── 設定檔已移除標示 + 逐台刪除（Excel 匯入功能，共用 source-excel-import.js）── */
    var btnDeleteSource = document.getElementById('btnDeleteSource');
    var configMissingNote = document.getElementById('configMissingNote');

    /**
     * 依目前選中的設備切換「刪除設備」按鈕與「設定檔已移除」提示。
     * JSON 已移除的設備：隱藏點位設定（檔案不在了）與刪除按鈕，只顯示提示。
     */
    function updateSourceControls(szName) {
        var isMissing = !!szName && aMissingConfig.indexOf(szName.toLowerCase()) >= 0;
        if (configMissingNote) configMissingNote.style.display = isMissing ? '' : 'none';
        if (pendingEngineNote) pendingEngineNote.style.display = 'none';
        if (btnDeleteSource) btnDeleteSource.style.display = (szName && !isMissing) ? '' : 'none';
        if (isMissing) hideOpenPointsButton();
    }

    /* ── 「待 Engine 載入」項目（JSON 已匯入、DB 尚無紀錄）：只顯示提示 + 刪除按鈕 ── */
    var pendingEngineNote = document.getElementById('pendingEngineNote');
    document.querySelectorAll('.pending-item').forEach(function (item) {
        item.addEventListener('click', function (e) {
            e.preventDefault();
            clearAllActive();
            this.classList.add('active');
            var szName = this.dataset.name || '';

            var fullDetail = document.getElementById('fullDetail');
            var subDetail = document.getElementById('subDetail');
            if (fullDetail) fullDetail.style.display = 'none';
            if (subDetail) subDetail.style.display = 'none';
            document.getElementById('detailTitle').innerHTML =
                '<i class="fas fa-hourglass-half me-1"></i>' + t('modbuscoordinator.card.device_detail') + ' — ' + szName;

            if (configMissingNote) configMissingNote.style.display = 'none';
            if (pendingEngineNote) pendingEngineNote.style.display = '';
            hideOpenPointsButton();
            szPointsCoordinator = szName;                       // 讓刪除按鈕知道目前是哪個設定檔
            if (btnDeleteSource) btnDeleteSource.style.display = szName ? '' : 'none';
        });
    });

    if (btnDeleteSource) {
        btnDeleteSource.addEventListener('click', function () {
            if (!szPointsCoordinator || !window.SourceExcelImport) return;
            window.SourceExcelImport.deleteSource(szPointsCoordinator);
        });
    }

    if (btnOpenPoints && pointsModalEl) {
        btnOpenPoints.addEventListener('click', function () {
            if (!szPointsCoordinator) return;
            bootstrap.Modal.getOrCreateInstance(pointsModalEl).show();
            loadPoints(szPointsCoordinator);
        });
    }

    function setPointsStatus(szText, isError) {
        var el = document.getElementById('pointsStatus');
        if (!el) return;
        el.textContent = szText;
        el.className = 'small ' + (isError ? 'text-danger' : 'text-success');
        el.style.display = szText ? '' : 'none';
    }

    /**
     * Engine 支援的資料型態 — 與後端 ModbusConfigFileService.SupportedDataTypes
     * （＝ Engine ModbusTagModel.SupportedDataTypes）一致。
     * BIT0–BIT15 為「取暫存器內第 n 個位元」，以迴圈產生避免手寫漏項。
     *
     * UI 為兩段式：型別下拉只放 BIT_GROUP 一項（否則清單長達 26 項），選到它才顯示
     * 0–15 的位元索引下拉，兩者合成後寫進隱藏欄位。儲存格式仍是單一字串 BIT0–BIT15，
     * JSON 與後端白名單完全不變。
     */
    var BASE_TYPES = ['INTEGER', 'UINTEGER', 'FLOATINGPT', 'SWAPPEDFP', 'DOUBLE', 'SWAPPEDDOUBLE', 'UINT32BE',
                      'DEC10K3', 'BCD'];
    var BIT_GROUP = 'BIT';      // 僅存在於 UI 的群組代表值，本身不是合法 DataType
    var BIT_INDEX_MAX = 15;

    /** 型別下拉的選項（BIT 收斂為一項）*/
    var TYPE_OPTIONS = BASE_TYPES.concat([BIT_GROUP]);

    /** 攤平的合法型別清單（等同後端白名單），用於判斷原值是否為已知型別 */
    var DATA_TYPES = BASE_TYPES.slice();
    for (var nBit = 0; nBit <= BIT_INDEX_MAX; nBit++) DATA_TYPES.push('BIT' + nBit);

    /**
     * 解析 BIT0–BIT15 的位元索引，非 BIT 型別回 -1。
     * 刻意只認大寫精確格式（與後端白名單同規則）— 'Bit5' 這類舊寫法要落到「保留原值」路徑，
     * 才不會在使用者未操作的情況下被靜默改成 'BIT5' 而算成一筆變更。
     */
    function parseBitIndex(sz) {
        var m = /^BIT(\d{1,2})$/.exec(sz || '');
        if (!m) return -1;
        var n = parseInt(m[1], 10);
        return (n >= 0 && n <= BIT_INDEX_MAX) ? n : -1;
    }

    function makeOption(szValue) {
        var opt = document.createElement('option');
        opt.value = szValue;
        opt.textContent = szValue;
        return opt;
    }

    /**
     * DataType 欄 — 型別下拉 +（僅 BIT 時顯示的）位元索引下拉，兩者合成後寫進隱藏的
     * [data-field="dataType"]，collectAndValidatePoints / countChanges 照舊讀該欄位不需改。
     * 原值不在白名單（大小寫或舊格式）時保留為型別下拉的第一個選項，避免無操作也被視為變更。
     */
    function makeDataTypeCell(szCurrent) {
        var td = document.createElement('td');
        var group = document.createElement('div');
        group.className = 'point-type-group';

        var hidden = document.createElement('input');
        hidden.type = 'hidden';
        hidden.dataset.field = 'dataType';

        var nBitIndex = parseBitIndex(szCurrent);

        var selType = document.createElement('select');
        selType.className = 'form-select form-select-sm point-select point-select-type';
        if (DATA_TYPES.indexOf(szCurrent) < 0 && szCurrent !== '') {
            selType.appendChild(makeOption(szCurrent));
        }
        TYPE_OPTIONS.forEach(function (szType) { selType.appendChild(makeOption(szType)); });
        selType.value = nBitIndex >= 0 ? BIT_GROUP : szCurrent;

        var selBit = document.createElement('select');
        selBit.className = 'form-select form-select-sm point-select-bit';
        for (var i = 0; i <= BIT_INDEX_MAX; i++) selBit.appendChild(makeOption(String(i)));
        selBit.value = String(nBitIndex >= 0 ? nBitIndex : 0);

        function syncDataType() {
            var isBit = selType.value === BIT_GROUP;
            selBit.style.display = isBit ? '' : 'none';
            hidden.value = isBit ? BIT_GROUP + selBit.value : selType.value;
        }
        selType.addEventListener('change', syncDataType);
        selBit.addEventListener('change', syncDataType);
        syncDataType();

        group.appendChild(selType);
        group.appendChild(selBit);
        group.appendChild(hidden);
        td.appendChild(group);
        return td;
    }

    /**
     * Modbus 位址驗證 — 與後端 ModbusConfigFileService.IsValidAddress 一致：
     * 5 位數慣例（0xxxx Coil 1-9999、1xxxx Discrete、3xxxx Input、4xxxx Holding）
     * 或 6 位數擴充慣例（000001-065536 / 1xxxxx / 3xxxxx / 4xxxxx），靠字串長度（含前導 0）區分
     */
    function isValidAddress(sz) {
        if (!/^\d{1,6}$/.test(sz)) return false;
        var n = parseInt(sz, 10);
        if (sz.length === 6) {
            return (n >= 1 && n <= 65536) || (n >= 100001 && n <= 165536) ||
                   (n >= 300001 && n <= 365536) || (n >= 400001 && n <= 465536);
        }
        return (n >= 1 && n <= 9999) || (n >= 10000 && n <= 19999) ||
               (n >= 30000 && n <= 39999) || (n >= 40000 && n <= 49999);
    }

    function makeCellInput(szValue, szField, szCssExtra) {
        var td = document.createElement('td');
        var input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-control form-control-sm point-input' + (szCssExtra ? ' ' + szCssExtra : '');
        input.value = szValue == null ? '' : szValue;
        input.dataset.field = szField;
        input.autocomplete = 'off';
        td.appendChild(input);
        return td;
    }

    /**
     * Device（站號內子設備分群）欄 — 多站號 Coordinator 依決策 4 站號即設備、Tag.Device 不適用，
     * 故 disable + 提示，從源頭避免誤填。
     */
    function makeDeviceCell(szValue, isMulti) {
        var td = document.createElement('td');
        var input = document.createElement('input');
        input.type = 'text';
        input.className = 'form-control form-control-sm point-input';
        input.value = szValue == null ? '' : szValue;
        input.dataset.field = 'device';
        input.autocomplete = 'off';
        if (isMulti) {
            input.disabled = true;
            input.title = t('modbuscoordinator.points.device_multi_hint');
            input.placeholder = t('modbuscoordinator.points.device_na');
        }
        td.appendChild(input);
        return td;
    }

    function renderPoints(data) {
        var tbody = document.getElementById('pointsTbody');
        tbody.innerHTML = '';

        var infoEl = document.getElementById('pointsDeviceInfo');
        infoEl.textContent = t('modbuscoordinator.points.device_info', {
            ip: data.ip, port: data.port, modbusId: data.modbusId, timeout: data.connectTimeout
        });

        // 站號 / Device 互斥（決策 4）：多站號 Coordinator 的 Device 欄一律 disable
        var isMulti = (data.modbusId || '').indexOf(',') >= 0;

        (data.points || []).forEach(function (p, i) {
            var tr = document.createElement('tr');

            var tdSeq = document.createElement('td');
            tdSeq.className = 'text-center text-muted points-col-seq';
            tdSeq.textContent = i + 1;
            tr.appendChild(tdSeq);

            tr.appendChild(makeCellInput(p.name, 'name'));
            tr.appendChild(makeCellInput(p.address, 'address', 'point-input-address'));

            tr.appendChild(makeDataTypeCell(p.dataType || ''));

            tr.appendChild(makeCellInput(p.ratio, 'ratio', 'point-input-num'));
            tr.appendChild(makeCellInput(p.unit, 'unit', 'point-input-num'));
            tr.appendChild(makeCellInput(p.min, 'min', 'point-input-num'));
            tr.appendChild(makeCellInput(p.max, 'max', 'point-input-num'));
            tr.appendChild(makeDeviceCell(p.device || '', isMulti));

            tbody.appendChild(tr);
        });
    }

    async function loadPoints(szName) {
        if (!pointsModalEl || !szName) return;

        szPointsCoordinator = szName;
        aOriginalPoints = [];
        setPointsStatus('', false);

        var titleEl = document.getElementById('pointsTitle');
        titleEl.innerHTML = '<i class="fas fa-list-ol me-1"></i>';
        titleEl.appendChild(document.createTextNode(t('modbuscoordinator.points.title') + ' — ' + szName));

        document.getElementById('pointsTbody').innerHTML =
            '<tr><td colspan="9" class="text-center text-muted py-3">' + t('modbuscoordinator.points.msg_loading') + '</td></tr>';

        try {
            var resp = await fetch('/ModbusCoordinator/Points/' + encodeURIComponent(szName), { credentials: 'same-origin' });
            if (!resp.ok) throw new Error('HTTP ' + resp.status);
            var data = await resp.json();
            aOriginalPoints = data.points || [];
            renderPoints(data);
        } catch (ex) {
            document.getElementById('pointsTbody').innerHTML = '';
            setPointsStatus(t('modbuscoordinator.points.msg_load_failed') + ' (' + ex.message + ')', true);
        }
    }

    /** 從表格收集點位（DataType 取原始值 — 唯讀欄位不進輸入框）；驗證失敗回傳 null */
    function collectAndValidatePoints() {
        var rows = document.querySelectorAll('#pointsTbody tr');
        if (rows.length !== aOriginalPoints.length) return null;

        var aPoints = [];
        for (var i = 0; i < rows.length; i++) {
            var get = function (field) {
                var el = rows[i].querySelector('[data-field="' + field + '"]');
                return el ? el.value.trim() : '';
            };
            var p = {
                name: get('name'),
                address: get('address'),
                dataType: get('dataType'),
                ratio: get('ratio'),
                unit: get('unit'),
                min: get('min'),
                max: get('max'),
                device: get('device')
            };

            var nRow = i + 1;
            if (!p.name) {
                setPointsStatus(t('modbuscoordinator.points.err_name_empty', { row: nRow }), true);
                return null;
            }
            if (!isValidAddress(p.address)) {
                setPointsStatus(t('modbuscoordinator.points.err_address_invalid', { row: nRow }), true);
                return null;
            }
            if (p.ratio === '' || isNaN(Number(p.ratio))) {
                setPointsStatus(t('modbuscoordinator.points.err_ratio_invalid', { row: nRow }), true);
                return null;
            }
            if ((p.min !== '' && isNaN(Number(p.min))) || (p.max !== '' && isNaN(Number(p.max)))) {
                setPointsStatus(t('modbuscoordinator.points.err_minmax_invalid', { row: nRow }), true);
                return null;
            }
            aPoints.push(p);
        }
        return aPoints;
    }

    function countChanges(aPoints) {
        var nChanged = 0;
        for (var i = 0; i < aPoints.length; i++) {
            var o = aOriginalPoints[i], p = aPoints[i];
            if (o.name !== p.name || o.address !== p.address || (o.dataType || '') !== p.dataType ||
                o.ratio !== p.ratio || o.unit !== p.unit || o.min !== p.min || o.max !== p.max ||
                (o.device || '') !== (p.device || '')) nChanged++;
        }
        return nChanged;
    }

    var btnSavePoints = document.getElementById('btnSavePoints');
    if (btnSavePoints) {
        btnSavePoints.addEventListener('click', async function () {
            if (!szPointsCoordinator || aOriginalPoints.length === 0) return;

            var aPoints = collectAndValidatePoints();
            if (!aPoints) return;

            var nChanged = countChanges(aPoints);
            if (nChanged === 0) {
                setPointsStatus(t('modbuscoordinator.points.msg_no_change'), false);
                return;
            }

            if (!window.confirm(t('modbuscoordinator.points.confirm_save', { count: nChanged }))) return;

            btnSavePoints.disabled = true;
            try {
                var resp = await fetch('/ModbusCoordinator/UpdatePoints', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ coordinatorName: szPointsCoordinator, points: aPoints })
                });
                var result = await resp.json();
                if (resp.ok && result.success) {
                    aOriginalPoints = aPoints;
                    setPointsStatus(t('modbuscoordinator.points.msg_save_success'), false);
                    // 來源資料夾內的 Excel 未能自動重產（Excel 開著 / 有未匯入修改）
                    if (result.workbookNotice) window.alert(result.workbookNotice);
                } else {
                    setPointsStatus(result.message || t('modbuscoordinator.msg.save_failed'), true);
                }
            } catch (ex) {
                setPointsStatus(t('modbuscoordinator.msg.save_failed_with', { error: ex.message }), true);
            } finally {
                btnSavePoints.disabled = false;
            }
        });
    }

    var btnReloadPoints = document.getElementById('btnReloadPoints');
    if (btnReloadPoints) {
        btnReloadPoints.addEventListener('click', function () {
            if (szPointsCoordinator) loadPoints(szPointsCoordinator);
        });
    }

    // 預設：改名後選回該設備；否則若第一筆是單一 ModbusID，自動載入
    if (selectAfterReload()) {
        // 已選取
    } else if (coordinators.length > 0) {
        var first = coordinators[0];
        var ids = (first.szModbusID || '').split(',');
        if (ids.length <= 1) {
            var firstItem = document.querySelector('.coordinator-item');
            if (firstItem) {
                firstItem.classList.add('active');
                if (window.i18n && window.i18n.ready) {
                    window.i18n.ready(function () { showFullDetail(0); });
                } else {
                    showFullDetail(0);
                }
            }
        }
    }
})();
