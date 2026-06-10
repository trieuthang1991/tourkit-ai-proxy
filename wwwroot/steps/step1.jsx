// Step 1: Form + AI Assistant panel
const { useState, useEffect, useRef } = React;

// ── AI Auto-fill Parser (v2 logic — port từ AITourPlanner.tsx lines 170-282) ─────
// Pure JS, parse 1 đoạn text natural Việt → object {route, adults, children, days, nights, ...}
// Caller tự setRequest. Trả {parsed: {...}, hits: [string]} để show user xem nhận diện gì.
const VIET_CITIES = ['Quy Nhơn', 'Phú Quốc', 'Phan Thiết', 'Sapa', 'Hạ Long',
  'Nha Trang', 'Đà Nẵng', 'Hội An', 'Đà Lạt', 'Cát Bà', 'Tuy Hòa', 'Mũi Né',
  'Vũng Tàu', 'Huế', 'Ninh Bình', 'Hải Phòng', 'Hà Nội', 'TP.HCM', 'Cần Thơ'];

function parseTourRequest(text) {
  const lower = text.toLowerCase();
  const hits = [];
  const out = {};

  // 1. Destination — match city + Hà Nội/TP.HCM prefix
  let matchedCity = '';
  for (const city of VIET_CITIES) {
    if (lower.includes(city.toLowerCase())) { matchedCity = city; break; }
  }
  if (matchedCity) {
    if (lower.includes('hà nội') && matchedCity !== 'Hà Nội' && matchedCity !== 'Sapa' && matchedCity !== 'Hạ Long') {
      out.route = `Hà Nội - ${matchedCity}`;
    } else if ((lower.includes('sài gòn') || lower.includes('tp.hcm') || lower.includes('tphcm')) && matchedCity !== 'TP.HCM') {
      out.route = `TP.HCM - ${matchedCity}`;
    } else { out.route = matchedCity; }
    hits.push(`📍 Điểm đến: ${out.route}`);
  }

  // 2. Adults — "X người lớn|khách|pax"
  const adultMatch = text.match(/(\d+)\s*(?:người lớn|khách|pax|người|lớn)/i);
  if (adultMatch) { out.adults = parseInt(adultMatch[1]); hits.push(`👥 Người lớn: ${out.adults}`); }

  // 3. Children — "X trẻ em|trẻ|bé"
  const childMatch = text.match(/(\d+)\s*(?:trẻ em|trẻ|em bé|bé|trẻ con)/i);
  if (childMatch) { out.children = parseInt(childMatch[1]); hits.push(`👶 Trẻ em: ${out.children}`); }

  // 4. Days + Nights — "XNYĐ" hoặc "X ngày Y đêm"
  const dnMatch = text.match(/(\d+)\s*n\s*(\d+)\s*đ/i) || text.match(/(\d+)\s*ngày\s*(\d+)\s*đêm/i);
  if (dnMatch) {
    out.days = parseInt(dnMatch[1]); out.nights = parseInt(dnMatch[2]);
    hits.push(`☀️ Số ngày: ${out.days}N · 🌙 ${out.nights}Đ`);
  } else {
    const dOnly = text.match(/(\d+)\s*ngày/i);
    if (dOnly) {
      out.days = parseInt(dOnly[1]); out.nights = Math.max(0, out.days - 1);
      hits.push(`☀️ Số ngày: ${out.days}N · 🌙 ${out.nights}Đ (auto)`);
    }
  }

  // 5. Budget — "X triệu/khách" hoặc "Xtr/pax" hoặc "Xk/khách"
  const budgetMatch = text.match(/(\d+(?:[.,]\d+)?)\s*(?:triệu|tr|tỉ|tỷ|k)\s*\/\s*(?:người|khách|pax)/i);
  if (budgetMatch) {
    let n = parseFloat(budgetMatch[1].replace(',', '.'));
    if (/tỉ|tỷ/i.test(budgetMatch[0])) n *= 1e9;
    else if (/triệu|tr/i.test(budgetMatch[0])) n *= 1e6;
    else if (/k/i.test(budgetMatch[0])) n *= 1e3;
    out.budgetPerPax = Math.round(n);
    hits.push(`💰 Ngân sách: ${(out.budgetPerPax / 1e6).toFixed(1)}tr/pax`);
  }

  // 6. Preferences (subset từ text — match các keyword phổ biến)
  const PREF_KEYWORDS = [
    ['team building', 'Team Building'],
    ['teambuilding', 'Team Building'],
    ['gala', 'Gala Dinner'],
    ['nghỉ dưỡng', 'Nghỉ dưỡng'],
    ['chụp ảnh', 'Chụp ảnh'],
    ['ẩm thực', 'Ẩm thực'],
    ['hải sản', 'Ẩm thực biển'],
    ['mạo hiểm', 'Mạo hiểm'],
    ['lịch sử', 'Lịch sử'],
  ];
  const prefs = [];
  PREF_KEYWORDS.forEach(([kw, label]) => {
    if (lower.includes(kw) && !prefs.includes(label)) prefs.push(label);
  });
  if (prefs.length > 0) { out.preferences = prefs; hits.push(`✨ Sở thích: ${prefs.join(', ')}`); }

  return { parsed: out, hits };
}

