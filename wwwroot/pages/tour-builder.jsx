// pages/tour-builder.jsx — Soạn Tour GIT bằng AI.
// 2 cột (taste): TRÁI ô NV mô tả tự do · PHẢI form-control prefill (NV sửa rồi tự implement vào TourKit).
// Pattern search lấy từ mobile TourCreate.razor (search-select pill, autocomplete khách).

const { useState: _tS, useEffect: _tE, useRef: _tR } = React;

const vnd = (n) => (Number(n) || 0).toLocaleString('vi-VN');
const num = (v) => { const n = Number(String(v ?? '').replace(/[^\d]/g, '')); return Number.isFinite(n) ? n : 0; };

const TOUR_TYPES = ['Nội địa', 'Inbound', 'Outbound'];

// Ô số tiền có format ngàn — mẫu AppMoneyInput mobile.
function MoneyInput({ value, onChange, placeholder = '0' }) {
  return (
    <input type="text" inputMode="decimal" className="tb-field tb-money"
      value={value ? Number(value).toLocaleString('vi-VN') : ''}
      onChange={e => onChange(num(e.target.value))} placeholder={placeholder} />
  );
}

// Search-select đơn giản (mẫu AppSearchSelect) — input + dropdown filter.
function SearchSelect({ items, value, onChange, placeholder }) {
  const [open, setOpen] = _tS(false);
  const [q, setQ] = _tS('');
  const ref = _tR(null);
  _tE(() => {
    const f = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener('mousedown', f);
    return () => document.removeEventListener('mousedown', f);
  }, []);
  const filtered = q ? items.filter(it => it.toLowerCase().includes(q.toLowerCase())) : items;
  return (
    <div className="tb-ss" ref={ref}>
      <button type="button" className="tb-field tb-ss-btn" onClick={() => setOpen(o => !o)}>
        <span className={value ? '' : 'tb-ph'}>{value || placeholder}</span>
        <Icon name="chevronDown" size={14} />
      </button>
      {open && (
        <div className="tb-ss-drop">
          <div className="tb-ss-search">
            <Icon name="search" size={13} />
            <input autoFocus placeholder="Tìm…" value={q} onChange={e => setQ(e.target.value)} />
          </div>
          <div className="tb-ss-list">
            {filtered.length === 0 && <div className="tb-ss-empty">Không có kết quả</div>}
            {filtered.map(it => (
              <button key={it} type="button"
                className={'tb-ss-item' + (value === it ? ' on' : '')}
                onClick={() => { onChange(it); setOpen(false); setQ(''); }}>
                {it}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// Empty form mặc định.
const EMPTY = () => ({
  title: '', marketName: '', tourType: '',
  startDate: '', endDate: '', adultCount: 0, childCount: 0,
  customerName: '', customerPhone: '', customerEmail: '',
  note: '', expenses: [], services: [], warnings: [],
});

const SAMPLE = `Tour Đà Nẵng - Hội An 3N2Đ, đi 15/7 về 17/7, đoàn 18 người lớn + 4 trẻ em, KH chị Nguyễn Thị Lan SĐT 0987 123 456, email lan@gmail.com. Vé tour 5,5tr/người lớn, trẻ em 3,5tr (VAT 8%). Khách sạn 3 sao Mường Thanh 2 đêm, giá 800k/đêm/phòng, đặt 10 phòng. Xe 35 chỗ 3 ngày, 4tr/ngày. Vé tham quan Bà Nà 750k/người. Yêu cầu: 1 phòng cho người ăn chay.`;

function MarketSelect({ value, onChange }) {
  const [markets, setMarkets] = _tS(['Nội địa', 'Inbound', 'Outbound', 'Châu Á', 'Châu Âu', 'Châu Mỹ', 'Châu Úc', 'Hàn Quốc', 'Nhật Bản', 'Thái Lan', 'Đài Loan', 'Trung Quốc']);
  // Có thể nâng cấp sau: gọi /api/ai/list_markets để lấy đúng tenant.
  return <SearchSelect items={markets} value={value} onChange={onChange} placeholder="— Chọn thị trường —" />;
}

// Ngưỡng cảnh báo margin thấp (Red Zone) — đồng nhất với pricing model CEO dashboard.
const RED_ZONE_MARGIN = 15;   // %

function TourBuilderPage({ pushToast }) {
  const [prompt, setPrompt] = _tS('');
  const [form, setForm] = _tS(EMPTY());
  const [busy, setBusy] = _tS(false);
  const [savedId, setSavedId] = _tS(null);          // id sau khi save (hoặc load từ ?id=)
  const [saving, setSaving] = _tS(false);

  // Load báo giá đã lưu nếu URL có ?id= (vd điều hướng từ /quotes click row)
  _tE(() => {
    const q = new URLSearchParams(window.location.search);
    const id = q.get('id');
    if (!id) return;
    (async () => {
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes/' + encodeURIComponent(id));
        if (!r.ok) { pushToast('Không tải được báo giá ' + id, 'error'); return; }
        const j = await r.json();
        if (j.data) setForm({ ...EMPTY(), ...j.data, expenses: j.data.expenses || [], services: j.data.services || [] });
        setSavedId(j.id);
        pushToast('Đã mở báo giá "' + (j.title || j.id) + '"');
      } catch (e) { pushToast('Lỗi tải: ' + e.message, 'error'); }
    })();
  }, []);

  const set = (k, v) => setForm(f => ({ ...f, [k]: v }));
  const setRow = (key, i, k, v) => setForm(f => ({ ...f, [key]: f[key].map((r, idx) => idx === i ? { ...r, [k]: v } : r) }));
  const addRow = (key, row) => setForm(f => ({ ...f, [key]: [...f[key], row] }));
  const delRow = (key, i) => setForm(f => ({ ...f, [key]: f[key].filter((_, idx) => idx !== i) }));

  async function aiFill() {
    if (!prompt.trim()) { pushToast('Hãy mô tả tour ở khung bên trái', 'error'); return; }
    setBusy(true);
    try {
      const cfg = window.tourkit.ai.getConfig();
      const key = window.tourkit.ai.getKey(cfg.provider);
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-builder/parse', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ prompt, provider: cfg.provider, model: cfg.model, apiKey: key || undefined }),
      });
      const d = await r.json();
      if (!r.ok) { pushToast(d.error || 'Bóc tách lỗi', 'error'); return; }
      // Merge: chỉ override field AI có giá trị, GIỮ field NV đã nhập.
      setForm(prev => ({
        ...prev,
        ...Object.fromEntries(Object.entries(d).filter(([_, v]) => v != null && !(Array.isArray(v) && v.length === 0))),
        warnings: d.warnings || [],
        expenses: (d.expenses && d.expenses.length) ? d.expenses : prev.expenses,
        services: (d.services && d.services.length) ? d.services : prev.services,
      }));
      pushToast('AI đã điền form — kiểm tra và bổ sung nếu cần');
    } catch (e) { pushToast('Lỗi: ' + e.message, 'error'); }
    finally { setBusy(false); }
  }

  // ── Pricing engine — live recalc.
  // Quy đổi pax: trẻ em = 0.75 người lớn (Bug A chuẩn nghiệp vụ CEO dashboard).
  // sumExp = TỔNG THU (sau VAT), sumSrv = TỔNG CHI (sau VAT, FOC nếu có).
  const sumExp = form.expenses.reduce((s, e) => s + (e.unitPrice * e.quantity) * (1 + (e.vatPercent || 0) / 100), 0);
  const sumSrv = form.services.reduce((s, e) => {
    const effNet = e.focRatio && e.focRatio > 0 ? e.netPrice * e.focRatio / (e.focRatio + 1) : e.netPrice;
    return s + (effNet * e.quantity * Math.max(e.nights, 1)) * (1 + (e.vatPercent || 0) / 100);
  }, 0);
  const profit = sumExp - sumSrv;
  const paxEquiv = (form.adultCount || 0) + (form.childCount || 0) * 0.75 || 1;
  const revPerPax = sumExp / paxEquiv;
  const costPerPax = sumSrv / paxEquiv;
  const profitPerPax = revPerPax - costPerPax;
  // Margin% derived (gross): profit / revenue. Red Zone < 15%.
  const marginPct = sumExp > 0 ? (profit / sumExp) * 100 : 0;
  const isRedZone = sumExp > 0 && marginPct < RED_ZONE_MARGIN;

  // Save báo giá → POST /api/v1/tour-quotes (upsert). id=null → tạo mới; có id → update.
  async function saveQuote() {
    if (!form.title && !form.customerName) {
      pushToast('Cần nhập Tên tour hoặc Tên khách trước khi lưu', 'error');
      return;
    }
    setSaving(true);
    try {
      const body = {
        id: savedId,
        title: form.title || null,
        customerName: form.customerName || null,
        customerPhone: form.customerPhone || null,
        marketName: form.marketName || null,
        tourType: form.tourType || null,
        startDate: form.startDate || null,
        endDate: form.endDate || null,
        adultCount: form.adultCount || 0,
        childCount: form.childCount || 0,
        totalNet: Math.round(sumSrv),
        totalRevenue: Math.round(sumExp),
        profit: Math.round(profit),
        marginPercent: sumExp > 0 ? Math.round(marginPct * 100) / 100 : null,
        data: form,
      };
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const j = await r.json();
      if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
      setSavedId(j.id);
      // Update URL ?id= (replaceState, không thêm history entry — F5 reload sẽ open lại)
      const url = new URL(window.location.href);
      url.searchParams.set('id', j.id);
      window.history.replaceState({}, '', url.toString());
      pushToast(savedId ? 'Đã cập nhật báo giá' : 'Đã lưu báo giá mới');
    } catch (e) { pushToast('Lỗi lưu: ' + e.message, 'error'); }
    finally { setSaving(false); }
  }

  function copyJson() {
    navigator.clipboard.writeText(JSON.stringify(form, null, 2));
    pushToast('Đã copy JSON form');
  }
  async function reset() {
    if (!(await window.appConfirm('Xóa toàn bộ form?', { title: 'Xóa form', confirmLabel: 'Xóa', danger: true }))) return;
    setForm(EMPTY()); setPrompt(''); pushToast('Đã xóa');
  }

  const hasForm = form.title || form.expenses.length > 0 || form.services.length > 0;
  return (
    <main className="page tb">
      <window.PageShell.PageHero
        icon="pin"
        title="Soạn Tour GIT bằng AI"
        badge="Type 3"
        sub="Mô tả tự do ở khung trái → AI bóc tách thành form-control bên phải. NV bổ sung rồi tự đưa vào TourKit."
        status={{ label: hasForm ? 'CÓ BẢN NHÁP' : 'CHƯA CÓ NHÁP',
          detail: hasForm ? `${form.expenses.length} thu · ${form.services.length} dịch vụ` : 'Nhập mô tả & bấm AI',
          tone: hasForm ? 'live' : 'idle' }}
        actions={<>
          <button className="tb-btn" onClick={() => window.tourkitRouter.navigate('/quotes')} disabled={busy || saving}>
            <Icon name="list" size={14} /> DS đã lưu
          </button>
          <button className="tb-btn" onClick={reset} disabled={busy || saving}><Icon name="trash" size={14} /> Xóa</button>
          <button className="tb-btn" onClick={copyJson} disabled={busy || saving}><Icon name="copy" size={14} /> Copy JSON</button>
          <button className="tb-btn tb-btn-primary" onClick={saveQuote} disabled={busy || saving || !hasForm}>
            <Icon name={saving ? 'refresh' : 'save'} size={14} /> {saving ? 'Đang lưu…' : (savedId ? 'Cập nhật' : 'Lưu báo giá')}
          </button>
        </>}
      />

      <div className="tb-grid">
        {/* TRÁI — input tự do */}
        <section className="tb-left">
          <div className="tb-card">
            <div className="tb-card-head"><Icon name="edit" size={15} /> Mô tả tour</div>
            <textarea className="tb-textarea" rows={14} value={prompt} onChange={e => setPrompt(e.target.value)}
              placeholder="Mô tả tour như đang nói chuyện với điều hành. Ví dụ: tên tour, ngày đi/về, số khách, thông tin liên hệ KH, các khoản thu, dịch vụ điều hành (khách sạn, xe, ăn, vé)…" />
            <div className="tb-help">
              <button className="tb-link" onClick={() => setPrompt(SAMPLE)} disabled={busy}>Dùng mô tả mẫu</button>
              <span className="tb-help-count">{prompt.length} ký tự</span>
            </div>
            <button className="tb-btn primary block" onClick={aiFill} disabled={busy || !prompt.trim()}>
              {busy ? <><span className="tb-spin" /> AI đang bóc tách…</> : <><Icon name="sparkle" size={15} /> AI điền vào form</>}
            </button>
            {form.warnings && form.warnings.length > 0 && (
              <div className="tb-warns">
                <div className="tb-warns-head"><Icon name="warning" size={13} /> AI lưu ý</div>
                <ul>{form.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
              </div>
            )}
          </div>
        </section>

        {/* PHẢI — form-control */}
        <section className="tb-right">
          {/* Khối 1: Thông tin tour */}
          <div className="tb-card">
            <div className="tb-card-head"><Icon name="pin" size={15} /> Thông tin tour</div>
            <div className="tb-row">
              <label>Tên tour <em>*</em></label>
              <input className="tb-field" value={form.title} onChange={e => set('title', e.target.value)} placeholder="vd: Đà Nẵng - Hội An 3N2Đ" />
            </div>
            <div className="tb-grid2">
              <div className="tb-row">
                <label>Thị trường</label>
                <MarketSelect value={form.marketName} onChange={v => set('marketName', v)} />
              </div>
              <div className="tb-row">
                <label>Loại hình</label>
                <SearchSelect items={TOUR_TYPES} value={form.tourType} onChange={v => set('tourType', v)} placeholder="— Chọn —" />
              </div>
            </div>
            <div className="tb-grid2">
              <div className="tb-row">
                <label>Ngày khởi hành <em>*</em></label>
                <input type="date" className="tb-field" value={form.startDate || ''} onChange={e => set('startDate', e.target.value)} />
              </div>
              <div className="tb-row">
                <label>Ngày kết thúc <em>*</em></label>
                <input type="date" className="tb-field" value={form.endDate || ''} onChange={e => set('endDate', e.target.value)} />
              </div>
            </div>
            <div className="tb-grid2">
              <div className="tb-row">
                <label>SL người lớn</label>
                <input type="number" className="tb-field" value={form.adultCount || 0} min={0}
                  onChange={e => set('adultCount', num(e.target.value))} />
              </div>
              <div className="tb-row">
                <label>SL trẻ em</label>
                <input type="number" className="tb-field" value={form.childCount || 0} min={0}
                  onChange={e => set('childCount', num(e.target.value))} />
              </div>
            </div>
          </div>

          {/* Khối 2: KH */}
          <div className="tb-card">
            <div className="tb-card-head"><Icon name="user" size={15} /> Khách đại diện</div>
            <div className="tb-row">
              <label>Họ tên</label>
              <input className="tb-field" value={form.customerName} onChange={e => set('customerName', e.target.value)} placeholder="Tên khách đại diện" />
            </div>
            <div className="tb-grid2">
              <div className="tb-row">
                <label>SĐT</label>
                <input className="tb-field" value={form.customerPhone} onChange={e => set('customerPhone', e.target.value)} placeholder="0xxx…" />
              </div>
              <div className="tb-row">
                <label>Email</label>
                <input className="tb-field" value={form.customerEmail} onChange={e => set('customerEmail', e.target.value)} placeholder="email@…" />
              </div>
            </div>
            <div className="tb-row">
              <label>Ghi chú</label>
              <textarea rows={2} className="tb-field" value={form.note} onChange={e => set('note', e.target.value)}
                placeholder="Yêu cầu đặc biệt, ghi chú nội bộ…" />
            </div>
          </div>

          {/* Khối 3: Phần thu */}
          <div className="tb-card">
            <div className="tb-card-head">
              <span><Icon name="trend" size={15} /> Phần thu</span>
              <button type="button" className="tb-card-add" onClick={() => addRow('expenses', { title: '', unitPrice: 0, quantity: 1, vatPercent: 0 })}>
                <Icon name="plus" size={13} /> Thêm
              </button>
            </div>
            {form.expenses.length === 0 && <div className="tb-empty-row">Chưa có khoản thu</div>}
            {form.expenses.map((row, i) => (
              <div key={i} className="tb-line">
                <div className="tb-line-grid">
                  <div className="tb-row tb-row-grow">
                    <label>Nội dung</label>
                    <input className="tb-field" value={row.title} onChange={e => setRow('expenses', i, 'title', e.target.value)} placeholder="vd: Vé tour người lớn" />
                  </div>
                  <button className="tb-del" onClick={() => delRow('expenses', i)} title="Xóa"><Icon name="trash" size={14} /></button>
                </div>
                <div className="tb-line-nums">
                  <div className="tb-row"><label>Đơn giá</label><MoneyInput value={row.unitPrice} onChange={v => setRow('expenses', i, 'unitPrice', v)} /></div>
                  <div className="tb-row"><label>SL</label><input type="number" className="tb-field" value={row.quantity} min={0} onChange={e => setRow('expenses', i, 'quantity', num(e.target.value))} /></div>
                  <div className="tb-row"><label>VAT %</label><input type="number" className="tb-field" value={row.vatPercent} min={0} onChange={e => setRow('expenses', i, 'vatPercent', num(e.target.value))} /></div>
                  <div className="tb-row tb-row-total"><label>Thành tiền</label>
                    <div className="tb-amt good">{vnd((row.unitPrice * row.quantity) * (1 + (row.vatPercent || 0) / 100))} ₫</div>
                  </div>
                </div>
              </div>
            ))}
          </div>

          {/* Khối 4: Dịch vụ ĐH */}
          <div className="tb-card">
            <div className="tb-card-head">
              <span><Icon name="bed" size={15} /> Dịch vụ điều hành (chi)</span>
              <button type="button" className="tb-card-add" onClick={() => addRow('services', { name: '', providerName: '', quantity: 1, nights: 1, netPrice: 0, vatPercent: 0, focRatio: 0 })}>
                <Icon name="plus" size={13} /> Thêm
              </button>
            </div>
            {form.services.length === 0 && <div className="tb-empty-row">Chưa có dịch vụ</div>}
            {form.services.map((row, i) => (
              <div key={i} className="tb-line">
                <div className="tb-line-grid">
                  <div className="tb-row tb-row-grow">
                    <label>Tên dịch vụ</label>
                    <input className="tb-field" value={row.name} onChange={e => setRow('services', i, 'name', e.target.value)} placeholder="vd: Khách sạn 3 sao" />
                  </div>
                  <button className="tb-del" onClick={() => delRow('services', i)} title="Xóa"><Icon name="trash" size={14} /></button>
                </div>
                <div className="tb-row">
                  <label>Nhà cung cấp</label>
                  <input className="tb-field" value={row.providerName || ''} onChange={e => setRow('services', i, 'providerName', e.target.value)} placeholder="Tên NCC" />
                </div>
                <div className="tb-line-nums">
                  <div className="tb-row"><label>SL</label><input type="number" className="tb-field" value={row.quantity} min={0} onChange={e => setRow('services', i, 'quantity', num(e.target.value))} /></div>
                  <div className="tb-row"><label>Số đêm/lượt</label><input type="number" className="tb-field" value={row.nights} min={0} onChange={e => setRow('services', i, 'nights', num(e.target.value))} /></div>
                  <div className="tb-row"><label>Giá NET</label><MoneyInput value={row.netPrice} onChange={v => setRow('services', i, 'netPrice', v)} /></div>
                  <div className="tb-row"><label>VAT %</label><input type="number" className="tb-field" value={row.vatPercent} min={0} onChange={e => setRow('services', i, 'vatPercent', num(e.target.value))} /></div>
                  <div className="tb-row"><label title="Free-of-charge: 1 miễn phí mỗi N suất. Vd Hotel FOC 16+1 → nhập 16.">FOC N+1</label>
                    <input type="number" className="tb-field" value={row.focRatio || 0} min={0} placeholder="0"
                      onChange={e => setRow('services', i, 'focRatio', num(e.target.value))} />
                  </div>
                  <div className="tb-row tb-row-total"><label>Tổng chi</label>
                    {(() => {
                      const effNet = row.focRatio > 0 ? row.netPrice * row.focRatio / (row.focRatio + 1) : row.netPrice;
                      const total = (effNet * row.quantity * Math.max(row.nights, 1)) * (1 + (row.vatPercent || 0) / 100);
                      const savings = row.focRatio > 0 ? (row.netPrice - effNet) * row.quantity * Math.max(row.nights, 1) * (1 + (row.vatPercent || 0) / 100) : 0;
                      return (<>
                        <div className="tb-amt warn">{vnd(Math.round(total))} ₫</div>
                        {savings > 0 && <div style={{fontSize:10,color:'#10B981',fontWeight:600}}>FOC tiết kiệm {vnd(Math.round(savings))}₫</div>}
                      </>);
                    })()}
                  </div>
                </div>
              </div>
            ))}
          </div>

          {/* Tổng kết — 2 dòng: tổng cả đoàn + per-pax (quy đổi trẻ em 75%). Margin% có Red Zone. */}
          <div className="tb-summary">
            <div><span>Tổng thu</span><b className="good">{vnd(Math.round(sumExp))} ₫</b></div>
            <div><span>Tổng chi</span><b className="warn">{vnd(Math.round(sumSrv))} ₫</b></div>
            <div><span>Lợi nhuận dự kiến</span><b className={profit >= 0 ? 'good' : 'bad'}>{vnd(Math.round(profit))} ₫</b></div>
            <div>
              <span>Margin %</span>
              <b style={{color: isRedZone ? 'var(--danger, #ef4444)' : (marginPct >= 25 ? 'var(--success, #10B981)' : 'var(--warning, #F59E0B)')}}>
                {sumExp > 0 ? marginPct.toFixed(1) + '%' : '—'}
              </b>
            </div>
          </div>
          {sumExp > 0 && (
            <div className="tb-summary" style={{marginTop: 8}}>
              <div><span>Thu / pax (quy đổi)</span><b>{vnd(Math.round(revPerPax))} ₫</b></div>
              <div><span>Chi / pax</span><b style={{color: 'var(--text-2)'}}>{vnd(Math.round(costPerPax))} ₫</b></div>
              <div><span>Lãi / pax</span><b className={profitPerPax >= 0 ? 'good' : 'bad'}>{vnd(Math.round(profitPerPax))} ₫</b></div>
              <div><span>Pax quy đổi</span><b style={{color: 'var(--text-2)'}}>{paxEquiv.toFixed(2)} <small style={{fontSize:10,opacity:.7}}>(trẻ × 0.75)</small></b></div>
            </div>
          )}
          {isRedZone && (
            <div style={{marginTop:10, padding:'10px 14px', borderRadius:10, background:'#fef2f2', border:'1px solid #fecaca', color:'#b91c1c', fontSize:12, fontWeight:600}}>
              ⚠ <b>Red Zone</b> — Margin {marginPct.toFixed(1)}% dưới ngưỡng tối thiểu {RED_ZONE_MARGIN}%. Kiểm duyệt trước khi gửi khách.
            </div>
          )}
        </section>
      </div>
    </main>
  );
}

window.TourBuilderPage = TourBuilderPage;
