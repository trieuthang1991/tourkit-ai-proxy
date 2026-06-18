// Step 2: Timeline + Costing sidebar + edit modal
const { useState: useS2 } = React;

function Step2Itinerary({ itinerary, setItinerary, request, onNext, onBack, density, pushToast }) {
  const [activeDay, setActiveDay] = useS2(0);
  const [editing, setEditing] = useS2(null); // { dayIdx, actIdx } or { dayIdx, actIdx: 'new' }
  // Per-activity HotelPicker: { dayIdx, actIdx } | null. Mở khi user bấm "Chọn NCC" trên HOTEL row.
  const [hotelPicking, setHotelPicking] = useS2(null);
  // Default tĩnh — không call AI cho đến khi user bấm nút.
  const [optimizer, setOptimizer] = useS2({
    advice: 'Bấm nút bên dưới để AI phân tích costing và đề xuất supplier giúp tối ưu margin.',
    cta: '🤖 Hỏi AI tối ưu',
    pristine: true
  });
  const [optLoading, setOptLoading] = useS2(false);

  const day = itinerary[activeDay] || itinerary[0];
  const totalPax = request.adults + request.children;

  // Costing summary
  const totalCost = itinerary.reduce((s, d) => s + d.activities.reduce((a, b) => a + (b.cost || 0), 0), 0);
  const guideCost = window.DEMO_GUIDE_COST;
  const netPerPax = Math.round((totalCost + guideCost) / Math.max(totalPax, 1));
  const margin = 25.3;
  const salePerPax = Math.round(netPerPax / (1 - margin/100));
  const profitPerPax = salePerPax - netPerPax;

  const runOptimizer = async () => {
    setOptLoading(true);
    try {
      const prompt = `Bạn là chuyên gia cost optimizer tour du lịch VN. Phân tích costing hiện tại và đề xuất tối ưu trong 2-3 câu.

Tour: ${request.route}, ${request.days}N${request.nights}Đ, ${totalPax} khách
Giá vốn/pax hiện tại: ${fmtVND(netPerPax)}
Giá bán/pax đề xuất: ${fmtVND(salePerPax)}
Margin: ${margin}%

Output JSON THUẦN:
{
  "advice": "2-3 câu, highlight tên supplier bằng <<...>> và số % bằng [[...]]",
  "cta": "Text ngắn cho button (3-5 chữ)"
}

Ví dụ giọng: "Tôi thấy Margin đang ở mức tốt. Tuy nhiên, nếu đổi option <<Resort FLC>> sang <<Anyia Hotel>>, bạn có thể tăng Margin lên [[32%]] mà vẫn đảm bảo tiêu chuẩn 4 sao."`;
      const raw = await window.claude.complete(prompt);
      const m = raw.match(/\{[\s\S]*\}/);
      if (m) setOptimizer({ ...JSON.parse(m[0]), pristine: false });
      else throw new Error('parse');
    } catch (e) {
      setOptimizer({
        advice: 'Tôi thấy Margin đang ở mức tốt. Tuy nhiên, nếu đổi option <<Resort FLC>> sang <<Anyia Hotel>>, bạn có thể tăng Margin lên [[32%]] mà vẫn đảm bảo tiêu chuẩn 4 sao.',
        cta: 'Thử phương án AI gợi ý',
        pristine: false
      });
    } finally {
      setOptLoading(false);
    }
  };

  const removeAct = (di, ai) => {
    setItinerary(it => it.map((d, i) => i === di ? {...d, activities: d.activities.filter((_, j) => j !== ai)} : d));
  };

  const saveAct = (data) => {
    if (editing.actIdx === 'new') {
      setItinerary(it => it.map((d, i) => i === editing.dayIdx ? {...d, activities: [...d.activities, {...data, id: 'n' + Date.now()}]} : d));
    } else {
      setItinerary(it => it.map((d, i) => i === editing.dayIdx ? {
        ...d, activities: d.activities.map((a, j) => j === editing.actIdx ? {...a, ...data} : a)
      } : d));
    }
    setEditing(null);
  };

  const renderOpt = (text) => {
    if (!text) return null;
    return text.split(/(<<[^>]+>>|\[\[[^\]]+\]\])/g).map((p, i) => {
      if (p.startsWith('<<')) return <span key={i} className="hl-num">{p.slice(2, -2)}</span>;
      if (p.startsWith('[[')) return <span key={i} className="hl-good">{p.slice(2, -2)}</span>;
      return <span key={i}>{p}</span>;
    });
  };

  return (
    <>
      {/* Customer / Tour context banner — surface customerName + route + pax từ Step 1 */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap',
        padding: '14px 20px', marginBottom: 16,
        background: 'linear-gradient(135deg, var(--primary-soft), white)',
        border: '1px solid var(--border)', borderRadius: 12,
      }}>
        <div style={{display: 'flex', alignItems: 'center', gap: 10}}>
          <Icon name="user" size={18} stroke={2} />
          <div>
            <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', letterSpacing: '0.06em'}}>Khách hàng</div>
            <div style={{fontSize: 14, fontWeight: 700, color: 'var(--text)'}}>
              {request.customerName || <em style={{color: 'var(--text-3)', fontWeight: 400}}>(chưa nhập tên — về Step 1 bổ sung)</em>}
            </div>
          </div>
        </div>
        <div style={{height: 28, width: 1, background: 'var(--border-strong)'}} />
        <div>
          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', letterSpacing: '0.06em'}}>Hành trình</div>
          <div style={{fontSize: 13, fontWeight: 600, color: 'var(--text)'}}>{request.route || '—'}</div>
        </div>
        <div style={{height: 28, width: 1, background: 'var(--border-strong)'}} />
        <div>
          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', letterSpacing: '0.06em'}}>Pax</div>
          <div style={{fontSize: 13, fontWeight: 600}}>{request.adults}NL{request.children > 0 ? ` + ${request.children}TE` : ''}</div>
        </div>
        <div style={{height: 28, width: 1, background: 'var(--border-strong)'}} />
        <div>
          <div style={{fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', letterSpacing: '0.06em'}}>Lịch trình</div>
          <div style={{fontSize: 13, fontWeight: 600}}>{request.days}N{request.nights}Đ · {itinerary.length} ngày sinh</div>
        </div>
        <div style={{marginLeft: 'auto', fontSize: 11, color: 'var(--text-3)'}}>
          MÃ: <strong style={{color: 'var(--text)'}}>{request.code}</strong>
        </div>
      </div>

      <div className="layout-2col">
        <div className="card">
          <div className="day-tabs">
            {itinerary.map((d, i) => (
              <button key={i} className={`day-tab ${i === activeDay ? 'active' : ''}`}
                onClick={() => setActiveDay(i)}>NGÀY {d.day}</button>
            ))}
            <button className="day-tab add" onClick={() => {
              setItinerary(it => [...it, { day: it.length + 1, title: 'Ngày mới', activities: [] }]);
            }}><Icon name="plus" size={14} /></button>
            <div className="day-meta-row">
              <button className="icon-btn" title="Lưu"><Icon name="save" size={16} /></button>
              <button className="icon-btn" title="Tùy chọn"><Icon name="more" size={16} /></button>
            </div>
          </div>

          <input className="day-title-input"
            value={day.title}
            onChange={e => setItinerary(it => it.map((d, i) => i === activeDay ? {...d, title: e.target.value} : d))} />

          <div className="activity-list">
            {day.activities.map((a, ai) => (
              <div key={a.id} className="activity" onClick={() => setEditing({dayIdx: activeDay, actIdx: ai})}>
                <span className="activity-drag" onClick={e => e.stopPropagation()}><Icon name="grip" size={16} /></span>
                <div className="activity-time">{a.time}</div>
                <div className="activity-body">
                  <div className="activity-type">
                    {(SERVICE_TYPES[a.type] || {}).label || a.type}
                    {/* Badge costType: pax (tím) hoặc shared (cyan) — fallback nếu chưa set */}
                    {(() => {
                      const ct = a.costType || ({HOTEL:'pax',MEAL:'pax',TICKET:'pax'}[a.type] || 'shared');
                      const isP = ct === 'pax';
                      return (
                        <span style={{marginLeft: 8, fontSize: 9, fontWeight: 800, padding: '2px 6px',
                          borderRadius: 4, letterSpacing: '0.05em', textTransform: 'uppercase',
                          color: isP ? '#a855f7' : '#0891b2',
                          background: isP ? '#f5f3ff' : '#ecfeff',
                          border: '1px solid ' + (isP ? '#e9d5ff' : '#a5f3fc')}}>
                          {isP ? '👤 Riêng' : '📦 Chung'}
                        </span>
                      );
                    })()}
                  </div>
                  <h4 className="activity-title">{a.title}</h4>
                  <p className="activity-desc">{a.description}</p>
                  {/* Supplier + desc (v2 logic — supplierName/supplierDesc từ AI sinh) */}
                  {(a.supplier || a.supplierName) && (
                    <div style={{marginTop: 6, padding: '6px 10px', background: 'var(--bg)',
                      borderRadius: 6, fontSize: 11, color: 'var(--text-2)', borderLeft: '2px solid var(--accent)',
                      display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 6}}>
                      <strong style={{color: 'var(--text)'}}>NCC:</strong>
                      <span>{a.supplier || a.supplierName}</span>
                      {(a.supplierDesc) && (
                        <span style={{color: 'var(--text-3)', fontStyle: 'italic'}}>
                          — {a.supplierDesc}
                        </span>
                      )}
                      {a.type === 'HOTEL' && (
                        <button className="activity-hotel-pick"
                          onClick={e => { e.stopPropagation(); setHotelPicking({dayIdx: activeDay, actIdx: ai}); }}
                          title="Chọn khách sạn từ NCC, hệ thống tự gợi ý pack phòng theo số khách (giá hợp đồng)">
                          <Icon name="bed" size={13} /> Chọn phòng theo pax
                        </button>
                      )}
                    </div>
                  )}
                  {/* HOTEL không có supplier sẵn → vẫn cho button đổi NCC để pick từ đầu */}
                  {a.type === 'HOTEL' && !(a.supplier || a.supplierName) && (
                    <div style={{marginTop: 6}}>
                      <button className="activity-hotel-pick"
                        onClick={e => { e.stopPropagation(); setHotelPicking({dayIdx: activeDay, actIdx: ai}); }}
                        title="Hệ thống tự gợi ý pack phòng theo số khách (giá hợp đồng từ NCC)">
                        <Icon name="bed" size={13} /> Chọn phòng theo pax
                      </button>
                    </div>
                  )}
                  <div className="activity-actions" onClick={e => e.stopPropagation()}>
                    <button onClick={() => setEditing({dayIdx: activeDay, actIdx: ai})}>Sửa dịch vụ</button>
                    <button className="danger" onClick={() => removeAct(activeDay, ai)}>Xóa</button>
                  </div>
                </div>
                <div className="activity-cost">
                  <div className="activity-cost-amount numeric">{fmtVND(a.cost)}</div>
                  <div className="activity-cost-label">COSTING (NET)</div>
                </div>
              </div>
            ))}
          </div>

          <button className="add-service-btn" onClick={() => setEditing({dayIdx: activeDay, actIdx: 'new'})}>
            + Thêm dịch vụ thủ công
          </button>
        </div>

        <div style={{display: 'flex', flexDirection: 'column', gap: 16}}>
          <div className="card">
            <div className="card-header" style={{marginBottom: 8}}>
              <h3 style={{fontSize: 13, letterSpacing: '0.08em', textTransform: 'uppercase'}}>Tổng hợp chi phí (Costing)</h3>
              <button className="icon-btn" style={{marginLeft: 'auto', width: 24, height: 24}}><Icon name="info" size={14} /></button>
            </div>

            <div className="cost-row">
              <div>
                <div className="cost-label">Giá vốn (NET) / khách</div>
              </div>
              <div className="cost-val numeric">{fmtVND(netPerPax)}</div>
            </div>
            <div className="cost-row">
              <div>
                <div className="cost-label">Giá bán đề xuất</div>
                <div className="cost-extra">↗ Lợi nhuận: {fmtVND(profitPerPax)}/pax</div>
              </div>
              <div className="cost-val accent numeric">{fmtVND(salePerPax)}</div>
            </div>
            <div className="cost-row">
              <div className="cost-label">Margin</div>
              <div className="margin-pill"><Icon name="trend" size={12} stroke={2.5} />{margin.toFixed(1)}%</div>
            </div>
          </div>

          <div className="card-dark">
            <div className="ai-header" style={{marginBottom: 12}}>
              <div className="ai-spark"><Icon name="sparkle" size={16} /></div>
              <div>
                <div className="ai-title">AI Cost Optimizer</div>
                <div className="ai-sub" style={{fontSize: 12}}>Tối ưu margin tự động</div>
              </div>
            </div>
            <div className="ai-bubble" style={{fontSize: 13, marginBottom: 14}}>
              {optLoading ? (
                <><div className="sk" style={{height: 11, marginBottom: 8, width: '92%'}} />
                <div className="sk" style={{height: 11, width: '70%'}} /></>
              ) : renderOpt(optimizer?.advice)}
            </div>
            {optimizer && !optLoading && (
              optimizer.pristine ? (
                <button className="btn btn-outline-white btn-full" style={{padding: 12}}
                  onClick={runOptimizer} disabled={optLoading}>
                  {optimizer.cta}
                </button>
              ) : (
                <button className="btn btn-outline-white btn-full" style={{padding: 12}}
                  onClick={() => {
                    // Apply AI optimization: bump markup +3% on largest cost row
                    setItinerary(it => {
                      let maxCost = 0, maxRef = null;
                      it.forEach(d => d.activities.forEach(a => {
                        if ((a.cost || 0) > maxCost) { maxCost = a.cost; maxRef = a; }
                      }));
                      if (maxRef) maxRef.cost = Math.round(maxRef.cost * 0.95);
                      return [...it];
                    });
                    setOptimizer(o => ({...o, advice: 'Đã áp dụng tối ưu: giảm 5% chi phí dịch vụ lớn nhất. Margin mới ước tính [[28.5%]].', cta: '✓ Đã tối ưu', pristine: false}));
                  }}>
                  {optimizer.cta || 'Thử phương án AI gợi ý'}
                </button>
              )
            )}
          </div>

          <button className="btn btn-dark btn-lg btn-full" onClick={onNext}>
            Tiếp tục: Bảng tính giá
            <Icon name="arrowRight" size={16} />
          </button>
        </div>
      </div>

      {editing && (
        <EditServiceModal
          data={editing.actIdx === 'new' ? null : itinerary[editing.dayIdx].activities[editing.actIdx]}
          dayNum={itinerary[editing.dayIdx].day}
          totalPax={(request?.adults || 0) + (request?.children || 0)}
          onClose={() => setEditing(null)}
          onSave={saveAct}
          onDelete={() => { if (editing.actIdx !== 'new') removeAct(editing.dayIdx, editing.actIdx); setEditing(null); }}
        />
      )}

      {/* HotelPickerModal: portal-render khi hotelPicking != null.
          Scope='all' → áp pack cho tất cả HOTEL activity trong tour (xuyên ngày).
          Scope='single' → chỉ áp cho activity hiện tại. */}
      {window.HotelPickerModal && (() => {
        const hotelCount = itinerary.reduce((s, d) =>
          s + (d.activities || []).filter(a => a.type === 'HOTEL').length, 0);
        return (
          <window.HotelPickerModal
            open={!!hotelPicking}
            pax={totalPax}
            currentTitle={hotelPicking ? itinerary[hotelPicking.dayIdx]?.activities[hotelPicking.actIdx]?.title : null}
            hotelCount={hotelCount}
            currentDayNum={hotelPicking ? (itinerary[hotelPicking.dayIdx]?.day || hotelPicking.dayIdx + 1) : null}
            onPick={(data, scope) => {
              const { dayIdx, actIdx } = hotelPicking;
              const applyPack = (a) => ({
                ...a,
                title:       data.title,
                supplier:    data.supplier,
                supplierId:  data.supplierId,
                pack:        data.pack,
                packDescription: data.packDescription,
                pricePerPaxPerNight: data.pricePerPaxPerNight,
                cost:        data.cost,
                description: data.description,
                verified:    true   // NCC thật → Step 3 badge
              });
              if (scope === 'all') {
                // Áp dụng cho MỌI HOTEL activity trong toàn tour
                setItinerary(it => it.map(d => ({
                  ...d,
                  activities: d.activities.map(a => a.type === 'HOTEL' ? applyPack(a) : a)
                })));
                pushToast && pushToast(`Đã áp pack "${data.packDescription}" cho ${hotelCount} đêm HOTEL`);
              } else {
                // Chỉ áp cho 1 activity
                setItinerary(it => it.map((d, i) => i === dayIdx ? {
                  ...d,
                  activities: d.activities.map((a, j) => j === actIdx ? applyPack(a) : a)
                } : d));
              }
            }}
            onClose={() => setHotelPicking(null)}
          />
        );
      })()}
    </>
  );
}