// Tier giá KS / pax / đêm (config đồng bộ với step3.jsx Pricing Options matrix)
const HOTEL_TIER_PRICE_S1 = { 3: 650000, 4: 1150000, 5: 2150000, 6: 4250000 };

function Step1Form({ request, setRequest, onGenerate, generating, genStream, genProgress, aiTone, pushToast,
                    hotelStars, setHotelStars, paxRanges, setPaxRanges,
                    hotelOptions, setHotelOptions }) {
  const [showAddPref, setShowAddPref] = React.useState(false);
  const [historyOpen, setHistoryOpen] = React.useState(false);
  const [history, setHistory] = React.useState(() => window.tourkitHistory?.load() || []);
  const [autofillOpen, setAutofillOpen] = React.useState(false);
  const [autofillText, setAutofillText] = React.useState('');
  const [autofillHits, setAutofillHits] = React.useState([]);

  const applyAutofill = () => {
    if (!autofillText.trim()) return;
    const { parsed, hits } = parseTourRequest(autofillText);
    if (hits.length === 0) {
      pushToast && pushToast('Không nhận diện được trường nào. Thử mô tả rõ hơn: "Đoàn 40 khách đi Quy Nhơn 3N2Đ 5tr/người"', 'warn');
      setAutofillHits(['Không khớp pattern nào.']);
      return;
    }
    setRequest(r => ({ ...r, ...parsed }));
    setAutofillHits(hits);
    pushToast && pushToast(`✨ Đã điền ${hits.length} trường từ AI`);
  };

  // Reload history mỗi khi panel mở (đề phòng vừa save mới)
  React.useEffect(() => {
    if (historyOpen) setHistory(window.tourkitHistory?.load() || []);
  }, [historyOpen]);

  const loadFromHistory = (entry) => {
    setRequest(entry.request);
    setHistoryOpen(false);
    pushToast && pushToast(`Đã tải yêu cầu: ${entry.summary}`);
  };

  const removeFromHistory = (id, e) => {
    e.stopPropagation();
    window.tourkitHistory?.remove(id);
    setHistory(window.tourkitHistory?.load() || []);
  };

  const clearAllHistory = async () => {
    if (history.length > 0 && !(await window.appConfirm(`Xoá toàn bộ ${history.length} yêu cầu cũ?`, { title: 'Xoá lịch sử', confirmLabel: 'Xoá hết', danger: true }))) return;
    window.tourkitHistory?.clear();
    setHistory([]);
  };

  const fmtRelTime = (ts) => {
    const diff = Date.now() - ts;
    if (diff < 60e3) return 'vừa xong';
    if (diff < 3600e3) return `${Math.floor(diff/60e3)} phút trước`;
    if (diff < 86400e3) return `${Math.floor(diff/3600e3)} giờ trước`;
    return `${Math.floor(diff/86400e3)} ngày trước`;
  };

  const updatePref = (p) => {
    setRequest(r => ({
      ...r,
      preferences: r.preferences.includes(p)
        ? r.preferences.filter(x => x !== p)
        : [...r.preferences, p]
    }));
  };

  const paxBadge = () => {
    const total = (Number(request.adults) || 0) + (Number(request.children) || 0);
    if (total < 10) return { label: 'Nhóm nhỏ', cls: 'small' };
    if (total <= 30) return { label: 'Đoàn vừa', cls: 'medium' };
    return { label: 'Đoàn lớn / MICE', cls: 'large' };
  };
  const badge = paxBadge();

  return (
    <div className="layout-2col-58">
      <div className="card">
        <div className="card-header" style={{position: 'relative'}}>
          <div className="card-icon"><Icon name="users" size={18} /></div>
          <h3>NHẬP YÊU CẦU ĐƠN HÀNG</h3>
          <div style={{marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 12}}>
            {history.length > 0 && (
              <button type="button" className="btn btn-ghost btn-sm"
                onClick={() => setHistoryOpen(o => !o)}
                style={{fontSize: 11}}>
                <Icon name="clock" size={12} /> Yêu cầu cũ ({history.length})
                <Icon name={historyOpen ? 'chevronUp' : 'chevronDown'} size={12} />
              </button>
            )}
            <button type="button" className="btn btn-ghost btn-sm"
              onClick={() => setAutofillOpen(o => !o)}
              style={{fontSize: 11, color: autofillOpen ? 'var(--accent)' : 'var(--text-2)'}}
              title="AI điền form từ mô tả tự do (port v2 logic)">
              <Icon name="sparkle" size={12} /> AI điền nhanh
              <Icon name={autofillOpen ? 'chevronUp' : 'chevronDown'} size={12} />
            </button>
            <span style={{fontSize: 11, color: 'var(--text-3)', letterSpacing: '0.05em'}}>
              MÃ: <strong style={{color: 'var(--text)'}}>{request.code}</strong>
            </span>
          </div>

          {/* AI Autofill widget — collapsible, dùng existing CSS (no Tailwind) */}
          {autofillOpen && (
            <div style={{
              position: 'absolute', top: 'calc(100% + 6px)', right: 12, left: 12,
              zIndex: 20, background: 'white', border: '1px solid var(--border)',
              borderRadius: 10, boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
              padding: 14
            }}>
              <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em',
                color: 'var(--text-3)', marginBottom: 8, textTransform: 'uppercase'}}>
                ✨ AI điền form từ mô tả tự do
              </div>
              <textarea value={autofillText}
                onChange={e => setAutofillText(e.target.value)}
                placeholder='Vd: "Đoàn 40 khách Vingroup đi Quy Nhơn 3N2Đ, 5tr/người, team building gala dinner"'
                rows={3}
                style={{width: '100%', padding: 10, fontSize: 13, fontFamily: 'inherit',
                  border: '1px solid var(--border)', borderRadius: 6, resize: 'vertical',
                  outline: 'none', boxSizing: 'border-box', marginBottom: 10}} />
              <div style={{display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap'}}>
                <button type="button" className="btn btn-primary btn-sm"
                  onClick={applyAutofill} disabled={!autofillText.trim()}>
                  <Icon name="sparkle" size={12} /> Điền form
                </button>
                <button type="button" className="btn btn-ghost btn-sm"
                  onClick={() => { setAutofillText(''); setAutofillHits([]); }}>
                  Xóa
                </button>
                <span style={{fontSize: 10, color: 'var(--text-3)', marginLeft: 'auto'}}>
                  Nhận diện: điểm đến · pax · ngày · ngân sách · sở thích
                </span>
              </div>
              {autofillHits.length > 0 && (
                <div style={{marginTop: 10, padding: '8px 10px', background: 'var(--bg)',
                  borderRadius: 6, fontSize: 11}}>
                  <div style={{fontWeight: 700, marginBottom: 4, color: 'var(--text-2)'}}>
                    Đã nhận diện ({autofillHits.length}):
                  </div>
                  {autofillHits.map((h, i) => (
                    <div key={i} style={{color: 'var(--text)', lineHeight: 1.7}}>{h}</div>
                  ))}
                </div>
              )}
            </div>
          )}

          {historyOpen && (
            <div style={{
              position: 'absolute', top: 'calc(100% + 6px)', right: 12, left: 12,
              zIndex: 20, background: 'white', border: '1px solid var(--border)',
              borderRadius: 10, boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
              padding: 10, maxHeight: 360, overflowY: 'auto'
            }}>
              <div style={{display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8, paddingBottom: 8, borderBottom: '1px solid var(--border)'}}>
                <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', color: 'var(--text-3)'}}>
                  YÊU CẦU GẦN ĐÂY ({history.length}) · LƯU CỤC BỘ
                </div>
                <button className="btn btn-ghost btn-sm" type="button"
                  onClick={clearAllHistory}
                  style={{fontSize: 10, color: 'var(--danger)'}}>
                  <Icon name="trash" size={11} /> Xoá hết
                </button>
              </div>

              <div style={{display: 'grid', gap: 4}}>
                {history.map(entry => {
                  const r = entry.request;
                  const totalPax = (r.adults || 0) + (r.children || 0);
                  return (
                    <button key={entry.id} type="button"
                      onClick={() => loadFromHistory(entry)}
                      style={{
                        display: 'grid', gridTemplateColumns: '1fr auto auto',
                        alignItems: 'center', gap: 10,
                        padding: '8px 10px', borderRadius: 6,
                        border: '1px solid var(--border)', background: 'var(--bg)',
                        cursor: 'pointer', textAlign: 'left'
                      }}>
                      <div style={{minWidth: 0}}>
                        <div style={{fontSize: 13, fontWeight: 700, color: 'var(--text)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap'}}>
                          {r.route}
                        </div>
                        <div style={{fontSize: 11, color: 'var(--text-3)', marginTop: 2}}>
                          {r.days}N{r.nights}Đ · {totalPax} khách · {fmtVND(r.budgetPerPax)}/pax
                          {r.preferences?.length > 0 && ` · ${r.preferences.slice(0, 3).join(', ')}${r.preferences.length > 3 ? '...' : ''}`}
                        </div>
                      </div>
                      <span style={{fontSize: 10, color: 'var(--text-3)', whiteSpace: 'nowrap'}}>
                        {fmtRelTime(entry.ts)}
                      </span>
                      <button type="button"
                        onClick={(e) => removeFromHistory(entry.id, e)}
                        title="Xoá khỏi lịch sử"
                        style={{
                          padding: 4, border: 'none', background: 'transparent',
                          color: 'var(--text-3)', cursor: 'pointer', borderRadius: 4
                        }}>
                        <Icon name="close" size={12} />
                      </button>
                    </button>
                  );
                })}
              </div>
            </div>
          )}
        </div>

        <div className="field">
          <label className="label">Tên khách hàng / Doanh nghiệp</label>
          <div className="input-icon">
            <Icon name="user" size={16} />
            <input className="input" value={request.customerName || ''}
              onChange={e => setRequest(r => ({...r, customerName: e.target.value}))}
              placeholder="VD: Tập đoàn Điện lực Miền Bắc / Anh Nguyễn Văn A" />
          </div>
          <div className="field-hint">Tên hiển thị trên báo giá + theo dõi pipeline (auto-fill nếu chat parser nhận diện được)</div>
        </div>

        <div className="field">
          <label className="label">Điểm đi / Điểm đến</label>
          <div className="input-icon">
            <Icon name="pin" size={16} />
            <input className="input" value={request.route}
              onChange={e => setRequest(r => ({...r, route: e.target.value}))}
              placeholder="VD: Hà Nội - Quy Nhơn" />
          </div>
        </div>

        <div className="field">
          <label className="label">
            Số lượng khách (Adult/Child)
            <span className={`pax-badge ${badge.cls}`}>{badge.label}</span>
          </label>
          <div className="field-row" style={{margin: 0}}>
            <NumStepper icon="user" label="Người lớn" value={request.adults}
              onChange={v => setRequest(r => ({...r, adults: v}))} min={1} />
            <NumStepper icon="users" label="Trẻ em" value={request.children}
              onChange={v => setRequest(r => ({...r, children: v}))} min={0} />
          </div>
        </div>

        <div className="field">
          <label className="label">Thời lượng tour (số ngày / đêm)</label>
          <div className="field-row" style={{margin: 0}}>
            <NumStepper icon="calendar" label="Số ngày" value={request.days}
              onChange={v => setRequest(r => ({...r, days: Math.max(1, v), nights: Math.max(0, Math.max(1, v) - 1)}))} min={1} />
            <NumStepper icon="calendar" label="Số đêm" value={request.nights}
              onChange={v => setRequest(r => ({...r, nights: Math.max(0, v)}))} min={0} />
          </div>
          <div className="field-hint">⚡ Đêm tự tính = Ngày − 1 khi đổi số ngày. Có thể chỉnh tay nếu khác.</div>
        </div>

        <div className="field">
          <label className="label">Ngân sách dự kiến / khách</label>
          <div className="input-icon" style={{position: 'relative'}}>
            <Icon name="dollar" size={16} />
            <input className="input" value={fmtVND(request.budgetPerPax)}
              onChange={e => {
                const n = parseInt(e.target.value.replace(/\D/g, '')) || 0;
                setRequest(r => ({...r, budgetPerPax: n}));
              }}
              style={{paddingRight: 56}} />
            <span className="input-suffix">VND</span>
          </div>
          <div className="field-hint">Tiêu chuẩn: 5-8tr · Cao cấp: 10-15tr · Luxury: &gt;15tr</div>
        </div>

        <div className="field">
          <label className="label">Sở thích & Yêu cầu đặc biệt</label>
          <div className="chips">
            {[...PREFERENCES, ...request.preferences.filter(p => !PREFERENCES.includes(p))].map(p => (
              <button key={p} type="button"
                className={`chip ${request.preferences.includes(p) ? 'active' : ''}`}
                onClick={() => updatePref(p)}>
                {p}
              </button>
            ))}
            <button className="chip add" type="button" onClick={() => setShowAddPref(true)}>
              <Icon name="plus" size={12} /> Thêm yêu cầu
            </button>
          </div>
        </div>

        <window.PromptDialog
          open={showAddPref}
          title="Thêm yêu cầu mới cho đoàn"
          eyebrow="SỞ THÍCH · YÊU CẦU ĐẶC BIỆT"
          placeholder="VD: Spa, Lễ hội cồng chiêng, Cà phê view biển..."
          onClose={() => setShowAddPref(false)}
          onSubmit={(val) => {
            if (!request.preferences.includes(val)) {
              setRequest(r => ({...r, preferences: [...r.preferences, val]}));
            }
          }}
          aiSuggest={(ctx) => `Bạn là chuyên gia tour Việt Nam. Khách đi ${ctx.route}, ${ctx.adults + ctx.children} khách, ${ctx.days}N${ctx.nights}Đ, ngân sách ${fmtVND(ctx.budgetPerPax)}/pax. Họ đã chọn: ${ctx.preferences.join(', ') || 'chưa có'}.

Gợi ý 5 sở thích / yêu cầu đặc biệt khác phù hợp với điểm đến và đoàn (mỗi cái 2-4 từ tiếng Việt). Output JSON array thuần: ["yêu cầu 1", "yêu cầu 2", ...]`}
          suggestContext={request}
        />

        {/* NCC Tier Picker (Step 1.5): chọn hotel NCC THẬT cho 3 tier 3,4,5 sao.
            Bỏ trống tier nào thì báo giá ẩn phương án đó. Component tự load NCC
            từ endpoint /api/v1/ncc/providers serviceId=1 (Khách sạn). */}
        {hotelOptions !== undefined && setHotelOptions && window.NccTierPicker && (
          <window.NccTierPicker
            hotelOptions={hotelOptions}
            setHotelOptions={setHotelOptions}
            marketId={null}
            pushToast={pushToast}
          />
        )}

        {/* ── PAX RANGE matrix (v2 logic — markup theo nhóm pax) ───────────────
            Đoàn nhỏ markup cao (chi phí cố định nặng / pax), đoàn lớn markup thấp.
            Auto-match: Step 3 hiển thị range matched + Apply. */}
        {paxRanges && setPaxRanges && (
          <div className="field">
            <label className="label">
              Quản lý khoảng khách (Pax Range)
              <span style={{fontSize: 11, color: 'var(--text-3)', fontWeight: 400, marginLeft: 8}}>
                · Markup % theo nhóm
              </span>
            </label>
            <div style={{display: 'grid', gap: 6}}>
              {paxRanges.map((r, i) => {
                const isMatched = request.adults >= r.from && request.adults <= r.to;
                return (
                  <div key={i} style={{
                    display: 'flex', alignItems: 'center', gap: 8,
                    padding: '8px 10px', borderRadius: 8,
                    background: isMatched ? 'var(--primary-soft)' : 'var(--bg)',
                    border: '1px solid ' + (isMatched ? 'var(--accent)' : 'var(--border)'),
                  }}>
                    <span style={{fontSize: 11, fontWeight: 700, color: 'var(--text-3)',
                      textTransform: 'uppercase', letterSpacing: '0.05em', minWidth: 44}}>
                      Pax
                    </span>
                    <input type="number" value={r.from} min={0}
                      onChange={e => setPaxRanges(rs => rs.map((x, j) => j === i ? {...x, from: +e.target.value || 0} : x))}
                      style={{width: 60, padding: '4px 6px', border: '1px solid var(--border)',
                        borderRadius: 4, fontSize: 13, textAlign: 'right'}} />
                    <span style={{color: 'var(--text-3)'}}>–</span>
                    <input type="number" value={r.to} min={0}
                      onChange={e => setPaxRanges(rs => rs.map((x, j) => j === i ? {...x, to: +e.target.value || 0} : x))}
                      style={{width: 60, padding: '4px 6px', border: '1px solid var(--border)',
                        borderRadius: 4, fontSize: 13, textAlign: 'right'}} />
                    <span style={{color: 'var(--text-3)', marginLeft: 10, fontSize: 11, fontWeight: 700,
                      textTransform: 'uppercase', letterSpacing: '0.05em', minWidth: 50}}>
                      Markup
                    </span>
                    <input type="number" value={r.markup} min={0} max={60} step={0.5}
                      onChange={e => setPaxRanges(rs => rs.map((x, j) => j === i ? {...x, markup: +e.target.value || 0} : x))}
                      style={{width: 60, padding: '4px 6px', border: '1px solid var(--border)',
                        borderRadius: 4, fontSize: 13, textAlign: 'right'}} />
                    <span style={{color: 'var(--text-3)'}}>%</span>
                    {isMatched && (
                      <span style={{fontSize: 10, fontWeight: 700, color: 'var(--accent)',
                        padding: '2px 8px', background: 'white', borderRadius: 999,
                        border: '1px solid var(--accent)', marginLeft: 6}}>
                        ✓ MATCHED
                      </span>
                    )}
                    {paxRanges.length > 1 && (
                      <button type="button"
                        onClick={() => setPaxRanges(rs => rs.filter((_, j) => j !== i))}
                        title="Xóa range"
                        style={{marginLeft: 'auto', background: 'transparent', border: 'none',
                          color: 'var(--text-3)', cursor: 'pointer', fontSize: 16, padding: 0,
                          width: 24, height: 24}}>
                        ×
                      </button>
                    )}
                  </div>
                );
              })}
              <button type="button" className="chip add"
                onClick={() => setPaxRanges(rs => [...rs, { from: 0, to: 0, markup: 20 }])}>
                <Icon name="plus" size={12} /> Thêm khoảng khách (option)
              </button>
            </div>
          </div>
        )}

        <div className="field">
          <label className="label">Ghi chú tự do</label>
          <textarea className="textarea" rows={4} value={request.notes}
            onChange={e => setRequest(r => ({...r, notes: e.target.value}))}
            placeholder="VD: Khách muốn có 1 ngày vui chơi tự do. Đề xuất các điểm ăn chơi tự do trong lịch trình..." />
        </div>
      </div>

      <AIAssistantPanel request={request} onGenerate={onGenerate} generating={generating}
        genStream={genStream} genProgress={genProgress} aiTone={aiTone} />
    </div>
  );
}

