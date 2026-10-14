/**
 * Excel 匯入 / 匯出 / 刪除 共用前端（Modbus 來源、DB 來源、OPC UA 來源三頁共用）。
 *
 * 流程：選檔 → POST {base}/ImportPreview（不寫檔）→ 預覽 Modal 列出 新增/覆寫/無變更/錯誤 + 刪除候選 + 其他既有
 *       → 使用者勾選 →（有覆寫或刪除時）確認 Modal 列出名稱 → POST {base}/ImportCommit → 重新整理頁面。
 * SID 位移（中間插入/刪除/重排）需另外勾選「我了解…」才能提交；
 * 「會蓋掉網頁修改」（JSON 的 WebEditedAt 晚於 Excel 的匯出時間）同樣需另外勾選。
 *
 * 匯出主按鈕寫進 Engine 來源資料夾（POST {base}/ExportToFolder），下拉保留「下載到瀏覽器」；
 * 匯入下拉可直接選來源資料夾內的 Excel（POST {base}/ImportPreviewFromFolder），資料夾內有未匯入修改時工具列顯示提示。
 *
 * 參數化：工具列 [data-srcxl-base] 帶 Controller 路由前綴（/ModbusCoordinator、/DbCoordinator、/OpcUaCoordinator）。
 * 對外：window.SourceExcelImport.deleteSource(name) — 各頁「刪除設備」按鈕呼叫（確認後 POST {base}/DeleteSource）。
 */
