// pages/ncc-import.jsx — Import NCC (AI): upload báo giá NCC → trích thành GRID (giữ cấu trúc bảng gốc) → review/sửa.
// Flow: upload PDF (hoặc dán text) → POST /api/v1/ncc-import/extract-quote
//        → { quote:{supplier, tables:[{title,columns,rows}], conditions[]}, latencyMs, tokensIn, tokensOut }.
// Style: PRESERVE — mirror trang data-list của site (PageShell.PageHero + card var(--surface) + bảng th/td theo token),
//        giống trang Nhà cung cấp (ncc-list.jsx). Drop-zone giữ nguyên class nccim-* có sẵn trong styles.css.

(function () {
  'use strict';
  const { useState, useRef } = React;
  const fmtNum = window.fmtNum || ((n) => Number(n || 0).toLocaleString('vi-VN'));
  const cellText = (c) => c == null ? '' : (typeof c === 'number' ? fmtNum(c) : String(c));

  // th/td + card theo đúng token bảng data-list của site (mirror _nccTh/_nccTd trong ncc-list.jsx).
  const _th = (w) => ({ padding: '10px 12px', fontWeight: 700, fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-3)', width: w, whiteSpace: 'nowrap', textAlign: 'left' });
  const _td = { padding: '4px 6px', verticalAlign: 'middle' };
  const _muted = { color: 'var(--text-3)' };
  const _card = { background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 10, padding: 16, marginBottom: 16 };
  const _cardLabel = { margin: '0 0 12px', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-3)' };

  function NccImportPage({ pushToast }) {
    const [quote, setQuote] = useState(null);
    const [meta, setMeta] = useState(null);     // { latencyMs, tokensIn, tokensOut, fileName }
    const [busy, setBusy] = useState(false);
    const [drag, setDrag] = useState(false);
    const [text, setText] = useState('');
    const [showText, setShowText] = useState(false);
    const fileRef = useRef(null);

    // ── Lưu vào CRM ──
    const [services, setServices] = useState([]);   // [{id,name}] loại dịch vụ NCC (từ TourKit.Api)
    const [serviceId, setServiceId] = useState(0);
    const [pcode, setPcode] = useState('');          // mã NCC tuỳ chọn (trống = API tự sinh)
    const [saving, setSaving] = useState(false);
    const [saved, setSaved] = useState(null);        // { providerId, providerCode, priceCount }
    const sid = () => localStorage.getItem('tourkit_tk_session') || '';

    const norm = (s) => (s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
    const TYPE_KW = {
      hotel: ['khach san', 'hotel', 'phong', 'luu tru', 'resort'], restaurant: ['nha hang', 'restaurant', 'am thuc', 'an uong'],
      transport: ['van chuyen', 'xe', 'transport', 'o to', 'van tai'], ticket: ['ve', 'ticket', 'may bay', 'hang khong', 'airline'],
      combo: ['combo', 'tron goi'], other: [],
    };

    // Tải loại dịch vụ NCC từ CRM + preselect theo serviceType AI đoán (khớp tên dịch vụ nếu có).
    const loadServices = async (hint) => {
      if (!sid()) { setServices([]); return; }
      try {
        const r = await fetch('/api/v1/ncc-import/services', { headers: { 'X-Session-Id': sid() } });
        const j = await r.json();
        if (!r.ok) { setServices([]); return; }
        const items = (j.items || []).map(x => ({ id: x.id ?? x.Id, name: x.name ?? x.Name }));
        setServices(items);
        const h = norm(hint);
        const kws = TYPE_KW[h] || [];
        let m = kws.length ? items.find(s => kws.some(k => norm(s.name).includes(k))) : null;
        if (!m && h) m = items.find(s => norm(s.name).includes(h) || h.includes(norm(s.name)));
        if (m) setServiceId(m.id);
      } catch { setServices([]); }
    };

    const apply = (j, fileName) => {
      setQuote(j.quote);
      setMeta({ latencyMs: j.latencyMs, tokensIn: j.tokensIn, tokensOut: j.tokensOut, fileName });
      setSaved(null); setServiceId(0); setPcode('');
      loadServices(j.quote?.supplier?.serviceType);
    };

    const extractFile = async (file) => {
      if (!file) return;
      setBusy(true);
      try {
        const fd = new FormData(); fd.append('file', file);
        const r = await fetch('/api/v1/ncc-import/extract-quote', { method: 'POST', body: fd });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        apply(j, file.name);
        pushToast?.(`✓ Bóc xong "${file.name}" (${j.latencyMs}ms)`);
      } catch (e) { pushToast?.('Lỗi: ' + e.message, 'error'); }
      finally { setBusy(false); }
    };

    const extractText = async () => {
      const t = text.trim();
      if (!t) { pushToast?.('Hãy dán nội dung trước', 'warn'); return; }
      setBusy(true);
      try {
        const r = await fetch('/api/v1/ncc-import/extract-quote', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: t }) });
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        apply(j, 'văn bản dán'); setShowText(false);
        pushToast?.(`✓ AI bóc xong (${j.latencyMs}ms)`);
      } catch (e) { pushToast?.('Lỗi AI: ' + e.message, 'error'); }
      finally { setBusy(false); }
    };

    const onDrop = (e) => { e.preventDefault(); setDrag(false); const f = e.dataTransfer.files?.[0]; if (f) extractFile(f); };
    const reset = () => { setQuote(null); setMeta(null); setSaved(null); setServiceId(0); setPcode(''); };

    // Lưu báo giá đã bóc tách → tạo NCC trong CRM (proxy /save → TourKit.Api /api/ai/providers).
    const save = async () => {
      if (!sid()) { pushToast?.('Chưa đăng nhập TourKit — mở trang Trợ lý để đăng nhập.', 'warn'); return; }
      if (!serviceId) { pushToast?.('Hãy chọn Loại dịch vụ trước khi lưu', 'warn'); return; }
      if (!(quote?.supplier?.name || '').trim()) { pushToast?.('Thiếu Tên NCC', 'warn'); return; }
      // SĐT bắt buộc (CRM yêu cầu) — AI thường không bóc được → nhắc user điền ô SĐT
      const _ph = quote?.supplier?.phones;
      const _firstPhone = Array.isArray(_ph) ? (_ph.find(x => (x || '').toString().trim()) || '').toString().trim() : (_ph || '').toString().trim();
      if (!_firstPhone && !(quote?.supplier?.contactPhone || '').trim()) {
        pushToast?.('Thiếu SĐT nhà cung cấp — nhập vào ô "SĐT" trước khi lưu (CRM bắt buộc)', 'warn'); return;
      }
      setSaving(true);
      try {
        const r = await fetch('/api/v1/ncc-import/save', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Session-Id': sid() },
          body: JSON.stringify({ quote, serviceId, providerCode: pcode.trim() || null }),
        });
        const j = await r.json();
        if (!r.ok || j.error) throw new Error(j.error || ('HTTP ' + r.status));
        const res = j.result || {};
        setSaved({
          providerId: res.providerId ?? res.ProviderId,
          providerCode: res.providerCode ?? res.ProviderCode,
          priceCount: j.priceCount,
        });
        pushToast?.(`✓ Đã lưu NCC ${res.providerCode ?? res.ProviderCode ?? ''} (${j.priceCount} dòng giá)`);
      } catch (e) { pushToast?.('Lưu thất bại: ' + e.message, 'error'); }
      finally { setSaving(false); }
    };
    const setSupplier = (k, v) => setQuote(q => ({ ...q, supplier: { ...q.supplier, [k]: v } }));
    const setCell = (ti, ri, ci, v) => setQuote(q => ({
      ...q,
      tables: q.tables.map((t, i) => i !== ti ? t
        : ({ ...t, rows: t.rows.map((row, j) => j !== ri ? row : row.map((c, k) => k === ci ? v : c)) }))
    }));

    const sup = quote?.supplier || {};
    const tableCount = (quote?.tables || []).length;
    const SUP_FIELDS = [
      ['name', 'Tên NCC'], ['serviceType', 'Loại dịch vụ'], ['phones', 'SĐT'], ['email', 'Email'],
      ['address', 'Địa chỉ'], ['city', 'Tỉnh / TP'], ['website', 'Website'],
      ['contactName', 'Người liên hệ'], ['validYear', 'Năm áp dụng'],
    ];

    return (
      <main className="page" style={{ padding: '18px 28px 60px', width: '100%' }}>
        <window.PageShell.PageHero
          icon="paper"
          title="AI Import NCC"
          sub="Upload file báo giá NCC (PDF/Word/Excel/PPT/email…) hoặc dán text → AI bóc thành bảng giữ nguyên cấu trúc gốc để review."
          status={quote
            ? { label: `${tableCount} BẢNG`, detail: sup.name || 'Đã bóc xong' }
            : { label: 'CHƯA CÓ DỮ LIỆU', detail: 'Upload file báo giá hoặc dán text' }}
          actions={quote
            ? <button className="btn btn-ghost btn-sm" onClick={reset}><Icon name="refresh" size={14} /> Bóc file khác</button>
            : <button className="btn btn-ghost btn-sm" onClick={() => window.tourkitRouter.navigate('/ncc-list')}><Icon name="list" size={14} /> Danh sách NCC</button>}
        />

        <div style={{ marginTop: 16 }}>
          {/* DROP ZONE — chưa có dữ liệu */}
          {!quote && !busy && (
            <section className="nccim-stage">
              <div className={'nccim-drop' + (drag ? ' is-drag' : '')}
                   onDragOver={e => { e.preventDefault(); setDrag(true); }} onDragLeave={() => setDrag(false)}
                   onDrop={onDrop} onClick={() => fileRef.current?.click()} role="button" tabIndex={0}>
                <input ref={fileRef} type="file"
                       accept=".pdf,.docx,.pptx,.xlsx,.eml,.html,.htm,.txt,.csv,.tsv,.json,.xml,.md"
                       style={{ display: 'none' }}
                       onChange={e => extractFile(e.target.files?.[0])} />
                <div className="nccim-drop-icon">
                  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                       strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                    <polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
                  </svg>
                </div>
                <h3>Kéo thả file báo giá vào đây</h3>
                <p>hoặc <strong>click để chọn</strong>. Hỗ trợ <code>.pdf .docx .pptx .xlsx .eml .html .txt .csv</code>…</p>
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

          {/* BUSY */}
          {busy && (<div className="nccim-busy"><div className="nccim-spinner" /><p>Đang bóc tách qua AI…</p></div>)}

          {/* RESULT — grid theo style data-list của site */}
          {quote && !busy && (
            <>
              {/* Thông tin nhà cung cấp */}
              <div style={_card}>
                <p style={_cardLabel}>Thông tin nhà cung cấp</p>
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
              {(quote.tables || []).map((t, ti) => (
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
              {(quote.conditions || []).length > 0 && (
                <div style={_card}>
                  <p style={_cardLabel}>Điều kiện chung · {quote.conditions.length}</p>
                  <ul style={{ margin: 0, paddingLeft: 18, color: 'var(--text-2)', fontSize: 13, lineHeight: 1.75, display: 'grid', gap: 4 }}>
                    {quote.conditions.map((c, i) => <li key={i}>{c}</li>)}
                  </ul>
                </div>
              )}

              {meta && (
                <p style={{ ..._muted, fontSize: 12, marginTop: 4 }}>
                  Nguồn: {meta.fileName} · {meta.latencyMs}ms{meta.tokensIn ? ` · ${meta.tokensIn}/${meta.tokensOut} tokens` : ''} · Sửa trực tiếp trong ô nếu cần.
                </p>
              )}

              {/* LƯU VÀO HỆ THỐNG CRM */}
              <div style={{ ..._card, marginTop: 16, border: '1px solid var(--accent)' }}>
                <p style={_cardLabel}>Lưu vào hệ thống CRM</p>
                {!sid() ? (
                  <p style={{ fontSize: 13, color: 'var(--text-2)', margin: 0 }}>
                    Cần <a href="#/assistant" style={{ color: 'var(--accent)', fontWeight: 600 }}>đăng nhập TourKit</a> để lưu NCC vào hệ thống.
                  </p>
                ) : saved ? (
                  <div style={{ fontSize: 13, color: 'var(--text)', display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
                    <span>✓ Đã lưu <strong>{saved.providerCode}</strong> · {saved.priceCount} dòng giá</span>
                    <a onClick={() => window.tourkitRouter.navigate('/ncc-list')} style={{ color: 'var(--accent)', cursor: 'pointer', fontWeight: 600 }}>Xem danh sách NCC</a>
                    <button className="btn btn-ghost btn-sm" onClick={() => setSaved(null)}>Lưu lại / sửa</button>
                  </div>
                ) : (
                  <>
                    <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
                      <label style={{ display: 'grid', gap: 4, minWidth: 220 }}>
                        <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600 }}>Loại dịch vụ *</span>
                        <select value={serviceId} onChange={e => setServiceId(+e.target.value)}
                                style={{ padding: '8px 10px', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, background: 'var(--bg)', color: 'var(--text)' }}>
                          <option value={0}>— Chọn loại dịch vụ —</option>
                          {services.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                        </select>
                      </label>
                      <label style={{ display: 'grid', gap: 4, minWidth: 180 }}>
                        <span style={{ fontSize: 11, color: 'var(--text-3)', fontWeight: 600 }}>Mã NCC (trống = tự sinh)</span>
                        <input value={pcode} onChange={e => setPcode(e.target.value)} placeholder="Tự sinh"
                               style={{ padding: '8px 10px', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, background: 'var(--bg)', color: 'var(--text)' }} />
                      </label>
                      <button className="btn btn-primary" onClick={save} disabled={saving || !serviceId}>
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