function NumStepper({ icon, label, value, onChange, min = 0 }) {
  return (
    <div>
      <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', letterSpacing: '0.06em', textTransform: 'uppercase', marginBottom: 6}}>{label}</div>
      <div className="num-stepper">
        <Icon name={icon} size={16} />
        <input type="text" value={value}
          onChange={e => onChange(parseInt(e.target.value) || 0)} />
        <div className="num-stepper-btns">
          <button type="button" onClick={() => onChange(value + 1)}><Icon name="chevronUp" size={12} /></button>
          <button type="button" onClick={() => onChange(Math.max(min, value - 1))}><Icon name="chevronDown" size={12} /></button>
        </div>
      </div>
    </div>
  );
}

function AIAssistantPanel({ request, onGenerate, generating, genStream, genProgress, aiTone }) {
  // Progress thực tế từ stream: stage 'meta' (đang stream meta) → 'days' (đang gen N ngày song song)
  const stage = genProgress?.stage || 'idle';
  const daysTotal = genProgress?.daysTotal || 0;
  const daysDone = genProgress?.daysDone || 0;
  const streamRef = React.useRef(null);
  React.useEffect(() => {
    // auto-scroll xuống cuối khi stream chữ mới
    if (streamRef.current) streamRef.current.scrollTop = streamRef.current.scrollHeight;
  }, [genStream]);

  // Mô tả tổng hợp tĩnh từ params — không call AI cho đến khi user bấm "SINH TOUR BẰNG AI".
  const analysis = React.useMemo(() => {
    const total = request.adults + request.children;
    if (!request.route || total === 0) return null;
    const focus = request.preferences[0] || 'tổng hợp';
    return {
      analysis: `Chào Anh/Chị! Đoàn [[${total} khách]] đi [[${request.route}]] trong ${request.days} ngày ${request.nights} đêm, ngân sách ~${fmtVND(request.budgetPerPax)}/pax, hướng [[${focus}]]. Bấm nút bên dưới để AI sinh lịch trình chi tiết.`,
      chips: [
        { label: `Sinh tour ${request.route.split('-').pop().trim()}`, icon: 'sparkle' },
        { label: `Tối ưu cho ${total} khách`, icon: 'zap' }
      ]
    };
  }, [request.route, request.adults, request.children, request.days, request.nights, request.budgetPerPax, request.preferences.join(','), aiTone]);
  const loading = false;

  const renderText = (text) => {
    if (!text) return null;
    const parts = text.split(/(\[\[[^\]]+\]\])/g);
    return parts.map((p, i) => {
      if (p.startsWith('[[')) return <span key={i} className="hl">{p.slice(2, -2)}</span>;
      return <span key={i}>{p}</span>;
    });
  };

  return (
    <div className="card-dark" style={{minHeight: 520, display: 'flex', flexDirection: 'column'}}>
      <div className="ai-header">
        <div className="ai-spark"><Icon name="sparkle" size={18} /></div>
        <div>
          <div className="ai-title">AI ASSISTANT</div>
          <div className="ai-sub">Trợ lý điều hành tour</div>
        </div>
        <div className="ai-status">{loading || generating ? 'Đang phân tích' : 'Sẵn sàng'}</div>
      </div>

      <div className="ai-bubble" style={{flex: 1, minHeight: 140}}>
        {generating ? (
          <div>
            <div style={{marginBottom: 12, fontSize: 13, color: 'rgba(255,255,255,0.85)', display: 'flex', alignItems: 'center', gap: 8}}>
              <span className="ai-thinking-dots" style={{transform: 'scale(0.7)'}}><span></span><span></span><span></span></span>
              <span className="hl">
                {stage === 'meta' && 'Đang phân tích & đặt tên tour...'}
                {stage === 'days' && `Đang lập lịch trình ${daysDone}/${daysTotal} ngày...`}
                {stage === 'idle' && 'Đang kết nối AI...'}
              </span>
            </div>
            {/* Stream text từ Step A meta — hiển thị JSON đang chạy ra */}
            {stage === 'meta' && genStream ? (
              <div ref={streamRef}
                style={{
                  fontSize: 11.5, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace',
                  color: 'rgba(255,255,255,0.75)',
                  background: 'rgba(0,0,0,0.25)',
                  borderRadius: 8, padding: '10px 12px',
                  maxHeight: 200, overflowY: 'auto',
                  whiteSpace: 'pre-wrap', wordBreak: 'break-word',
                  border: '1px solid rgba(255,255,255,0.08)'
                }}>
                {genStream}
                <span style={{display: 'inline-block', width: 6, height: 13, background: 'var(--primary)', marginLeft: 2, verticalAlign: 'middle', animation: 'blink 1s infinite'}}></span>
              </div>
            ) : stage === 'days' ? (
              // Stage days: bar progress
              <div>
                <div style={{height: 6, background: 'rgba(255,255,255,0.1)', borderRadius: 3, overflow: 'hidden', marginBottom: 8}}>
                  <div style={{
                    height: '100%',
                    width: `${daysTotal > 0 ? Math.round((daysDone / daysTotal) * 100) : 0}%`,
                    background: 'var(--primary)',
                    transition: 'width 0.4s ease'
                  }}></div>
                </div>
                <div style={{fontSize: 12, color: 'rgba(255,255,255,0.65)'}}>
                  {daysDone === daysTotal && daysTotal > 0
                    ? 'Đang bổ sung mô tả chi tiết & tính costing...'
                    : `Đã xong ${daysDone}/${daysTotal} ngày`}
                </div>
              </div>
            ) : null}
          </div>
        ) : loading ? (
          <div>
            <div className="sk" style={{height: 12, marginBottom: 8, width: '90%'}} />
            <div className="sk" style={{height: 12, marginBottom: 8, width: '80%'}} />
            <div className="sk" style={{height: 12, width: '60%'}} />
          </div>
        ) : analysis ? (
          renderText(analysis.analysis)
        ) : (
          <span style={{color: 'rgba(255,255,255,0.6)'}}>
            Chào bạn! Hãy bắt đầu bằng việc điền điểm đến và số khách. Tôi sẽ phân tích và đề xuất ngay khi có thông tin cơ bản.
          </span>
        )}
      </div>

      {analysis?.chips && !generating && (
        <div style={{marginBottom: 18}}>
          <div className="ai-section-label">AI gợi ý · suggestion chips</div>
          <div className="ai-suggestions">
            {analysis.chips.map((c, i) => (
              <button key={i} className="ai-chip" onClick={onGenerate} disabled={generating}>
                <span style={{display: 'flex', alignItems: 'center', gap: 10}}>
                  <span style={{color: 'var(--primary)'}}><Icon name={c.icon || 'sparkle'} size={14} /></span>
                  {c.label}
                </span>
                <span className="ai-chip-arrow"><Icon name="arrowRight" size={14} /></span>
              </button>
            ))}
          </div>
        </div>
      )}

      <button className="ai-cta-primary" onClick={onGenerate} disabled={generating}>
        <Icon name="sparkle" size={16} />
        {generating ? 'ĐANG LẬP TRÌNH...' : 'SINH TOUR BẰNG AI'}
      </button>
    </div>
  );
}

window.Step1Form = Step1Form;