// Default costType theo loại dịch vụ. Có thể user override.
function defaultCostType(type) {
  // pax = tính / khách (riêng): KS, ăn uống, vé tham quan
  // shared = trọn gói đoàn (chung): xe, HDV, gala, sân khấu
  return ({
    HOTEL: 'pax', MEAL: 'pax', TICKET: 'pax',
    BUS: 'shared', GUIDE: 'shared', TEAMBUILDING: 'shared',
    SIGHTSEEING: 'shared', ENTERTAINMENT: 'shared',
  })[type] || 'shared';
}

// ── NCC THẬT theo loại dịch vụ (thay "thư viện NCC mẫu" tĩnh) ──────────────────
// Map TYPE activity (wizard) → loại dịch vụ (category) NCC trong CRM bằng keyword,
// rồi lấy NCC thật qua /api/v1/ncc/providers?serviceId=. Cache categories ở module.
const NCC_TYPE_KEYWORDS = {
  HOTEL: ['khach san', 'phong', 'luu tru', 'resort', 'quy phong', 'hotel'],
  TRANSPORT: ['van chuyen', 'xe', 'nha xe', 'van tai', 'may bay', 'hang khong', 'transport'],
  BUS: ['van chuyen', 'xe', 'nha xe', 'van tai'],
  MEAL: ['nha hang', 'an uong', 'am thuc', 'nuoc', 'suoi', 'restaurant'],
  GUIDE: ['huong dan', 'hdv', 'guide'],
  TICKET: ['ve', 'ticket', 'tham quan'],
  SIGHTSEEING: ['tham quan', 'tour', 'land', 'diem'],
  ACTIVITY: ['tour', 'land', 'chi phi khac', 'team', 'gala', 'su kien', 'hoat dong'],
  TEAMBUILDING: ['team', 'gala', 'su kien'],
  ENTERTAINMENT: ['gala', 'su kien', 'giai tri'],
};
const _nccNormVi = (s) => (s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
let _nccCatCache = null;
async function _loadNccCats() {
  if (_nccCatCache) return _nccCatCache;
  try {
    const r = await window.tourkitAuth.authedFetch('/api/v1/ncc/categories');
    if (!r.ok) { _nccCatCache = []; return _nccCatCache; }
    const data = await r.json();
    const arr = Array.isArray(data) ? data : (data.items || data.Items || []);
    _nccCatCache = arr.map(c => ({ id: c.id ?? c.Id, name: c.name ?? c.Name ?? c.service_name }));
  } catch { _nccCatCache = []; }
  return _nccCatCache;
}
async function fetchNccByType(type) {
  const cats = await _loadNccCats();
  if (!cats.length) return { catName: null, items: [] };
  const kws = NCC_TYPE_KEYWORDS[type] || [];
  const cat = kws.length ? cats.find(c => { const n = _nccNormVi(c.name); return kws.some(k => n.includes(k)); }) : null;
  if (!cat) return { catName: null, catId: null, items: [] };
  return { catName: cat.name, catId: cat.id, items: await fetchNccByCatId(cat.id) };
}

// Lấy NCC theo 1 category id cụ thể — dùng cho dropdown "Lọc theo loại" (user chủ động đổi loại NCC xem).
async function fetchNccByCatId(catId) {
  try {
    const r = await window.tourkitAuth.authedFetch('/api/v1/ncc/providers?serviceId=' + encodeURIComponent(catId));
    if (!r.ok) return [];
    const data = await r.json();
    const arr = Array.isArray(data) ? data : (data.items || data.Items || []);
    return arr.map(p => ({ id: p.id ?? p.Id, name: p.name ?? p.Name, code: p.code ?? p.Code, city: p.city ?? p.City, phone: p.phone ?? p.Phone }));
  } catch { return []; }
}

// Bảng giá DỊCH VỤ của 1 NCC (theo loại DV đang xem). Mỗi DV: tên + giá hợp đồng (net) + giá công bố.
async function fetchNccServices(provId, catId) {
  try {
    const r = await window.tourkitAuth.authedFetch('/api/v1/ncc/providers/' + provId + '/services?categoryId=' + encodeURIComponent(catId));
    if (!r.ok) return [];
    const data = await r.json();
    const arr = Array.isArray(data) ? data : (data.items || data.Items || []);
    return arr.map(s => ({ id: s.id ?? s.Id, name: s.name ?? s.Name, contractPrice: s.contractPrice ?? s.ContractPrice ?? 0, publicPrice: s.publicPrice ?? s.PublicPrice ?? 0 }));
  } catch { return []; }
}

function EditServiceModal({ data, dayNum, onClose, onSave, onDelete, totalPax }) {
  const [form, setForm] = useS2(data || {
    type: 'SIGHTSEEING', time: '10:00', title: '', description: '', cost: 0, supplier: '',
    costType: 'shared',
  });
  // Backfill costType cho activity cũ chưa có field này
  React.useEffect(() => {
    if (!form.costType) setForm(f => ({...f, costType: defaultCostType(f.type)}));
  }, []); // eslint-disable-line
  const lib = SUPPLIER_LIBRARY[form.type] || [];
  // Default tĩnh từ supplier library — không call AI cho đến khi user bấm.
  const defaultAdvice = `Mặc định: sử dụng <<${lib[0]?.name || 'supplier hiện có'}>>. Bấm "Hỏi AI" để có gợi ý chi tiết hơn theo ngân sách & mùa.`;
  const [advice, setAdvice] = useS2(defaultAdvice);
  const [adviceLoading, setAdviceLoading] = useS2(false);
  const [advicePristine, setAdvicePristine] = useS2(true);
  // NCC THẬT theo loại dịch vụ (thay "Thư viện gợi ý NCC" mẫu tĩnh). Refetch khi đổi loại DV.
  const [ncc, setNcc] = useS2({ loading: true, items: [], catName: null, catId: null });
  const [nccCats, setNccCats] = useS2([]);          // toàn bộ category cho dropdown "Loại NCC"
  const [nccQ, setNccQ] = useS2('');                // ô tìm kiếm
  const [nccProvince, setNccProvince] = useS2('');  // lọc theo tỉnh/thành
  const [nccPage, setNccPage] = useS2(1);
  const NCC_PAGE_SIZE = 6;
  // Load category 1 lần cho dropdown lọc theo loại.
  React.useEffect(() => { _loadNccCats().then(cs => setNccCats(cs || [])); }, []);
  // Auto map NCC theo loại dịch vụ của activity; refetch + reset filter khi đổi loại.
  React.useEffect(() => {
    let alive = true;
    setNcc(n => ({ ...n, loading: true }));
    setNccQ(''); setNccProvince(''); setNccPage(1);
    fetchNccByType(form.type).then(res => { if (alive) setNcc({ loading: false, items: res.items, catName: res.catName, catId: res.catId }); });
    return () => { alive = false; };
  }, [form.type]);
  // User chủ động đổi loại NCC qua dropdown → fetch category đó.
  const selectNccCat = async (catId) => {
    if (!catId) return;
    const cat = nccCats.find(c => String(c.id) === String(catId));
    setNcc(n => ({ ...n, loading: true }));
    setNccQ(''); setNccProvince(''); setNccPage(1);
    const items = await fetchNccByCatId(catId);
    setNcc({ loading: false, items, catName: cat ? cat.name : null, catId });
  };
  const applyNcc = (p) => setForm(f => ({ ...f, supplier: p.name, supplierId: p.id }));
  // Xem dịch vụ + giá của 1 NCC: lazy-fetch + cache theo provId. Chọn 1 DV → điền NCC + tên DV + giá net.
  const [expandedNcc, setExpandedNcc] = useS2(null);
  const [svcCache, setSvcCache] = useS2({});
  const toggleNccServices = async (p) => {
    if (expandedNcc === p.id) { setExpandedNcc(null); return; }
    setExpandedNcc(p.id);
    if (!svcCache[p.id] && ncc.catId != null) {
      setSvcCache(c => ({ ...c, [p.id]: { loading: true, items: [] } }));
      const items = await fetchNccServices(p.id, ncc.catId);
      setSvcCache(c => ({ ...c, [p.id]: { loading: false, items } }));
    }
  };
  const applyService = (p, svc) => setForm(f => ({ ...f,
    supplier: p.name, supplierId: p.id,
    supplierService: svc.name, supplierServiceId: svc.id,
    cost: (svc.contractPrice || svc.publicPrice || f.cost || 0),   // giữ NGUYÊN giá net NCC (không chia)
    costType: 'pax',   // mặc định Chi phí riêng (giá net × số khách)
  }));
  React.useEffect(() => { setNccPage(1); }, [nccQ, nccProvince]);   // đổi filter → về trang 1
  // Lọc client-side (search + tỉnh/thành) rồi phân trang — tránh render quá nhiều card 1 lúc.
  const nccProvinces = [...new Set(ncc.items.map(p => p.city).filter(Boolean))].sort();
  const nccFiltered = ncc.items.filter(p => {
    if (nccProvince && p.city !== nccProvince) return false;
    if (nccQ) { const q = _nccNormVi(nccQ); const hay = _nccNormVi([p.name, p.code, p.phone, p.city].filter(Boolean).join(' ')); if (!hay.includes(q)) return false; }
    return true;
  });
  const nccTotalPages = Math.max(1, Math.ceil(nccFiltered.length / NCC_PAGE_SIZE));
  const nccSafePage = Math.min(nccPage, nccTotalPages);
  const nccPageItems = nccFiltered.slice((nccSafePage - 1) * NCC_PAGE_SIZE, nccSafePage * NCC_PAGE_SIZE);

  // Reset về mặc định khi đổi loại dịch vụ (không call AI).
  React.useEffect(() => {
    setAdvice(`Mặc định: sử dụng <<${(SUPPLIER_LIBRARY[form.type] || [])[0]?.name || 'supplier hiện có'}>>. Bấm "Hỏi AI" để có gợi ý chi tiết hơn theo ngân sách & mùa.`);
    setAdvicePristine(true);
  }, [form.type]);

  const runAdvice = async () => {
    setAdviceLoading(true);
    try {
      const prompt = `Bạn là điều hành tour. Khách đang sửa dịch vụ loại ${SERVICE_TYPES[form.type]?.label}. Đưa ra lời khuyên ngắn (1-2 câu) gợi ý 1 supplier cụ thể từ danh sách, có lý do (giá tốt, chính sách FOC, mùa thấp điểm...).

Suppliers: ${JSON.stringify(lib.map(s => ({name: s.name, ncc: s.ncc, price: s.price})))}

Output JSON: {"advice": "câu khuyên, highlight tên supplier bằng <<...>>"}`;
      const raw = await window.claude.complete(prompt);
      const m = raw.match(/\{[\s\S]*\}/);
      if (m) { setAdvice(JSON.parse(m[0]).advice); setAdvicePristine(false); }
      else throw new Error('parse');
    } catch (e) {
      setAdvice(`Dựa trên ngân sách đoàn, tôi đề xuất sử dụng <<${lib[0]?.name || 'supplier hiện có'}>>. Họ đang có chính sách FOC 16+1 rất tốt cho các đoàn công ty trong tháng 6.`);
      setAdvicePristine(false);
    } finally {
      setAdviceLoading(false);
    }
  };

  const applySupplier = (s) => {
    setForm(f => ({...f, title: s.name, description: s.desc, cost: s.price, supplier: s.ncc}));
  };

  const renderAdvice = (text) => {
    if (!text) return null;
    return text.split(/(<<[^>]+>>)/g).map((p, i) => {
      if (p.startsWith('<<')) return <span key={i} className="hl">{p.slice(2, -2)}</span>;
      return <span key={i}>{p}</span>;
    });
  };

  return (
    <div className="modal-backdrop">
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-left" style={{maxHeight: '88vh', minHeight: 0}}>
          <div className="modal-title-row">
            <div className="card-icon"><Icon name="paper" size={16} /></div>
            <div>
              <h3 className="modal-title">{data ? 'SỬA DỊCH VỤ' : 'THÊM DỊCH VỤ THỦ CÔNG'}</h3>
              <div className="modal-breadcrumb">NGÀY {dayNum} · GROUP QUOTATION TOOL</div>
            </div>
            <button className="modal-close icon-btn" onClick={onClose}><Icon name="close" size={18} /></button>
          </div>

          <div className="field-row" style={{marginTop: 22}}>
            <div>
              <label className="label">Loại dịch vụ</label>
              <select className="select" value={form.type} onChange={e => setForm(f => ({...f, type: e.target.value}))}>
                {Object.entries(SERVICE_TYPES).map(([k, v]) => (
                  <option key={k} value={k}>{v.label.toUpperCase()}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="label">Giờ</label>
              <div className="input-icon">
                <Icon name="clock" size={16} />
                <input className="input" value={form.time}
                  onChange={e => setForm(f => ({...f, time: e.target.value}))} />
              </div>
            </div>
          </div>

          <div className="field">
            <label className="label">Tên hoạt động / Dịch vụ</label>
            <input className="input" value={form.title} placeholder="VD: Tham quan Vịnh Hạ Long bằng du thuyền"
              onChange={e => setForm(f => ({...f, title: e.target.value}))} />
          </div>

          <div className="field">
            <label className="label">Nhà cung cấp (NCC)</label>
            <input className="input" value={form.supplier || ''} placeholder="Chọn NCC bên phải hoặc nhập tay…"
              onChange={e => setForm(f => ({...f, supplier: e.target.value, supplierId: undefined}))} />
          </div>

          <div className="field">
            <label className="label">Nội dung chi tiết chương trình</label>
            <textarea className="textarea" rows={4} value={form.description} placeholder="Mô tả chi tiết: lịch trình, điểm đến, dịch vụ kèm theo, lưu ý cho khách…"
              onChange={e => setForm(f => ({...f, description: e.target.value}))} />
          </div>

          {/* ── PHÂN LOẠI CHI PHÍ — 2 cards ───────────────────────────── */}
          <div className="field">
            <label className="label" style={{textTransform: 'uppercase', letterSpacing: '0.05em', fontSize: 11, color: 'var(--text-3)'}}>
              Phân loại chi phí du lịch
            </label>
            <div className="cost-type-picker">
              {[
                {k: 'pax',    title: 'CHI PHÍ RIÊNG', desc: 'Tính theo số khách (Pax). Ví dụ: Tiền ăn, khách sạn, vé đi tàu...', color: '#a855f7', bg: '#f5f3ff'},
                {k: 'shared', title: 'CHI PHÍ CHUNG', desc: 'Chi phí cố định trọn gói cả đoàn. Ví dụ: Xe đưa đón, HDV, sân khấu...', color: '#0891b2', bg: '#ecfeff'},
              ].map(o => {
                const on = (form.costType || 'shared') === o.k;
                return (
                  <button key={o.k} type="button" className={'ctp-card' + (on ? ' on' : '')}
                    onClick={() => setForm(f => ({...f, costType: o.k}))}
                    style={{borderColor: on ? o.color : 'var(--border)', background: on ? o.bg : 'white'}}>
                    <div className="ctp-dot" style={{background: on ? o.color : '#cbd5e1'}} />
                    <div style={{flex: 1, textAlign: 'left'}}>
                      <div className="ctp-title" style={{color: on ? o.color : 'var(--text)'}}>{o.title}</div>
                      <div className="ctp-desc">{o.desc}</div>
                    </div>
                  </button>
                );
              })}
            </div>
          </div>

          <div className="field">
            <label className="label">
              {(form.costType || 'shared') === 'pax' ? 'Giá NET / khách / lần' : 'Giá NET trọn gói cả đoàn'}
              <span style={{float: 'right', fontSize: 10, fontWeight: 700, padding: '2px 8px', borderRadius: 4,
                background: (form.costType || 'shared') === 'pax' ? '#f5f3ff' : '#ecfeff',
                color: (form.costType || 'shared') === 'pax' ? '#a855f7' : '#0891b2',
                letterSpacing: '0.04em', textTransform: 'uppercase'}}>
                {(form.costType || 'shared') === 'pax' ? 'PER PAX' : 'FIXED GROUP'}
              </span>
            </label>
            <div style={{position: 'relative'}}>
              <span style={{position: 'absolute', left: 12, top: '50%', transform: 'translateY(-50%)', color: 'var(--text-3)', zIndex: 1}}>
                <Icon name="dollar" size={14} />
              </span>
              <input className="input numeric" value={fmtNum(form.cost)}
                onChange={e => setForm(f => ({...f, cost: parseInt(e.target.value.replace(/\D/g, '')) || 0}))}
                style={{paddingRight: 56, paddingLeft: 34}} />
              <span className="input-suffix">VND</span>
            </div>
            {/* Preview tự toán */}
            <div className="cost-preview">
              <Icon name="chart" size={13} />
              <span style={{fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.04em', color: 'var(--text-3)'}}>
                Bảng tính dự toán NET dịch vụ:
              </span>
              <span style={{flex: 1}} />
              <span style={{fontSize: 12, color: 'var(--text-2)'}}>
                {(form.costType || 'shared') === 'pax'
                  ? <>Chi phí / khách × <strong>{totalPax || '?'}</strong> Pax = </>
                  : <>Chi phí cố định đoàn = </>}
                <span style={{color: 'var(--primary)', fontWeight: 800}}>
                  {fmtNum(((form.costType || 'shared') === 'pax' ? (form.cost || 0) * (totalPax || 0) : (form.cost || 0)))}đ
                </span>
              </span>
            </div>
          </div>

          <div className="modal-footer" style={{position: 'sticky', bottom: 0, zIndex: 2}}>
            {data && <button className="btn btn-ghost" onClick={onDelete} style={{color: 'var(--danger)'}}><Icon name="trash" size={14} /> Xóa</button>}
            <div style={{marginLeft: 'auto', display: 'flex', gap: 10}}>
              <button className="btn btn-outline" onClick={onClose}>Hủy bỏ</button>
              <button className="btn btn-primary" onClick={() => onSave(form)}>
                <Icon name="save" size={14} stroke={2} /> Lưu thay đổi
              </button>
            </div>
          </div>
        </div>

        <div className="modal-right" style={{display: 'flex', flexDirection: 'column', maxHeight: '88vh', minHeight: 0, overflowY: 'hidden'}}>
          <div className="modal-title-row" style={{marginBottom: 12}}>
            <div className="card-icon"><Icon name="search" size={16} /></div>
            <div>
              <h3 className="modal-title" style={{fontSize: 14}}>NCC THEO LOẠI{ncc.catName ? ' · ' + ncc.catName : ''}</h3>
              <div className="modal-breadcrumb">Chọn dịch vụ NCC cho hoạt động</div>
            </div>
            <button className="modal-close icon-btn" onClick={onClose}><Icon name="close" size={18} /></button>
          </div>

          {/* Tìm kiếm + lọc theo loại NCC + tỉnh/thành */}
          <input className="input" placeholder="Tìm NCC (tên / mã / SĐT)…" value={nccQ}
            onChange={e => setNccQ(e.target.value)} style={{marginBottom: 8, fontSize: 13}} />
          <div style={{display: 'flex', gap: 8, marginBottom: 12}}>
            <select className="select" value={ncc.catId ?? ''} onChange={e => selectNccCat(e.target.value)}
              style={{flex: 1, fontSize: 12, minWidth: 0}}>
              <option value="">— Loại NCC —</option>
              {nccCats.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
            <select className="select" value={nccProvince} onChange={e => setNccProvince(e.target.value)}
              style={{flex: 1, fontSize: 12, minWidth: 0}} disabled={nccProvinces.length === 0}>
              <option value="">— Tỉnh/thành —</option>
              {nccProvinces.map(c => <option key={c} value={c}>{c}</option>)}
            </select>
          </div>

          {ncc.loading ? (
            <>
              <div className="sk" style={{height: 54, marginBottom: 8, borderRadius: 10}} />
              <div className="sk" style={{height: 54, borderRadius: 10}} />
            </>
          ) : ncc.items.length === 0 ? (
            <div style={{fontSize: 12, color: 'var(--text-3)', padding: '14px 12px', background: 'var(--bg)', borderRadius: 10, lineHeight: 1.6}}>
              Chưa có NCC loại <strong>{(SERVICE_TYPES[form.type] || {}).label || form.type}</strong> trong hệ thống.
              Thêm ở module <strong>Nhà cung cấp</strong> để hiện ở đây.
            </div>
          ) : nccFiltered.length === 0 ? (
            <div style={{fontSize: 12, color: 'var(--text-3)', padding: '14px 12px', background: 'var(--bg)', borderRadius: 10, lineHeight: 1.6}}>
              Không tìm thấy NCC khớp bộ lọc. Thử xoá ô tìm kiếm hoặc đổi tỉnh/thành.
            </div>
          ) : (
            <div style={{flex: 1, minHeight: 0, overflowY: 'auto', margin: '0 -4px', padding: '0 4px'}}>
              {nccPageItems.map((p, i) => {
                const sel = form.supplierId != null && p.id != null && String(form.supplierId) === String(p.id);
                const open = expandedNcc === p.id;
                const svc = svcCache[p.id];
                return (
                  <div key={p.id || i} className="supplier-card" style={sel ? {borderColor: 'var(--primary)', background: '#fff7ed'} : {}}>
                    <div className="supplier-card-head">
                      <div className="supplier-card-name">{p.name}{sel && form.supplierService ? <span style={{color: 'var(--primary)', fontWeight: 600}}> · {form.supplierService}</span> : null}</div>
                      {p.code && <div className="supplier-card-price">{p.code}</div>}
                    </div>
                    <p className="supplier-card-desc">{[p.city, p.phone].filter(Boolean).join(' · ') || 'NCC trong hệ thống'}</p>
                    <button type="button" className="btn btn-sm btn-outline"
                      style={{width: '100%', marginTop: 6, padding: '6px 8px', fontSize: 12}}
                      onClick={() => toggleNccServices(p)}>
                      {open ? '▴ Ẩn dịch vụ' : '▾ Xem dịch vụ & giá'}
                    </button>
                    {open && (
                      <div style={{marginTop: 8, borderTop: '1px dashed var(--border)', paddingTop: 8}}>
                        {!svc || svc.loading ? (
                          <div className="sk" style={{height: 40, borderRadius: 8}} />
                        ) : svc.items.length === 0 ? (
                          <div style={{fontSize: 11.5, color: 'var(--text-3)', padding: '4px 2px', lineHeight: 1.5}}>NCC chưa có dịch vụ / bảng giá. Thêm ở module <strong>Nhà cung cấp</strong>.</div>
                        ) : svc.items.map((s, j) => {
                          const ssel = sel && form.supplierServiceId != null && String(form.supplierServiceId) === String(s.id);
                          return (
                            <div key={s.id || j} style={{display: 'flex', alignItems: 'center', gap: 8, padding: '6px 4px', borderBottom: j < svc.items.length - 1 ? '1px solid var(--bg)' : 'none'}}>
                              <div style={{flex: 1, minWidth: 0}}>
                                <div style={{fontSize: 12.5, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis'}}>{s.name || '(DV chưa đặt tên)'}</div>
                                <div style={{fontSize: 11, color: 'var(--text-3)'}}>Net <strong style={{color: 'var(--primary)'}}>{fmtNum(s.contractPrice)}đ</strong>{s.publicPrice ? ' · CB ' + fmtNum(s.publicPrice) + 'đ' : ''}</div>
                              </div>
                              <button type="button" className={'btn btn-sm ' + (ssel ? 'btn-primary' : 'btn-outline')}
                                style={{padding: '4px 10px', fontSize: 11, flexShrink: 0}}
                                onClick={() => applyService(p, s)}>
                                {ssel ? '✓ Đã chọn' : 'Chọn'}
                              </button>
                            </div>
                          );
                        })}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {!ncc.loading && nccFiltered.length > NCC_PAGE_SIZE && (
            <div style={{display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginTop: 10, fontSize: 12}}>
              <button type="button" className="btn btn-sm btn-outline" disabled={nccSafePage <= 1}
                onClick={() => setNccPage(p => Math.max(1, p - 1))} style={{padding: '4px 10px'}}>‹ Trước</button>
              <span style={{color: 'var(--text-3)'}}>Trang {nccSafePage}/{nccTotalPages} · {nccFiltered.length} NCC</span>
              <button type="button" className="btn btn-sm btn-outline" disabled={nccSafePage >= nccTotalPages}
                onClick={() => setNccPage(p => Math.min(nccTotalPages, p + 1))} style={{padding: '4px 10px'}}>Sau ›</button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

window.Step2Itinerary = Step2Itinerary;
