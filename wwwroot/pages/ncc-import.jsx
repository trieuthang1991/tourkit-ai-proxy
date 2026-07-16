// pages/ncc-import.jsx — Import NCC (AI): upload 1-10 file báo giá NCC → trích thành GRID (giữ cấu trúc bảng gốc) → review/sửa/lưu từng NCC.
// Flow: upload N file → parallel POST /api/v1/ncc-import/extract-quote (concurrency 3)
//        → mỗi file = 1 item trong `items[]` (status pending/processing/done/error). User switch tab để review/save từng NCC.
// Style: PRESERVE — mirror trang data-list của site (PageShell.PageHero + card var(--surface) + bảng th/td theo token),
//        giống trang Nhà cung cấp (ncc-list.jsx). Drop-zone giữ nguyên class nccim-* có sẵn trong styles.css.

(function () {
  'use strict';
  const { useState, useRef } = React;
  const fmtNum = window.fmtNum || ((n) => Number(n || 0).toLocaleString('vi-VN'));
  const cellText = (c) => c == null ? '' : (typeof c === 'number' ? fmtNum(c) : String(c));

  const MAX_FILES = 10;
  const CONCURRENCY = 3;   // giới hạn call song song sang AI để không burst quota

  // th/td + card theo đúng token bảng data-list của site (mirror _nccTh/_nccTd trong ncc-list.jsx).
  const _th = (w) => ({ padding: '10px 12px', fontWeight: 700, fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-3)', width: w, whiteSpace: 'nowrap', textAlign: 'left' });
  const _td = { padding: '4px 6px', verticalAlign: 'middle' };
  const _muted = { color: 'var(--text-3)' };
  const _card = { background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 10, padding: 16, marginBottom: 16 };
  const _cardLabel = { margin: '0 0 12px', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-3)' };

  function newItem(fileName) {
    return {
      fileName,
      status: 'pending',    // 'pending' | 'processing' | 'done' | 'error'
      quote: null,          // { supplier, tables, conditions }
      meta: null,           // { latencyMs, tokensIn, tokensOut }
      error: null,
      saved: null,          // { providerId, providerCode, priceCount }
      serviceId: 0,
      pcode: '',
    };
  }

  function NccImportPage({ pushToast }) {
    // Danh sách file đã / đang xử lý — mỗi item độc lập (quote riêng, service riêng, saved riêng).
    const [items, setItems] = useState([]);          // Array<Item>
    const [activeIdx, setActiveIdx] = useState(0);
    const [drag, setDrag] = useState(false);
    const [text, setText] = useState('');
    const [showText, setShowText] = useState(false);
    const fileRef = useRef(null);

    // ── Lưu vào CRM ──
    const [services, setServices] = useState([]);    // [{id,name}] loại dịch vụ NCC (dùng chung cho mọi item — cache 1 lần/tenant)
    const [saving, setSaving] = useState(false);
    const sid = () => localStorage.getItem('tourkit_tk_session') || '';

    const norm = (s) => (s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
    const TYPE_KW = {
      hotel: ['khach san', 'hotel', 'phong', 'luu tru', 'resort'], restaurant: ['nha hang', 'restaurant', 'am thuc', 'an uong'],
      transport: ['van chuyen', 'xe', 'transport', 'o to', 'van tai'], ticket: ['ve', 'ticket', 'may bay', 'hang khong', 'airline'],
      combo: ['combo', 'tron goi'], other: [],
    };

    // Tải loại dịch vụ NCC từ CRM 1 lần cho toàn page (list dùng chung; per-item chỉ khác serviceId đã chọn).
    // Trả về list để caller preselect được ngay (avoid race với setState async).
    const ensureServices = async () => {
      if (services.length > 0) return services;
      if (!sid()) return [];
      try {
        const r = await fetch('/api/v1/ncc-import/services', { headers: { 'X-Session-Id': sid() } });
        const j = await r.json();
        if (!r.ok) return [];
        const list = (j.items || []).map(x => ({ id: x.id ?? x.Id, name: x.name ?? x.Name }));
        setServices(list);
        return list;
      } catch { return []; }
    };

    // Preselect serviceId theo hint (serviceType AI đoán) khớp tên loại dịch vụ CRM.
    const guessServiceId = (hint, list) => {
      const h = norm(hint);
      const kws = TYPE_KW[h] || [];
      let m = kws.length ? list.find(s => kws.some(k => norm(s.name).includes(k))) : null;
      if (!m && h) m = list.find(s => norm(s.name).includes(h) || h.includes(norm(s.name)));
      return m ? m.id : 0;
    };

    // Update 1 item trong items[] theo index — dùng cho setSupplier/setCell/setServiceId per-item.
    const patchItem = (idx, patch) =>
      setItems(list => list.map((it, i) => i === idx ? { ...it, ...(typeof patch === 'function' ? patch(it) : patch) } : it));

    // ── Extract 1 file → cập nhật item tương ứng theo file object (không phụ thuộc index vì có thể race). ──
    // itemKey = index KHI push vào (ổn định do items chỉ append trong flow này, không remove giữa chừng).
    const extractOne = async (idx, file) => {
      patchItem(idx, { status: 'processing' });
      try {
        const fd = new FormData(); fd.append('file', file);
        const r = await fetch('/api/v1/ncc-import/extract-quote', { method: 'POST', body: fd });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        const list = await ensureServices();
        patchItem(idx, {
          status: 'done',
          quote: j.quote,
          meta: { latencyMs: j.latencyMs, tokensIn: j.tokensIn, tokensOut: j.tokensOut },
          serviceId: guessServiceId(j.quote?.supplier?.serviceType, list),
        });
        pushToast?.(`✓ Bóc xong "${file.name}" (${j.latencyMs}ms)`);
      } catch (e) {
        patchItem(idx, { status: 'error', error: e.message });
        pushToast?.(`Lỗi "${file.name}": ${e.message}`, 'error');
      }
    };

    // Xử lý N file song song với giới hạn CONCURRENCY (tránh burst AI provider quota).
    const extractFiles = async (files) => {
      const arr = Array.from(files || []).slice(0, MAX_FILES);
      if (arr.length === 0) return;
      if (files.length > MAX_FILES) pushToast?.(`Chỉ nhận tối đa ${MAX_FILES} file/lần — đã bỏ ${files.length - MAX_FILES} file cuối`, 'warn');

      // Append tất cả file (status=pending) → user thấy toàn bộ queue ngay.
      const baseIdx = items.length;
      const newItems = arr.map(f => newItem(f.name));
      setItems(list => [...list, ...newItems]);
      if (baseIdx === 0) setActiveIdx(0);

      // Concurrency runner: mỗi worker cắn từng file tuần tự, đồng thời có CONCURRENCY worker chạy.
      let cursor = 0;
      const workers = Array.from({ length: Math.min(CONCURRENCY, arr.length) }, async () => {
        while (true) {
          const i = cursor++;
          if (i >= arr.length) return;
          await extractOne(baseIdx + i, arr[i]);
        }
      });
      await Promise.all(workers);
    };

    const extractText = async () => {
      const t = text.trim();
      if (!t) { pushToast?.('Hãy dán nội dung trước', 'warn'); return; }
      const idx = items.length;
      setItems(list => [...list, newItem('văn bản dán')]);
      if (idx === 0) setActiveIdx(0);
      setShowText(false); setText('');
      patchItem(idx, { status: 'processing' });
      try {
        const r = await fetch('/api/v1/ncc-import/extract-quote', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: t }) });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        const list = await ensureServices();
        patchItem(idx, {
          status: 'done',
          quote: j.quote,
          meta: { latencyMs: j.latencyMs, tokensIn: j.tokensIn, tokensOut: j.tokensOut },
          serviceId: guessServiceId(j.quote?.supplier?.serviceType, list),
        });
        pushToast?.(`✓ AI bóc xong (${j.latencyMs}ms)`);
      } catch (e) {
        patchItem(idx, { status: 'error', error: e.message });
        pushToast?.('Lỗi AI: ' + e.message, 'error');
      }
    };

    const onDrop = (e) => { e.preventDefault(); setDrag(false); const fs = e.dataTransfer.files; if (fs?.length) extractFiles(fs); };
    const reset = () => { setItems([]); setActiveIdx(0); };

    // ── Lưu 1 NCC → tạo trong CRM (proxy /save → TourKit.Api /api/ai/providers). ──
    const active = items[activeIdx];
    const save = async () => {
      if (!active || active.status !== 'done' || !active.quote) return;
      if (!sid()) { pushToast?.('Chưa đăng nhập TourKit — mở trang Trợ lý để đăng nhập.', 'warn'); return; }
      if (!active.serviceId) { pushToast?.('Hãy chọn Loại dịch vụ trước khi lưu', 'warn'); return; }
      if (!(active.quote?.supplier?.name || '').trim()) { pushToast?.('Thiếu Tên NCC', 'warn'); return; }
      const _ph = active.quote?.supplier?.phones;
      const _firstPhone = Array.isArray(_ph) ? (_ph.find(x => (x || '').toString().trim()) || '').toString().trim() : (_ph || '').toString().trim();
      if (!_firstPhone && !(active.quote?.supplier?.contactPhone || '').trim()) {
        pushToast?.('Thiếu SĐT nhà cung cấp — nhập vào ô "SĐT" trước khi lưu (CRM bắt buộc)', 'warn'); return;
      }
      setSaving(true);
      try {
        const r = await fetch('/api/v1/ncc-import/save', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Session-Id': sid() },
          body: JSON.stringify({ quote: active.quote, serviceId: active.serviceId, providerCode: active.pcode.trim() || null }),
        });
        const j = await r.json();
        if (!r.ok || j.error) throw new Error(j.error || ('HTTP ' + r.status));
        const res = j.result || {};
        patchItem(activeIdx, {
          saved: {
            providerId: res.providerId ?? res.ProviderId,
            providerCode: res.providerCode ?? res.ProviderCode,
            priceCount: j.priceCount,
          }
        });
        pushToast?.(`✓ Đã lưu NCC ${res.providerCode ?? res.ProviderCode ?? ''} (${j.priceCount} dòng giá)`);
      } catch (e) { pushToast?.('Lưu thất bại: ' + e.message, 'error'); }
      finally { setSaving(false); }
    };

    // Setter wrap: update supplier/cell/serviceId/pcode CHO active item (không đụng item khác).
    const setSupplier = (k, v) => patchItem(activeIdx, it => ({ quote: { ...it.quote, supplier: { ...it.quote.supplier, [k]: v } } }));
    const setCell = (ti, ri, ci, v) => patchItem(activeIdx, it => ({
      quote: {
        ...it.quote,
        tables: it.quote.tables.map((t, i) => i !== ti ? t
          : ({ ...t, rows: t.rows.map((row, j) => j !== ri ? row : row.map((c, k) => k === ci ? v : c)) }))
      }
    }));
    const setActiveServiceId = (v) => patchItem(activeIdx, { serviceId: v });
    const setActivePcode = (v) => patchItem(activeIdx, { pcode: v });

    const hasItems = items.length > 0;
    const anyBusy = items.some(it => it.status === 'processing');
    const sup = active?.quote?.supplier || {};
    const tableCount = (active?.quote?.tables || []).length;
    const SUP_FIELDS = [
      ['name', 'Tên NCC'], ['serviceType', 'Loại dịch vụ'], ['phones', 'SĐT'], ['email', 'Email'],
      ['address', 'Địa chỉ'], ['city', 'Tỉnh / TP'], ['website', 'Website'],
      ['contactName', 'Người liên hệ'], ['validYear', 'Năm áp dụng'],
    ];

    // Icon + màu cho status badge trong tab.
    const statusChip = (s) => {
      if (s === 'processing') return { label: '⏳', color: 'var(--text-3)', title: 'Đang bóc…' };
      if (s === 'error') return { label: '✗', color: '#e5484d', title: 'Lỗi' };
      if (s === 'pending') return { label: '…', color: 'var(--text-3)', title: 'Chờ' };
      return { label: '✓', color: 'var(--accent)', title: 'Đã bóc' };
    };

    // Cảnh báo thiếu số liệu: check field bắt buộc SAU khi AI bóc xong.
    // Đặc biệt SĐT — AI thường bóc trượt vì báo giá không luôn có (yêu cầu CRM insertProvider).
    // Bảng giá rỗng — AI parse fail hoàn toàn (báo giá kiểu ảnh / hidden text). User nên bóc lại hoặc dán text.
    // Trả về array các field thiếu (tiếng Việt) để render banner cảnh báo.
    const missingFieldsFor = (it) => {
      if (!it || it.status !== 'done' || !it.quote) return [];
      const missing = [];
      const q = it.quote;
      if (!(q.supplier?.name || '').trim()) missing.push('Tên NCC');
      const ph = q.supplier?.phones;
      const firstPhone = Array.isArray(ph)
        ? (ph.find(x => (x || '').toString().trim()) || '').toString().trim()
        : (ph || '').toString().trim();
      if (!firstPhone && !(q.supplier?.contactPhone || '').trim()) missing.push('SĐT');
      const totalPriceRows = (q.tables || []).reduce((sum, t) => sum + ((t.rows || []).length), 0);
      if (totalPriceRows === 0) missing.push('Bảng giá dịch vụ');
      if (!it.serviceId || it.serviceId <= 0) missing.push('Loại dịch vụ CRM (chưa map được)');
      return missing;
    };
    const activeMissing = missingFieldsFor(active);

    return (
      <main className="page" style={{ padding: '18px 28px 60px', width: '100%' }}>
        <window.PageShell.PageHero
          icon="paper"
          title="AI Import NCC"
          sub={`Upload tối đa ${MAX_FILES} file báo giá NCC/lần (PDF/Word/Excel/PPT/email…) hoặc dán text → AI bóc song song thành bảng để review từng NCC.`}
          status={hasItems
            ? { label: `${items.length} FILE${anyBusy ? ' · ĐANG BÓC' : ''}`, detail: sup.name || `File ${activeIdx + 1}/${items.length}` }
            : { label: 'CHƯA CÓ DỮ LIỆU', detail: 'Upload file báo giá hoặc dán text' }}
          actions={hasItems
            ? <>
                <button className="btn btn-ghost btn-sm" onClick={() => fileRef.current?.click()} disabled={anyBusy || items.length >= MAX_FILES}
                        title={items.length >= MAX_FILES ? `Đã đủ ${MAX_FILES} file` : 'Thêm file'}>
                  <Icon name="plus" size={14} /> Thêm file
                </button>
                <button className="btn btn-ghost btn-sm" onClick={reset}><Icon name="refresh" size={14} /> Bóc file khác</button>
              </>
            : <button className="btn btn-ghost btn-sm" onClick={() => window.tourkitRouter.navigate('/ncc-list')}>
                <Icon name="list" size={14} /> Danh sách NCC
              </button>}
        />

        {/* Input file DÙNG CHUNG — dropzone (khi rỗng) + nút "Thêm file" (khi đã có items). */}
        <input ref={fileRef} type="file" multiple
               accept=".pdf,.docx,.pptx,.xlsx,.eml,.html,.htm,.txt,.csv,.tsv,.json,.xml,.md"
               style={{ display: 'none' }}
               onChange={e => { extractFiles(e.target.files); e.target.value = ''; }} />

        <div style={{ marginTop: 16 }}>
          {/* DROP ZONE — chưa có file */}
          {!hasItems && (
            <section className="nccim-stage">
              <div className={'nccim-drop' + (drag ? ' is-drag' : '')}
                   onDragOver={e => { e.preventDefault(); setDrag(true); }} onDragLeave={() => setDrag(false)}
                   onDrop={onDrop} onClick={() => fileRef.current?.click()} role="button" tabIndex={0}>
                <div className="nccim-drop-icon">
                  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                       strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                    <polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
                  </svg>
                </div>
                <h3>Kéo thả file báo giá vào đây</h3>
                <p>hoặc <strong>click để chọn</strong>. Chọn 1 hoặc nhiều file (tối đa {MAX_FILES}/lần). Hỗ trợ <code>.pdf .docx .pptx .xlsx .eml .html .txt .csv</code>…</p>
              </div>

              <div className="nccim-or"><span>HOẶC</span></div>

              <button className="nccim-paste-btn" onClick={() => setShowText(s => !s)}>
                <Icon name="paper" size={14} stroke={2.2} /> Dán nội dung báo giá (copy từ PDF / Word)
              </button>

              {showText && (
                <div className="nccim-paste">
                  <textarea value={text} onChange={e => setText(e.target.value)} rows={8}
                            placeholder={'Dán nội dung bảng báo giá ở đây…'} />
                  <div className="nccim-paste-actions">
                    <button className="btn btn-ghost btn-sm" onClick={() => { setShowText(false); setText(''); }}>Hủy</button>
                    <button className="btn btn-primary btn-sm" onClick={extractText} disabled={!text.trim()}>
                      <Icon name="sparkle" size={14} stroke={2.4} /> AI bóc tách
                    </button>
                  </div>
                </div>
              )}
            </section>
          )}

          {/* TAB BAR — 1 hàng chip cho mỗi file. Click chuyển active. Hiện khi >0 file. */}
          {hasItems && (
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 12, alignItems: 'center' }}>
              {items.map((it, i) => {
                const s = statusChip(it.status);
                const active = i === activeIdx;
                const nMissing = missingFieldsFor(it).length;
                return (
                  <button key={i} onClick={() => setActiveIdx(i)}
                          title={it.error || (nMissing > 0 ? `${it.fileName} — thiếu ${nMissing} số liệu` : it.fileName)}
                          style={{
                            display: 'inline-flex', alignItems: 'center', gap: 6, padding: '6px 10px', borderRadius: 999,
                            border: '1px solid ' + (active ? 'var(--accent)' : 'var(--border)'),
                            background: active ? 'rgba(37,99,235,0.08)' : 'var(--surface)',
                            color: 'var(--text)', fontSize: 12, cursor: 'pointer',
                            fontWeight: active ? 700 : 500, maxWidth: 260,
                          }}>
                    <span style={{ color: s.color, fontWeight: 700 }}>{s.label}</span>
                    <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{it.fileName}</span>
                    {nMissing > 0 && !it.saved && (
                      <span title={`Thiếu ${nMissing} trường bắt buộc`}
                            style={{ color: '#92400e', background: 'rgba(245,158,11,0.20)', border: '1px solid rgba(245,158,11,0.4)',
                                     borderRadius: 999, padding: '0 6px', fontSize: 10, fontWeight: 700 }}>
                        ⚠ {nMissing}
                      </span>
                    )}
                    {it.saved && <span style={{ color: 'var(--accent)', fontSize: 10, fontWeight: 700 }}>ĐÃ LƯU</span>}
                  </button>
                );
              })}
              <span style={_muted}>· {items.filter(it => it.status === 'done').length}/{items.length} xong</span>
            </div>
          )}

          {/* ACTIVE ITEM — trạng thái xử lý / lỗi / result */}
          {active && active.status === 'processing' && (
            <div className="nccim-busy"><div className="nccim-spinner" /><p>Đang bóc tách "{active.fileName}" qua AI…</p></div>
          )}
          {active && active.status === 'pending' && (
            <div style={{ padding: 24, textAlign: 'center', ..._muted }}>Đang chờ đến lượt xử lý…</div>
          )}
          {active && active.status === 'error' && (
            <div style={{ padding: 16, borderRadius: 8, background: 'rgba(239,68,68,0.08)', border: '1px solid rgba(239,68,68,0.30)', color: 'var(--danger)' }}>
              Lỗi file "{active.fileName}": {active.error}
            </div>
          )}

          {/* RESULT — grid theo style data-list của site */}
          {active && active.status === 'done' && active.quote && (
            <>
              {/* Warning: thiếu số liệu bắt buộc — hiện NGAY sau khi AI bóc xong (không đợi Save mới báo). */}
              {activeMissing.length > 0 && (
                <div style={{
                  display: 'flex', alignItems: 'flex-start', gap: 12,
                  padding: '12px 14px', borderRadius: 10, marginBottom: 16,
                  background: 'rgba(245,158,11,0.10)', borderLeft: '4px solid #f59e0b',
                  border: '1px solid rgba(245,158,11,0.28)'
                }}>
                  <span style={{ fontSize: 18, flexShrink: 0 }}>⚠️</span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontSize: 13, fontWeight: 700, color: '#92400e', marginBottom: 4 }}>
                      Thiếu số liệu — cần bổ sung trước khi lưu vào CRM
                    </div>
                    <ul style={{ margin: 0, paddingLeft: 18, fontSize: 12.5, color: 'var(--text)', lineHeight: 1.7 }}>
                      {activeMissing.map((f, i) => <li key={i}><strong>{f}</strong></li>)}
                    </ul>
                    <div style={{ marginTop: 6, fontSize: 11.5, color: 'var(--text-3)' }}>
                      Điền vào ô "Thông tin nhà cung cấp" bên dưới{activeMissing.includes('Bảng giá dịch vụ') ? ' — hoặc bóc lại bằng file rõ hơn / dán text trực tiếp' : ''}.
                    </div>
                  </div>
                </div>
              )}

              {/* Thông tin nhà cung cấp */}
              <div style={_card}>
                <p style={_cardLabel}>Thông tin nhà cung cấp · <span style={_muted}>{active.fileName}</span></p>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 12 }}>
                  {SUP_FIELDS.map(([k, label]) => {
                    const val = sup[k];
                    const isArr = Array.isArray(val);
                    return (
                      <label key={k} style={{ display: 'grid', gap: 4 }}>
                        <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600 }}>
                          {label}{k === 'phones' && <span style={{ color: '#e5484d' }}> *</span>}
                        </span>
                        <input value={isArr ? val.join(', ') : (val ?? '')}
                               onChange={e => setSupplier(k, e.target.value)}
                               style={{ padding: '8px 10px', border: '1px solid var(--border)', borderRadius: 8,
                                        fontSize: 13, background: 'var(--bg)', color: 'var(--text)' }} />
                      </label>
                    );
                  })}
                </div>
              </div>

              {/* Bảng giá — mỗi bảng gốc 1 grid */}
              {(active.quote.tables || []).map((t, ti) => (
                <div key={ti} style={{ marginBottom: 16 }}>
                  <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, margin: '0 0 8px', flexWrap: 'wrap' }}>
                    <h3 style={{ margin: 0, fontSize: 15, fontWeight: 700 }}>{t.title || `Bảng ${ti + 1}`}</h3>
                    <span style={{ ..._muted, fontSize: 12 }}>{(t.rows || []).length} dòng × {(t.columns || []).length} cột</span>
                  </div>
                  <div style={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 10, overflowX: 'auto' }}>
                    <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                      <thead style={{ background: 'var(--bg)' }}>
                        <tr>{(t.columns || []).map((c, ci) => <th key={ci} style={_th()}>{c}</th>)}</tr>
                      </thead>
                      <tbody>
                        {(t.rows || []).map((row, ri) => (
                          <tr key={ri} style={{ borderTop: '1px solid var(--border)' }}>
                            {(t.columns || []).map((_, ci) => (
                              <td key={ci} style={_td}>
                                <input value={cellText(row[ci])} onChange={e => setCell(ti, ri, ci, e.target.value)}
                                       style={{ width: '100%', minWidth: 88, padding: '7px 9px', borderRadius: 6, fontSize: 13,
                                                border: '1px solid transparent', background: 'transparent', color: 'var(--text)' }}
                                       onFocus={e => { e.target.style.borderColor = 'var(--accent)'; e.target.style.background = 'var(--surface)'; }}
                                       onBlur={e => { e.target.style.borderColor = 'transparent'; e.target.style.background = 'transparent'; }} />
                              </td>
                            ))}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ))}

              {/* Điều kiện chung */}
              {(active.quote.conditions || []).length > 0 && (
                <div style={_card}>
                  <p style={_cardLabel}>Điều kiện chung · {active.quote.conditions.length}</p>
                  <ul style={{ margin: 0, paddingLeft: 18, color: 'var(--text-2)', fontSize: 13, lineHeight: 1.75, display: 'grid', gap: 4 }}>
                    {active.quote.conditions.map((c, i) => <li key={i}>{c}</li>)}
                  </ul>
                </div>
              )}

              {active.meta && (
                <p style={{ ..._muted, fontSize: 12, marginTop: 4 }}>
                  Nguồn: {active.fileName} · {active.meta.latencyMs}ms{active.meta.tokensIn ? ` · ${active.meta.tokensIn}/${active.meta.tokensOut} tokens` : ''} · Sửa trực tiếp trong ô nếu cần.
                </p>
              )}

              {/* LƯU VÀO HỆ THỐNG CRM */}
              <div style={{ ..._card, marginTop: 16, border: '1px solid var(--accent)' }}>
                <p style={_cardLabel}>Lưu vào hệ thống CRM</p>
                {!sid() ? (
                  <p style={{ fontSize: 13, color: 'var(--text-2)', margin: 0 }}>
                    Cần <a href="#/assistant" style={{ color: 'var(--accent)', fontWeight: 600 }}>đăng nhập TourKit</a> để lưu NCC vào hệ thống.
                  </p>
                ) : active.saved ? (
                  <div style={{ fontSize: 13, color: 'var(--text)', display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
                    <span>✓ Đã lưu <strong>{active.saved.providerCode}</strong> · {active.saved.priceCount} dòng giá</span>
                    <a onClick={() => window.tourkitRouter.navigate('/ncc-list')} style={{ color: 'var(--accent)', cursor: 'pointer', fontWeight: 600 }}>Xem danh sách NCC</a>
                    <button className="btn btn-ghost btn-sm" onClick={() => patchItem(activeIdx, { saved: null })}>Lưu lại / sửa</button>
                  </div>
                ) : (
                  <>
                    <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
                      <label style={{ display: 'grid', gap: 4, minWidth: 220 }}>
                        <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600 }}>Loại dịch vụ *</span>
                        <select value={active.serviceId} onChange={e => setActiveServiceId(+e.target.value)}
                                style={{ padding: '8px 10px', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, background: 'var(--bg)', color: 'var(--text)' }}>
                          <option value={0}>— Chọn loại dịch vụ —</option>
                          {services.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                        </select>
                      </label>
                      <label style={{ display: 'grid', gap: 4, minWidth: 180 }}>
                        <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600 }}>Mã NCC (trống = tự sinh)</span>
                        <input value={active.pcode} onChange={e => setActivePcode(e.target.value)} placeholder="Tự sinh"
                               style={{ padding: '8px 10px', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, background: 'var(--bg)', color: 'var(--text)' }} />
                      </label>
                      <button className="btn btn-primary" onClick={save} disabled={saving || !active.serviceId}>
                        {saving ? 'Đang lưu…' : 'Lưu vào hệ thống'}
                      </button>
                    </div>
                    {services.length === 0 && (
                      <p style={{ ..._muted, fontSize: 12, marginTop: 8 }}>Không tải được loại dịch vụ — kiểm tra đăng nhập TourKit.</p>
                    )}
                  </>
                )}
              </div>
            </>
          )}
        </div>
      </main>
    );
  }

  window.NccImportPage = NccImportPage;
})();