(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    var toolbar = document.querySelector('[data-srcxl-base]');
    if (!toolbar) return;

    var szBase = toolbar.dataset.srcxlBase;
    var previewModalEl = document.getElementById('srcxlPreviewModal');
    var confirmModalEl = document.getElementById('srcxlConfirmModal');
    var fileInput = document.getElementById('srcxlFileInput');
    var btnImport = document.getElementById('srcxlBtnImport');
    var btnCommit = document.getElementById('srcxlBtnCommit');

    var preview = null;          // 最近一次預覽結果
    var szNoSelectableMsg = null; // 非 null = 這份預覽完全沒有可勾選項目（狀態列顯示原因而非「尚未勾選」）
    var isCommitting = false;
    var confirmCallback = null;  // 確認 Modal 按「確定」時要跑的函數

    /* ───────────── 共用 UI helper ───────────── */

    function setStatus(szText, isError) {
        var el = document.getElementById('srcxlPreviewStatus');
        if (!el) return;
        el.textContent = szText || '';
        el.className = 'small me-auto ' + (isError ? 'text-danger' : 'text-success');
    }

    function showConfirm(szHtml, onYes) {
        document.getElementById('srcxlConfirmBody').innerHTML = szHtml;
        confirmCallback = onYes;
        bootstrap.Modal.getOrCreateInstance(confirmModalEl).show();
    }

    document.getElementById('srcxlBtnConfirmYes').addEventListener('click', function () {
        bootstrap.Modal.getOrCreateInstance(confirmModalEl).hide();
        var cb = confirmCallback;
        confirmCallback = null;
        if (cb) cb();
    });

    function kindBadge(szKind) {
        var map = {
            Added:     ['bg-success', 'srcxl.kind.added'],
            Overwrite: ['bg-warning text-dark', 'srcxl.kind.overwrite'],
            Unchanged: ['bg-secondary', 'srcxl.kind.unchanged'],
            Error:     ['bg-danger', 'srcxl.kind.error']
        };
        var m = map[szKind] || ['bg-secondary', szKind];
        return '<span class="badge ' + m[0] + '">' + escapeHtml(t(m[1])) + '</span>';
    }

    function listHtml(items, szClass, nMax) {
        if (!items || !items.length) return '';
        var shown = items.slice(0, nMax);
        var html = '<ul class="srcxl-list ' + (szClass || '') + '">' +
            shown.map(function (s) { return '<li>' + escapeHtml(s) + '</li>'; }).join('') + '</ul>';
        if (items.length > nMax) {
            html += '<div class="text-muted">' + escapeHtml(t('srcxl.detail.more', { count: items.length - nMax })) + '</div>';
        }
        return html;
    }

    /* ───────────── 上傳 → 預覽 ───────────── */

    btnImport.addEventListener('click', function () { fileInput.value = ''; fileInput.click(); });

    fileInput.addEventListener('change', function () {
        var file = fileInput.files && fileInput.files[0];
        if (!file) return;
        uploadForPreview(file);
    });

    function uploadForPreview(file) {
        startPreview(file.name, function () {
            var form = new FormData();
            form.append('file', file, file.name);
            return fetch(szBase + '/ImportPreview', { method: 'POST', body: form, credentials: 'same-origin' });
        });
    }

    /** 以來源資料夾內的 Excel 預覽（伺服器直接讀檔，不經瀏覽器上傳）；之後的勾選 / 提交流程相同 */
    function previewFromFolder(szFileName) {
        startPreview(szFileName, function () {
            return fetch(szBase + '/ImportPreviewFromFolder', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ fileName: szFileName })
            });
        });
    }

    /** 開預覽 Modal → 呼叫 request()（回傳 fetch Promise）→ 渲染預覽結果 */
    async function startPreview(szDisplayName, request) {
        preview = null;
        szNoSelectableMsg = null;
        document.getElementById('srcxlWorkbookName').textContent = szDisplayName;
        document.getElementById('srcxlPreviewLoading').style.display = '';
        document.getElementById('srcxlPreviewBody').style.display = 'none';
        document.getElementById('srcxlPreviewError').style.display = 'none';
        document.getElementById('srcxlAckSidShift').checked = false;
        document.getElementById('srcxlAckWebEdit').checked = false;
        btnCommit.disabled = true;
        setStatus('', false);
        bootstrap.Modal.getOrCreateInstance(previewModalEl).show();

        try {
            var resp = await request();
            var data = null;
            try { data = await resp.json(); } catch (e) { /* 非 JSON（如 413/500 頁面） */ }

            document.getElementById('srcxlPreviewLoading').style.display = 'none';
            document.getElementById('srcxlPreviewBody').style.display = '';

            if (!resp.ok || !data || !data.success) {
                var errEl = document.getElementById('srcxlPreviewError');
                errEl.textContent = (data && data.message) || t('srcxl.msg.preview_failed', { status: resp.status });
                errEl.style.display = '';
                clearTables();
                return;
            }

            preview = data;
            renderPreview(data);
        } catch (ex) {
            document.getElementById('srcxlPreviewLoading').style.display = 'none';
            document.getElementById('srcxlPreviewBody').style.display = '';
            var errEl2 = document.getElementById('srcxlPreviewError');
            errEl2.textContent = t('srcxl.msg.call_failed_with', { error: ex.message });
            errEl2.style.display = '';
            clearTables();
        }
    }

    function clearTables() {
        document.getElementById('srcxlSheetsBody').innerHTML = '';
        document.getElementById('srcxlDeleteBody').innerHTML = '';
        document.getElementById('srcxlOtherBody').innerHTML = '';
        document.getElementById('srcxlDeleteSection').style.display = 'none';
        document.getElementById('srcxlOtherSection').style.display = 'none';
        document.getElementById('srcxlSidShiftBox').style.display = 'none';
        document.getElementById('srcxlWebEditBox').style.display = 'none';
        document.getElementById('srcxlNoSelectableBox').style.display = 'none';
        document.getElementById('srcxlExportedAt').textContent = '';
    }

    /**
     * 整份預覽沒有任何可勾選項目（工作表全是錯誤／無變更、也沒有刪除候選）時，
     * 頂部提示從「勾選後匯入」換成說明原因，避免使用者以為勾選框壞掉。
     */
    function refreshNoSelectableHint(data) {
        var box = document.getElementById('srcxlNoSelectableBox');
        var nSelectable = document.querySelectorAll('.srcxl-sheet-check:not(:disabled), .srcxl-del-check, .srcxl-other-check').length;
        if (nSelectable > 0) { box.style.display = 'none'; return null; }

        var nErrors = (data.sheets || []).filter(function (s) { return s.kind === 'Error'; }).length;
        var szMsg = nErrors > 0
            ? t('srcxl.msg.no_selectable_errors', { count: nErrors })
            : t('srcxl.msg.no_selectable_unchanged');
        box.textContent = szMsg;
        box.style.display = '';
        return szMsg;
    }

    /* ───────────── 預覽渲染 ───────────── */

    function renderPreview(data) {
        // 活頁簿匯出時間：沒有 = 範本 / 手工建立 / 從別的檔複製工作表（覆寫網頁修改時會要求確認）
        document.getElementById('srcxlExportedAt').textContent = data.exportedAt
            ? t('srcxl.preview.exported_at', { time: data.exportedAt })
            : t('srcxl.preview.exported_unknown');

        var tbody = document.getElementById('srcxlSheetsBody');
        tbody.innerHTML = (data.sheets || []).map(function (s, i) {
            var isSelectable = s.kind === 'Added' || s.kind === 'Overwrite';
            var detail = '';

            if (s.renameHintFrom) {
                detail += '<div class="text-primary"><i class="fas fa-info-circle me-1"></i>' +
                    escapeHtml(t('srcxl.detail.rename_hint', { from: s.renameHintFrom })) + '</div>';
            }
            if (s.existingSourceWorkbook && s.kind !== 'Added') {
                detail += '<div class="text-muted">' + escapeHtml(t('srcxl.detail.source_workbook', { name: s.existingSourceWorkbook })) + '</div>';
            }
            if (s.hasWebEditConflict) {
                detail += '<div class="text-danger fw-bold"><i class="fas fa-exclamation-triangle me-1"></i>' +
                    escapeHtml(t('srcxl.detail.web_edited', { time: s.webEditedAt || '' })) + '</div>';
            }
            if (s.hasSidShift) {
                detail += '<div class="text-danger fw-bold"><i class="fas fa-exclamation-triangle me-1"></i>' +
                    escapeHtml(t('srcxl.detail.sid_shift', { index: s.sidShiftFromIndex })) + '</div>';
            } else if (s.tailRemovedCount > 0) {
                detail += '<div class="text-warning"><i class="fas fa-exclamation-triangle me-1"></i>' +
                    escapeHtml(t('srcxl.detail.tail_removed', { count: s.tailRemovedCount })) + '</div>';
            }
            if (s.keyedRemovedCount > 0) {
                detail += '<div class="text-warning"><i class="fas fa-exclamation-triangle me-1"></i>' +
                    escapeHtml(t('srcxl.detail.keyed_removed', { count: s.keyedRemovedCount })) + '</div>';
            }
            detail += listHtml(s.headerChanges, 'srcxl-list-change', 10);
            var pointLines = (s.pointChanges || []).map(function (c) {
                var szLabel = c.change === 'Added' ? t('srcxl.change.added')
                            : c.change === 'Removed' ? t('srcxl.change.removed') : '';
                return '#' + c.index + ' ' + c.name + (szLabel ? ' ' + szLabel : '') + (c.summary ? ' — ' + c.summary : '');
            });
            detail += listHtml(pointLines, 'srcxl-list-change', 10);
            detail += listHtml((s.errors || []).map(function (e) { return e.message; }), 'srcxl-list-error', 20);
            detail += listHtml((s.warnings || []).map(function (w) { return w.message; }), 'srcxl-list-warn text-muted', 5);
            if (!detail && s.kind === 'Unchanged') detail = '<span class="text-muted">—</span>';

            var szPoints = s.kind === 'Added' ? String(s.newPointCount)
                         : (s.oldPointCount === s.newPointCount ? String(s.newPointCount) : s.oldPointCount + ' → ' + s.newPointCount);

            return '<tr class="' + (s.kind === 'Error' ? 'table-danger' : '') + '">' +
                '<td class="srcxl-col-check"><input type="checkbox" class="form-check-input srcxl-sheet-check" data-index="' + i + '"' +
                    (isSelectable ? '' : ' disabled') + (s.defaultChecked ? ' checked' : '') + ' /></td>' +
                '<td class="fw-bold">' + escapeHtml(s.sheetName) + '</td>' +
                '<td>' + kindBadge(s.kind) + '</td>' +
                '<td class="text-nowrap">' + escapeHtml(szPoints) + '</td>' +
                '<td class="srcxl-detail">' + detail + '</td>' +
            '</tr>';
        }).join('');

        // 刪除候選
        var delSection = document.getElementById('srcxlDeleteSection');
        var delBody = document.getElementById('srcxlDeleteBody');
        if (data.deleteCandidates && data.deleteCandidates.length) {
            delSection.style.display = '';
            delBody.innerHTML = data.deleteCandidates.map(function (d, i) { return existingRow(d, 'srcxl-del-check', i); }).join('');
        } else {
            delSection.style.display = 'none';
            delBody.innerHTML = '';
        }

        // 其他未包含
        var otherSection = document.getElementById('srcxlOtherSection');
        var otherBody = document.getElementById('srcxlOtherBody');
        if (data.otherExisting && data.otherExisting.length) {
            otherSection.style.display = '';
            document.getElementById('srcxlOtherCount').textContent = data.otherExisting.length;
            otherBody.innerHTML = data.otherExisting.map(function (d, i) { return existingRow(d, 'srcxl-other-check', i); }).join('');
        } else {
            otherSection.style.display = 'none';
            otherBody.innerHTML = '';
        }

        tbody.querySelectorAll('.srcxl-sheet-check').forEach(function (cb) { cb.addEventListener('change', refreshCommitState); });
        delBody.querySelectorAll('input').forEach(function (cb) { cb.addEventListener('change', refreshCommitState); });
        otherBody.querySelectorAll('input').forEach(function (cb) { cb.addEventListener('change', refreshCommitState); });
        szNoSelectableMsg = refreshNoSelectableHint(data);
        refreshCommitState();
    }

    function existingRow(d, szClass, i) {
        var szSource = d.sourceWorkbook
            ? t('srcxl.detail.source_workbook', { name: d.sourceWorkbook })
            : t('srcxl.detail.source_unknown');
        return '<tr>' +
            '<td class="srcxl-col-check"><input type="checkbox" class="form-check-input ' + szClass + '" data-name="' + escapeHtml(d.name) + '"' +
                (d.defaultChecked ? ' checked' : '') + ' /></td>' +
            '<td class="fw-bold">' + escapeHtml(d.name) + '</td>' +
            '<td class="text-muted small">' + escapeHtml(szSource) + '</td>' +
        '</tr>';
    }

    document.getElementById('srcxlCheckAllSheets').addEventListener('change', function () {
        var isChecked = this.checked;
        document.querySelectorAll('.srcxl-sheet-check:not(:disabled)').forEach(function (cb) { cb.checked = isChecked; });
        refreshCommitState();
    });

    document.getElementById('srcxlAckSidShift').addEventListener('change', refreshCommitState);
    document.getElementById('srcxlAckWebEdit').addEventListener('change', refreshCommitState);

    function getSelection() {
        var importSheets = [];
        var overwriteNames = [];
        var isShift = false;
        var isWebEdit = false;
        document.querySelectorAll('.srcxl-sheet-check:checked').forEach(function (cb) {
            var s = preview.sheets[parseInt(cb.dataset.index, 10)];
            importSheets.push(s.sheetName);
            if (s.kind === 'Overwrite') overwriteNames.push(s.sheetName);
            if (s.hasSidShift) isShift = true;
            if (s.hasWebEditConflict) isWebEdit = true;
        });
        var deleteNames = [];
        document.querySelectorAll('.srcxl-del-check:checked, .srcxl-other-check:checked').forEach(function (cb) {
            deleteNames.push(cb.dataset.name);
        });
        return { importSheets: importSheets, overwriteNames: overwriteNames, deleteNames: deleteNames, hasSidShift: isShift, hasWebEditConflict: isWebEdit };
    }

    function refreshCommitState() {
        if (!preview) { btnCommit.disabled = true; return; }
        var sel = getSelection();
        var shiftBox = document.getElementById('srcxlSidShiftBox');
        shiftBox.style.display = sel.hasSidShift ? '' : 'none';
        var webEditBox = document.getElementById('srcxlWebEditBox');
        webEditBox.style.display = sel.hasWebEditConflict ? '' : 'none';
        var isAckOk = (!sel.hasSidShift || document.getElementById('srcxlAckSidShift').checked)
                   && (!sel.hasWebEditConflict || document.getElementById('srcxlAckWebEdit').checked);
        var nTotal = sel.importSheets.length + sel.deleteNames.length;
        btnCommit.disabled = isCommitting || nTotal === 0 || !isAckOk;
        if (nTotal === 0 && szNoSelectableMsg) {
            setStatus(szNoSelectableMsg, true);
            return;
        }
        setStatus(nTotal === 0 ? t('srcxl.msg.nothing_selected') : t('srcxl.msg.selection_summary', {
            imports: sel.importSheets.length, deletes: sel.deleteNames.length
        }), false);
    }

    /* ───────────── 提交 ───────────── */

    btnCommit.addEventListener('click', function () {
        if (!preview || isCommitting) return;
        var sel = getSelection();

        if (sel.overwriteNames.length === 0 && sel.deleteNames.length === 0) {
            commit(sel);
            return;
        }

        var html = '';
        if (sel.overwriteNames.length) {
            html += '<div class="fw-bold mb-1">' + escapeHtml(t('srcxl.confirm.overwrite_list', { count: sel.overwriteNames.length })) + '</div>' +
                listHtml(sel.overwriteNames, 'mb-2', 50);
        }
        if (sel.deleteNames.length) {
            html += '<div class="fw-bold text-danger mb-1">' + escapeHtml(t('srcxl.confirm.delete_list', { count: sel.deleteNames.length })) + '</div>' +
                listHtml(sel.deleteNames, 'mb-2', 50) +
                '<div class="text-muted">' + escapeHtml(t('srcxl.confirm.delete_note')) + '</div>';
        }
        showConfirm(html, function () { commit(sel); });
    });

    async function commit(sel) {
        isCommitting = true;
        btnCommit.disabled = true;
        setStatus(t('srcxl.msg.committing'), false);

        try {
            var resp = await fetch(szBase + '/ImportCommit', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({
                    token: preview.token,
                    importSheets: sel.importSheets,
                    deleteNames: sel.deleteNames,
                    acknowledgeSidShift: document.getElementById('srcxlAckSidShift').checked,
                    acknowledgeWebEdit: document.getElementById('srcxlAckWebEdit').checked
                })
            });
            var data = null;
            try { data = await resp.json(); } catch (e) { /* ignore */ }

            if (resp.ok && data && data.success) {
                setStatus(data.message || t('srcxl.msg.commit_success'), false);
                // 資料夾內 Excel 未能自動重產（Excel 開著等）→ 頁面重新整理前先告知
                if (data.workbookNotice) window.alert(data.workbookNotice);
                // 不能立刻 reload：Engine watcher 去抖 1 秒 + 延遲 0.5 秒才寫 DB，太早渲染會把新設備判成「待 Engine 載入」
                waitForEngineThenReload(sel.importSheets, function (nRemaining) {
                    setStatus(t('srcxl.msg.waiting_engine', { count: nRemaining }), false);
                });
                return;
            }

            isCommitting = false;
            setStatus((data && data.message) || t('srcxl.msg.commit_failed', { status: resp.status }), true);
            if (resp.status === 409) {
                // 預覽後設定已被更動 → 必須重新上傳
                preview = null;
                btnCommit.disabled = true;
            } else {
                refreshCommitState();
            }
        } catch (ex) {
            isCommitting = false;
            setStatus(t('srcxl.msg.call_failed_with', { error: ex.message }), true);
            refreshCommitState();
        }
    }

    /* ───────────── 逐台刪除（各頁按鈕呼叫） ───────────── */

    function deleteSource(szName, onDone) {
        if (!szName) return;
        var html = '<div class="fw-bold text-danger mb-1">' + escapeHtml(t('srcxl.confirm.delete_one', { name: szName })) + '</div>' +
            '<div class="text-muted">' + escapeHtml(t('srcxl.confirm.delete_note')) + '</div>';

        showConfirm(html, async function () {
            try {
                var resp = await fetch(szBase + '/DeleteSource', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ name: szName })
                });
                var data = null;
                try { data = await resp.json(); } catch (e) { /* ignore */ }
                var szMsg = (data && data.message) || (resp.ok ? t('srcxl.msg.delete_success') : t('srcxl.msg.delete_failed', { status: resp.status }));
                if (data && data.workbookNotice) szMsg += '\n\n' + data.workbookNotice;
                if (typeof onDone === 'function') onDone(resp.ok && data && data.success, szMsg);
                else {
                    window.alert(szMsg);
                    if (resp.ok && data && data.success) window.location.reload();
                }
            } catch (ex) {
                var szErr = t('srcxl.msg.call_failed_with', { error: ex.message });
                if (typeof onDone === 'function') onDone(false, szErr); else window.alert(szErr);
            }
        });
    }

    /* ───────────── 匯出到來源資料夾 ─────────────
     * POST {base}/ExportToFolder?workbook=&force= → 伺服器寫進 Engine 來源資料夾（固定檔名覆寫、留 .bak），
     * 回傳實際完整路徑（前端只顯示伺服器訊息，不自行組路徑）。
     * 409 + needsConfirm：同名檔有未匯入修改 / 非系統匯出 → 確認後帶 force=true 重送；409 + locked：Excel 開著。
     */
    async function exportToFolder(szWorkbook, isForce) {
        var szUrl = szBase + '/ExportToFolder?force=' + (isForce ? 'true' : 'false') +
            (szWorkbook ? '&workbook=' + encodeURIComponent(szWorkbook) : '');
        try {
            var resp = await fetch(szUrl, { method: 'POST', credentials: 'same-origin' });
            var data = null;
            try { data = await resp.json(); } catch (e) { /* ignore */ }

            if (resp.ok && data && data.success) {
                window.alert(data.message);
                refreshPendingHint();
                return;
            }
            if (resp.status === 409 && data && data.needsConfirm) {
                showConfirm('<div>' + escapeHtml(data.message) + '</div>', function () { exportToFolder(szWorkbook, true); });
                return;
            }
            window.alert((data && data.message) || t('srcxl.msg.export_failed', { status: resp.status }));
        } catch (ex) {
            window.alert(t('srcxl.msg.call_failed_with', { error: ex.message }));
        }
    }

    document.getElementById('srcxlBtnExport').addEventListener('click', function () { exportToFolder('', false); });
    toolbar.querySelectorAll('.srcxl-export-folder').forEach(function (btn) {
        btn.addEventListener('click', function () { exportToFolder(btn.dataset.workbook, false); });
    });

    /* ───────────── 從來源資料夾匯入 + 未匯入修改提示（GET {base}/FolderWorkbooks） ───────────── */

    var folderMenu = document.getElementById('srcxlFolderMenu');

    async function fetchFolderWorkbooks() {
        var resp = await fetch(szBase + '/FolderWorkbooks', { credentials: 'same-origin', cache: 'no-store' });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        return resp.json();
    }

    function setFolderMenuItems(szHtml) {
        folderMenu.querySelectorAll('.srcxl-folder-item').forEach(function (li) { li.remove(); });
        folderMenu.insertAdjacentHTML('beforeend', szHtml);
    }

    function folderMenuText(szText) {
        return '<li class="srcxl-folder-item"><span class="dropdown-item-text small text-muted">' + escapeHtml(szText) + '</span></li>';
    }

    async function loadFolderMenu() {
        setFolderMenuItems(folderMenuText(t('srcxl.import.folder_loading')));
        try {
            var list = await fetchFolderWorkbooks();
            renderPendingHint(list);
            if (!list.length) { setFolderMenuItems(folderMenuText(t('srcxl.import.folder_empty'))); return; }
            setFolderMenuItems(list.map(function (w) {
                return '<li class="srcxl-folder-item"><button type="button" class="dropdown-item small" data-file="' + escapeHtml(w.name) + '">' +
                    escapeHtml(w.name) +
                    (w.hasUnimportedChanges ? ' <span class="badge bg-warning text-dark">' + escapeHtml(t('srcxl.import.unimported')) + '</span>' : '') +
                    '<div class="text-muted srcxl-folder-meta">' + escapeHtml(t('srcxl.import.modified_at', { time: w.modifiedAt })) + '</div>' +
                '</button></li>';
            }).join(''));
        } catch (ex) {
            setFolderMenuItems(folderMenuText(t('srcxl.msg.folder_list_failed')));
        }
    }

    document.getElementById('srcxlBtnFolderMenu').addEventListener('show.bs.dropdown', loadFolderMenu);
    folderMenu.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-file]');
        if (btn) previewFromFolder(btn.dataset.file);
    });

    /** 列出「匯出後又在 Excel 存檔、尚未匯入」的檔名（點檔名直接開預覽）；沒有就隱藏 */
    function renderPendingHint(list) {
        var box = document.getElementById('srcxlPendingHint');
        var pending = (list || []).filter(function (w) { return w.hasUnimportedChanges; });
        if (!pending.length) { box.style.display = 'none'; return; }
        document.getElementById('srcxlPendingList').innerHTML = pending.map(function (w) {
            return '<a href="#" class="srcxl-pending-link ms-1" data-file="' + escapeHtml(w.name) + '">' + escapeHtml(w.name) + '</a>';
        }).join('、');
        box.style.display = '';
    }

    function refreshPendingHint() {
        fetchFolderWorkbooks().then(renderPendingHint).catch(function () { /* 提示非必要，失敗就不顯示 */ });
    }

    document.getElementById('srcxlPendingList').addEventListener('click', function (e) {
        var link = e.target.closest('[data-file]');
        if (!link) return;
        e.preventDefault();
        previewFromFolder(link.dataset.file);
    });

    refreshPendingHint();

    /* ───────────── 等 Engine 載入（輪詢 {base}/SourceStatus） ─────────────
     * 匯入寫檔後 Engine 才非同步載入並寫 DB；頁面清單讀的是 DB。
     * 用 loaded 名單判斷哪些名稱已被 Engine 載入：全部到齊（或逾時）就 reload。
     */
    var POLL_INTERVAL_MS = 1000;
    var POLL_MAX_MS = 20000;

    async function fetchStatus() {
        var resp = await fetch(szBase + '/SourceStatus', { credentials: 'same-origin', cache: 'no-store' });
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        return resp.json();
    }

    /**
     * 等 names 全部出現在 Engine 已載入清單再 reload；逾時也 reload（頁面會以「待 Engine 載入」呈現剩下的）。
     * onTick(nRemaining) 每輪回呼，供顯示進度。
     */
    function waitForEngineThenReload(names, onTick) {
        var pending = (names || []).map(function (n) { return String(n).toLowerCase(); });
        if (pending.length === 0) { window.location.reload(); return; }
        var nStart = Date.now();

        async function tick() {
            try {
                var st = await fetchStatus();
                var loaded = (st.loaded || []).map(function (n) { return String(n).toLowerCase(); });
                pending = pending.filter(function (n) { return loaded.indexOf(n) < 0; });
            } catch (e) { /* 網路抖動就下一輪再試 */ }

            if (pending.length === 0 || Date.now() - nStart > POLL_MAX_MS) {
                window.location.reload();
                return;
            }
            if (typeof onTick === 'function') onTick(pending.length);
            setTimeout(tick, POLL_INTERVAL_MS);
        }
        setTimeout(tick, POLL_INTERVAL_MS);
    }

    /**
     * 頁面載入時若有「待 Engine 載入」項目（JSON 在、DB 沒有），背景輪詢最多 60 秒，
     * 任一項被 Engine 載入就 reload 讓它變成正常設備；逾時就停（可能是設定驗證失敗，留著提示給使用者看）。
     */
    function watchPending(names) {
        var pending = (names || []).map(function (n) { return String(n).toLowerCase(); });
        if (pending.length === 0) return;
        var nStart = Date.now();

        async function tick() {
            try {
                var st = await fetchStatus();
                var loaded = (st.loaded || []).map(function (n) { return String(n).toLowerCase(); });
                if (pending.some(function (n) { return loaded.indexOf(n) >= 0; })) {
                    window.location.reload();
                    return;
                }
            } catch (e) { /* ignore */ }
            if (Date.now() - nStart < 60000) setTimeout(tick, 3000);
        }
        setTimeout(tick, 3000);
    }

    // 頁面可在載入本檔前以 window._srcxlPendingNames 交付待載入名單（Modbus 頁由 Razor 內嵌）
    if (Array.isArray(window._srcxlPendingNames)) watchPending(window._srcxlPendingNames);

    window.SourceExcelImport = { deleteSource: deleteSource, watchPending: watchPending, waitForEngineThenReload: waitForEngineThenReload };
})();
