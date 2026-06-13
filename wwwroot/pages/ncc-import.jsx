// pages/ncc-import.jsx — Bóc tách + chuẩn hoá file NCC để import vào hệ thống.
//
// Flow: user upload (.xlsx / .csv / .txt) HOẶC dán text →
//   POST /api/v1/ncc-import/extract → preview bảng 13 cột (sửa được) →
//   POST /api/v1/ncc-import/export → tải file Excel chuẩn (file_import_ncc.xlsx)
// Style: orange brand + white cards + sidebar app shell (giống các page khác).

(function () {
  'use strict';
  const { useState, useEffect, useRef } = React;
  const fmtNum = window.fmtNum || ((n) => Number(n || 0).toLocaleString('vi-VN'));

  function NccImportPage({ pushToast }) {
    const [meta, setMeta] = useState({ types: [], statuses: ['Hoạt động', 'Ngừng'] });
    const [rows, setRows] = useState([]);
    const [source, setSource] = useState(null);
    const [busy, setBusy] = useState(false);
    const [drag, setDrag] = useState(false);
    const [text, setText] = useState('');
    const [showText, setShowText] = useState(false);
    const fileRef = useRef(null);

    // Pull enum types/statuses từ backend
    useEffect(() => {
      fetch('/api/v1/ncc-import/meta')
        .then(r => r.json())
        .then(m => setMeta({
          types: m.types || [],
          statuses: m.statuses || ['Hoạt động', 'Ngừng']
        }))
        .catch(() => {});
    }, []);

    const handleFile = async (file) => {
      if (!file) return;
      setBusy(true);
      try {
        const fd = new FormData();
        fd.append('file', file);
        const r = await fetch('/api/v1/ncc-import/extract', { method: 'POST', body: fd });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        setRows(j.rows || []);
        setSource(j.source);
        pushToast?.(`✓ Bóc tách ${j.cleanedRowCount}/${j.rawRowCount} NCC từ ${file.name}`);
      } catch (e) {
        pushToast?.('Lỗi: ' + e.message, 'error');
      } finally { setBusy(false); }
    };

    const handleText = async () => {
      const t = text.trim();
      if (!t) { pushToast?.('Hãy dán nội dung trước', 'warn'); return; }
      setBusy(true);
      try {
        const r = await fetch('/api/v1/ncc-import/extract', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: t })
        });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        setRows(j.rows || []);
        setSource(j.source);
        setShowText(false);
        pushToast?.(`✓ AI bóc ${j.rows?.length || 0} NCC từ văn bản dán (${j.latencyMs}ms)`);
      } catch (e) {
        pushToast?.('Lỗi AI: ' + e.message, 'error');
      } finally { setBusy(false); }
    };

    const onDrop = (e) => {
      e.preventDefault(); setDrag(false);
      const f = e.dataTransfer.files?.[0];
      if (f) handleFile(f);
    };

    const updateRow = (i, key, val) => {
      setRows(rs => rs.map((r, idx) => idx === i ? { ...r, [key]: val } : r));
    };
    const removeRow = (i) => setRows(rs => rs.filter((_, idx) => idx !== i));
    const addRow = () => setRows(rs => [...rs, {
      code: '', name: '', phone: '', email: '', type: '',
      quantity: 0, totalBuy: 0, paid: 0, collected: 0, owed: 0, balance: 0,
      status: 'Hoạt động'
    }]);

    const exportXlsx = async () => {
      if (rows.length === 0) { pushToast?.('Chưa có dòng nào', 'warn'); return; }
      setBusy(true);
      try {
        const r = await fetch('/api/v1/ncc-import/export', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ rows })
        });
        if (!r.ok) throw new Error('Export lỗi (' + r.status + ')');
        const blob = await r.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        const stamp = new Date().toISOString().slice(0, 16).replace(/[-T:]/g, '');
        a.download = `file_import_ncc_${stamp}.xlsx`;
        a.click();
        URL.revokeObjectURL(url);
        pushToast?.(`✓ Đã tải file ${rows.length} NCC`);
      } catch (e) {
        pushToast?.('Lỗi: ' + e.message, 'error');
      } finally { setBusy(false); }
    };

    return (
      <main className="page nccim">
        <header className="page-head">
          <div>
            <h1>Bóc tách file NCC</h1>
            <p className="page-sub">Upload Excel/CSV hoặc dán text, AI chuẩn hoá về 13 cột chuẩn rồi xuất file mẫu để import lên hệ thống.</p>
          </div>
          <div className="page-head-actions">
            <a className="btn btn-ghost btn-sm" href="/api/v1/ncc-import/template" target="_blank">
              <Icon name="paper" size={14} stroke={2.2} /> Tải template mẫu
            </a>
          </div>
        </header>

        {/* DROP ZONE — initial state (chưa có rows) */}
        {rows.length === 0 && !busy && (
          <section className="nccim-stage">
            <div
              className={'nccim-drop' + (drag ? ' is-drag' : '')}
              onDragOver={(e) => { e.preventDefault(); setDrag(true); }}
              onDragLeave={() => setDrag(false)}
              onDrop={onDrop}
              onClick={() => fileRef.current?.click()}
              role="button"
              tabIndex={0}
            >
              <input ref={fileRef} type="file" accept=".xlsx,.xls,.pdf,.docx,.doc,.csv,.txt"
                style={{ display: 'none' }} onChange={e => handleFile(e.target.files?.[0])} />
              <div className="nccim-drop-icon">
                {/* Inline upload SVG — bộ icons project chưa có "upload" */}
                <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                  strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                  <polyline points="17 8 12 3 7 8" />
                  <line x1="12" y1="3" x2="12" y2="15" />
                </svg>
              </div>
              <h3>Kéo thả file vào đây</h3>
              <p>hoặc <strong>click để chọn</strong>. Hỗ trợ <code>.xlsx</code> <code>.pdf</code> <code>.docx</code> <code>.csv</code> <code>.txt</code>.</p>
              <div className="nccim-drop-hint">
                <span><Icon name="check" size={12} stroke={2.6} /> Excel / CSV: tách trực tiếp</span>
                <span><Icon name="sparkle" size={12} stroke={2.4} /> PDF / Word / Text: AI bóc</span>
              </div>
            </div>

            <div className="nccim-or">
              <span>HOẶC</span>
            </div>

            <button className="nccim-paste-btn" onClick={() => setShowText(s => !s)}>
              <Icon name="paper" size={14} stroke={2.2} /> Dán văn bản tự do (Word / PDF copy)
            </button>

            {showText && (
              <div className="nccim-paste">
                <textarea
                  value={text}
                  onChange={e => setText(e.target.value)}
                  rows={8}
                  placeholder={'Ví dụ:\nKhách sạn Mường Thanh, 0901234567, quỹ phòng\nXe Ford Transit, 0912345678, nhà xe\n...'}
                />
                <div className="nccim-paste-actions">
                  <button className="btn btn-ghost btn-sm" onClick={() => { setShowText(false); setText(''); }}>Hủy</button>
                  <button className="btn btn-primary btn-sm" onClick={handleText} disabled={!text.trim()}>
                    <Icon name="sparkle" size={14} stroke={2.4} /> AI bóc tách
                  </button>
                </div>
              </div>
            )}
          </section>
        )}

        {/* BUSY */}
        {busy && (
          <div className="nccim-busy">
            <div className="nccim-spinner" />
            <p>Đang xử lý {source === 'ai-text' ? 'qua AI' : 'file'}…</p>
          </div>
        )}

        {/* PREVIEW TABLE */}
        {rows.length > 0 && !busy && (
          <section className="nccim-preview">
            <div className="nccim-preview-head">
              <div>
                <h3>{rows.length} NCC đã chuẩn hoá</h3>
                <p>Nguồn: <span className="nccim-src">{labelSource(source)}</span> · Sửa trực tiếp trong bảng nếu cần.</p>
              </div>
              <div className="nccim-preview-actions">
                <button className="btn btn-ghost btn-sm" onClick={() => { setRows([]); setSource(null); }}>
                  Bóc file khác
                </button>
                <button className="btn btn-ghost btn-sm" onClick={addRow}>
                  <Icon name="plus" size={14} stroke={2.4} /> Thêm dòng
                </button>
                <button className="btn btn-primary" onClick={exportXlsx}>
                  <Icon name="download" size={14} stroke={2.4} /> Xuất Excel chuẩn
                </button>
              </div>
            </div>

            <div className="nccim-table-wrap">
              <table className="nccim-table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Mã NCC</th>
                    <th>Tên NCC</th>
                    <th>SĐT</th>
                    <th>Email</th>
                    <th>Loại NCC</th>
                    <th>Tình trạng</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <tr key={i}>
                      <td className="nccim-td-num">{i + 1}</td>
                      <td><input value={r.code || ''} onChange={e => updateRow(i, 'code', e.target.value)} placeholder="NCCxxx" /></td>
                      <td><input value={r.name || ''} onChange={e => updateRow(i, 'name', e.target.value)} placeholder="Tên NCC" /></td>
                      <td><input value={r.phone || ''} onChange={e => updateRow(i, 'phone', e.target.value)} placeholder="0901…" /></td>
                      <td><input value={r.email || ''} onChange={e => updateRow(i, 'email', e.target.value)} placeholder="email@…" /></td>
                      <td>
                        <select value={r.type || ''} onChange={e => updateRow(i, 'type', e.target.value)}>
                          <option value="">— Chọn —</option>
                          {meta.types.map(t => <option key={t} value={t}>{t}</option>)}
                          {/* Nếu AI snap không khớp, hiện luôn giá trị raw để user thấy */}
                          {r.type && !meta.types.includes(r.type) && <option value={r.type}>{r.type} (chưa chuẩn)</option>}
                        </select>
                      </td>
                      <td>
                        <select value={r.status || 'Hoạt động'} onChange={e => updateRow(i, 'status', e.target.value)}>
                          {meta.statuses.map(s => <option key={s} value={s}>{s}</option>)}
                        </select>
                      </td>
                      <td>
                        <button className="nccim-row-del" onClick={() => removeRow(i)} title="Xóa dòng">
                          <Icon name="close" size={14} stroke={2.4} />
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        )}
      </main>
    );
  }

  function labelSource(s) {
    if (s === 'excel') return 'Excel';
    if (s === 'csv') return 'CSV';
    if (s === 'ai-text') return 'AI bóc từ text';
    return s || '?';
  }

  window.NccImportPage = NccImportPage;
})();
