// pages/wizard.jsx — Trang chính: wizard 4 bước build tour + báo giá.
// Mỗi page nhận props ổn định từ App shell (pushToast, tweaks). State riêng (step,
// request, itinerary, rows, marketing) sống trong component này — chuyển page = mất state.
// Nếu muốn persist giữa pages, lift state lên App hoặc dùng tourkitStorage.

const { useState: _uS, useEffect: _uE } = React;

// Per-task model routing CHỈ áp dụng khi provider là opencode-go (model id tương thích).
// Provider khác (9routes, claude-builtin) → dùng model user chọn trong Settings.
// Resolve at call time vì user có thể đổi provider giữa session.
const WIZARD_TASK_MODELS_OPENCODE = {
  meta:         'deepseek-v4-flash',
  activities:   'deepseek-v4-flash',
  descriptions: 'deepseek-v4-flash',
  marketing:    'deepseek-v4-flash',
  mega:         'deepseek-v4-flash',
};

function pickModel(task) {
  const cfg = window.tourkit.ai.getConfig();
  if (cfg.provider === 'opencode-go') return WIZARD_TASK_MODELS_OPENCODE[task];
  return undefined;   // undefined → ai-provider fallback về cfg.model
}

const WIZARD_STEPS = [
  { id: 1, label: 'Yêu cầu khách hàng', icon: 'users' },
  { id: 2, label: 'AI Lập lịch trình',   icon: 'list' },
  { id: 3, label: 'Bảng tính giá',       icon: 'chart' },
  { id: 4, label: 'Xuất báo giá',        icon: 'paper' }
];

// matchMedia-based isMobile hook (≤640px) — cùng pattern deals.jsx / customers.jsx.
function _wzIsMobile(bp = 640) {
  const [m, setM] = _uS(() => window.innerWidth <= bp);
  _uE(() => {
    const check = () => setM(window.innerWidth <= bp);
    window.addEventListener('resize', check);
    check();
    return () => window.removeEventListener('resize', check);
  }, []);
  return m;
}

// ─── 1 card báo giá (mobile ≤640px) — bảng 8 cột không vừa màn điện thoại ────
function QuoteCard({ t, statusLabel, statusClass, onOpen, onDelete }) {
  const req = t.request || {};
  const totalPax = (req.adults || 0) + (req.children || 0);
  const totalNet = (t.rows || []).reduce((s, r) => s + (r.priceNet || 0), 0);
  const salePerPax = t.salePerPax || (totalPax > 0 ? Math.round(totalNet * 1.2 / totalPax) : 0);
  const createdAt = t.createdAt ? new Date(t.createdAt) : null;
  return (
    <div className="wl-qcard" onClick={onOpen}>
      <div className="wl-qcard-top">
        <span className="wl-code">{t.code || t.id?.slice(-6).toUpperCase()}</span>
        <span className={'wl-status ' + statusClass}>{statusLabel}</span>
      </div>
      <div className="wl-qcard-cust">{req.customerName || t.title || '—'}</div>
      <div className="wl-qcard-meta">
        {req.route || '—'} · {req.days}N{req.nights}Đ
        {totalPax > 0 && ` · ${totalPax} khách`}
      </div>
      <div className="wl-qcard-meta">
        Người tạo: {t.createdBy || 'Hệ thống'}
        {createdAt && ' · ' + createdAt.toLocaleDateString('vi-VN')}
      </div>
      <div className="wl-qcard-money">
        <div>
          <div className="wl-qcard-money-label">TỔNG NET</div>
          <div className="wl-qcard-money-val">{fmtVND(totalNet)}</div>
        </div>
        <div style={{textAlign: 'right'}}>
          <div className="wl-qcard-money-label">GIÁ BÁN / KHÁCH</div>
          <div className="wl-qcard-money-val wl-good">{fmtVND(salePerPax)}</div>
        </div>
      </div>
      <div className="wl-qcard-actions" onClick={e => e.stopPropagation()}>
        <button className="wl-action" onClick={onOpen}><Icon name="paper" size={14} /> Xem chi tiết</button>
        <button className="wl-action wl-action-del" onClick={onDelete}><Icon name="trash" size={14} /> Xoá</button>
      </div>
    </div>
  );
}

function WizardPage({ pushToast, tweaks }) {
  const Storage = window.tourkitStorage;
  const Parsers = window.tourkitParsers;

  const [step, setStep] = _uS(1);
  const [view, setView] = _uS('list');            // 'list' (dashboard mặc định) | 'create' (wizard form)
  const isMobile = _wzIsMobile();                 // ≤640px → dashboard render card thay bảng
  const [listFilter, setListFilter] = _uS('all'); // 'all' | 'success' | 'sent' | 'draft'
  const [listSearch, setListSearch] = _uS('');
  const [savedTours, setSavedTours] = _uS([]);
  const [nccCatalog, setNccCatalog] = _uS(null);  // tên NCC thật (TourKit) để ưu tiên khi sinh tour
  const [request, setRequest]       = _uS(window.DEMO_REQUEST);
  // ── Pricing config (v2 logic, lifted để Step 1 config + Step 3 dùng) ─────────
  // hotelStars: tier KS user muốn so sánh ở matrix (3*/4*/5*/6*).
  // paxRanges:  matrix markup theo số pax — đoàn nhỏ markup cao, đoàn lớn thấp.
  const [hotelStars, setHotelStars] = _uS([3, 4, 5]);
  // hotelOptions = { 3?: {providerId, providerName, roomTypeId, roomTypeName, pricePerPaxPerNight},
  //                  4?: {...}, 5?: {...} }
  // Set qua NccTierPicker (Step 1.5). Dùng cho Step 3 (cost) + Step 4 (3 báo giá).
  const [hotelOptions, setHotelOptions] = _uS({});
  // activeTier: số sao đang được CHỌN NHANH ở Step 3 (highlighted báo giá ở Step 4).
  // null → Step 4 ghĩa hiển thị mọi tier như thường.
  const [activeTier, setActiveTier] = _uS(null);
  const [paxRanges, setPaxRanges]   = _uS([
    { from: 1,  to: 14,  markup: 28 },
    { from: 15, to: 30,  markup: 22 },
    { from: 31, to: 200, markup: 18 },
  ]);
  const [itinerary, setItinerary]   = _uS(window.DEMO_ITINERARY);
  const [rows, setRows]             = _uS(window.COSTING_ROWS);
  const [generating, setGenerating] = _uS(false);
  const [genStream, setGenStream]   = _uS('');
  const [genProgress, setGenProgress] = _uS({stage: 'idle', daysTotal: 0, daysDone: 0});
  const [confirmNew, setConfirmNew] = _uS(false);
  const [marketing, setMarketing]   = _uS({
    tourName: 'TOUR DU LỊCH QUY NHƠN',
    tagline: 'HÀNH TRÌNH DI SẢN & BIỂN XANH',
    dayTitles: window.DEMO_ITINERARY.map(d => d.title)
  });

  // ── 2-tier báo giá (Redis draft + SQL commit) — mirror tour-builder pattern.
  // Autosave debounce 1.5s khi state (request/itinerary/rows/marketing) đổi → POST /draft (Redis).
  // Step 4 nút "Lưu báo giá" → POST commit (SQL dbo.TourQuotes).
  const [quoteId, setQuoteId]             = _uS(null);
  const [draftStatus, setDraftStatus]     = _uS('pristine');  // pristine|dirty|autosaving|draft|committed
  const [lastDraftAt, setLastDraftAt]     = _uS(null);
  const [lastCommitAt, setLastCommitAt]   = _uS(null);
  const autosaveTimerRef                  = React.useRef(null);
  const skipNextAutosaveRef               = React.useRef(false);

  // Map state → SaveTourQuoteRequest (mapping indexed columns + full data JSON cho fidelity).
  // QUAN TRỌNG: long/int columns server-side NON-NULL → mọi giá trị NaN/null phải finite fallback 0.
  const safeInt = (n) => {
    const x = Number(n);
    return Number.isFinite(x) ? Math.round(x) : 0;
  };
  const buildQuoteBody = () => {
    // Tổng net + revenue gần đúng (Step 3 có B3 hybrid + costType; ở đây xài approx cho INDEX columns).
    const totalNet = (rows || []).reduce((s, r) => s + ((Number(r.priceNet) || 0) * (Number(r.qty) || 1)), 0);
    const totalRev = (rows || []).reduce((s, r) => {
      const net = (Number(r.priceNet) || 0) * (Number(r.qty) || 1);
      const vat = (Number(r.vat) || 0) / 100, mk = (Number(r.markup) || 0) / 100;
      return s + net * (1 + vat) * (1 + mk);
    }, 0);
    const profit = totalRev - totalNet;
    const margin = totalRev > 0 ? (profit / totalRev) * 100 : null;
    return {
      id: quoteId,
      title:         (marketing && marketing.tourName) || (request && request.route) || null,
      customerName:  (request && request.customerName) || null,
      customerPhone: null,
      marketName:    null,
      tourType:      null,
      startDate:     null,
      endDate:       null,
      adultCount:    safeInt(request && request.adults),
      childCount:    safeInt(request && request.children),
      totalNet:      safeInt(totalNet),
      totalRevenue:  safeInt(totalRev),
      profit:        safeInt(profit),
      marginPercent: margin != null ? Math.round(margin * 100) / 100 : null,
      data: { request, itinerary, marketing, rows, hotelStars, hotelOptions, paxRanges, activeTier, source: 'wizard' },
    };
  };

  // Autosave → Redis
  async function autosaveQuoteDraft() {
    setDraftStatus('autosaving');
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes/draft', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildQuoteBody()),
      });
      const j = await r.json();
      if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
      if (j.id && !quoteId) setQuoteId(j.id);
      setLastDraftAt(j.savedAt || new Date().toISOString());
      setDraftStatus('draft');
    } catch (e) {
      setDraftStatus('dirty');
      console.warn('[wizard autosave] fail:', e.message);
    }
  }

  // Watch state → debounce 1.5s
  _uE(() => {
    if (skipNextAutosaveRef.current) { skipNextAutosaveRef.current = false; return; }
    setDraftStatus(prev => (prev === 'committed' || prev === 'pristine') ? 'dirty' : prev);
    if (autosaveTimerRef.current) clearTimeout(autosaveTimerRef.current);
    autosaveTimerRef.current = setTimeout(autosaveQuoteDraft, 1500);
    return () => { if (autosaveTimerRef.current) clearTimeout(autosaveTimerRef.current); };
  }, [request, itinerary, rows, marketing, hotelStars, hotelOptions, paxRanges, activeTier]);

  // Browser-level guard: warn user nếu đóng tab/reload khi data chưa commit DB
  // (cover trường hợp Redis chết / TTL 24h hết — lúc đó draft Redis cũng mất → user phải biết để bấm Lưu)
  _uE(() => {
    const isUnsaved = draftStatus === 'dirty' || draftStatus === 'draft' || draftStatus === 'autosaving';
    if (!isUnsaved) return;
    const onBeforeUnload = (e) => {
      const msg = 'Báo giá chưa lưu vào hệ thống — sẽ mất nếu Redis tạm hết hạn. Bấm "Lưu ngay vào hệ thống" trước khi đóng.';
      e.preventDefault();
      e.returnValue = msg;
      return msg;
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [draftStatus]);

  // Commit → SQL
  async function commitQuote() {
    try {
      if (autosaveTimerRef.current) { clearTimeout(autosaveTimerRef.current); autosaveTimerRef.current = null; }
      const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildQuoteBody()),
      });
      const j = await r.json();
      if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
      setQuoteId(j.id);
      setDraftStatus('committed');
      setLastCommitAt(j.item?.updatedAt || new Date().toISOString());
      setLastDraftAt(null);
      skipNextAutosaveRef.current = true;
      pushToast('✓ Đã lưu báo giá vào DB');
    } catch (e) { pushToast('Lỗi commit báo giá: ' + e.message, 'error'); }
  }

  const handleGenerate = async (opts = {}) => {
    const SKIP_CACHE = true;
    const forceFresh = SKIP_CACHE || opts.forceFresh === true;
    setGenerating(true);
    setGenStream('');
    setGenProgress({stage: 'meta', daysTotal: request.days, daysDone: 0});
    const dest = request.route.split('-').pop().trim() || request.route;
    let success = false;
    const tGen0 = Date.now();
    console.log(`[Gen] ▶ START · ${request.route} · ${request.days}N${request.nights}Đ · ${request.adults}+${request.children} khách · ${fmtVND(request.budgetPerPax)}/pax · prefs=[${request.preferences.join(', ')}]`);

    Storage.saveRequestToHistory(request);

    const cacheKey = Storage.buildTourCacheKey(request);
    if (!forceFresh) {
      const hit = Storage.readTourCache(request);
      if (hit) {
        const ageHr = (hit.ageMs / 3600e3).toFixed(1);
        const stats = Storage.bumpTourStat('hits');
        console.log(`[Gen] ✓ CACHE HIT · key=${hit.key} · age=${ageHr}h · hitrate=${Storage.tourHitRate(stats)}`);
        setItinerary(hit.value.itinerary);
        if (hit.value.marketing) setMarketing(hit.value.marketing);
        if (Array.isArray(hit.value.rows)) setRows(hit.value.rows);
        success = true;
        setGenerating(false);
        setGenProgress({stage: 'idle', daysTotal: 0, daysDone: 0});
        setStep(2);
        pushToast(`⚡ Cache hit: tour ${dest} (${ageHr}h trước)`);
        return;
      }
    }
    {
      const stats = Storage.bumpTourStat('misses');
      console.log(`[Gen] CACHE MISS · key=${cacheKey} · hitrate=${Storage.tourHitRate(stats)}${forceFresh ? ' (forced fresh)' : ''}`);
    }

    try {
      const totalPax = request.adults + request.children;
      const budgetPerDay = Math.round((request.budgetPerPax * totalPax) / request.days);

      setItinerary([]);

      // Ưu tiên NCC THẬT của công ty (TourKit). Nạp tên NCC → nhét vào prompt.
      const nccNames = await loadNcc();
      const nccBlock = nccNames.length
        ? `\nNCC ƯU TIÊN (dùng ĐÚNG tên này cho cột supplier khi dịch vụ phù hợp, ưu tiên trước nguồn ngoài):\n${nccNames.slice(0, 40).join(', ')}\n`
        : '';

      const megaSystem = 'Bạn xuất tour Việt Nam theo format text với `|` separator. Không JSON, không markdown, không giải thích. Bắt đầu ngay với "TÊN:".';

      const promptAll = `Sinh tour Việt Nam theo format text DƯỚI ĐÂY (không JSON, không markdown, bắt đầu ngay với "TÊN:"):

TÊN: <tên tour UPPERCASE ≤8 từ>
TAG: <tagline UPPERCASE ngắn>

NGÀY 1 | <tên ngày có địa danh ${dest}>
HH:MM | TYPE | tên activity | giá_VND | NCC | mô tả 1 câu tiếng Việt 15-25 từ
HH:MM | TYPE | tên activity | giá_VND | NCC | mô tả
(thêm dòng cho đến hết 4-5 activity của ngày)

NGÀY 2 | <tên ngày>
...

Input:
- Tour: ${request.route} ${request.days}N${request.nights}Đ
- ${totalPax} khách (${request.adults}NL+${request.children}TE)
- Ngân sách ${fmtVND(budgetPerDay)}/ngày cho cả đoàn
- Sở thích: ${request.preferences.join(', ') || 'tổng hợp'}

Rules:
- ĐÚNG ${request.days} ngày, mỗi ngày 4-5 activity sắp theo giờ
- TYPE ∈ TRANSPORT | SIGHTSEEING | MEAL | HOTEL | ACTIVITY (UPPERCASE)
- Mỗi field tách bằng " | " (space-pipe-space). KHÔNG dùng ký tự "|" trong tên/mô tả.
- Tên activity ≤50ch. Phải ĐÚNG NGHIỆP VỤ TOUR theo TYPE:
  · TRANSPORT: "Vé máy bay khứ hồi <route>" HOẶC "Xe limousine 16 chỗ <route>" HOẶC "Vé tàu SE giường nằm" — KHÔNG dùng "Xe máy bay" / "Máy bay xe" / từ ghép vô nghĩa khác
  · HOTEL: "Khách sạn <số sao> sao <khu vực>" (vd "Khách sạn 4 sao trung tâm biển")
  · MEAL: "<Loại bữa> tại <địa điểm/loại hình>" (vd "Bữa trưa hải sản địa phương", "Buffet sáng tại khách sạn")
  · SIGHTSEEING: tên ĐỊA DANH THẬT ở ${dest} (vd "Tháp Nhạn", "Eo Gió"); ĐỪNG bịa địa danh
  · ACTIVITY: "<Loại hoạt động>" (vd "Team Building bãi biển", "Gala Dinner âm nhạc")
- giá là số nguyên VND cho cả đoàn ${totalPax}, không dấu phẩy/dot
- supplier: nếu khớp 1 NCC trong "NCC ƯU TIÊN" → ghi ĐÚNG tên đó; KHÔNG khớp → ghi:
  · TRANSPORT → "Đối tác vận chuyển"
  · HOTEL → "Đối tác khách sạn"
  · MEAL → "Nhà hàng đối tác"
  · SIGHTSEEING → "Tự túc"
  · ACTIVITY → "Đơn vị tổ chức"
  (TUYỆT ĐỐI KHÔNG ghi literal "NCC" hay "TBD" — luôn dùng default tiếng Việt nghĩa hợp)
- Mô tả 1 câu Việt 15-25 từ, không thêm tên riêng mới
- TUYỆT ĐỐI KHÔNG bịa tên hotel/nhà hàng (vd "Bö Hing Hotel" = SAI)
${nccBlock}
Bắt đầu output ngay:`;

      console.log(`[Gen] Step ALL · mega-batch text format (${request.days} ngày)`);
      const tCall0 = Date.now();
      const rawAll = await window.tourkit.ai.completeStream(promptAll, (_delta, full) => {
        setGenStream(full);
      }, { model: pickModel('mega'), tag: 'mega', maxTokens: 8192, system: megaSystem, workflow: 'WizardTour' });
      console.log(`[Gen] Step ALL DONE in ${Date.now() - tCall0}ms, raw=${rawAll.length}ch`);

      const parsed = Parsers.parseTourText(rawAll, request.days);
      console.log(`[Gen] Parsed → ${parsed.days.length}/${request.days} ngày, ${parsed.days.reduce((s, d) => s + d.activities.length, 0)} activities`);

      if (parsed.days.length === 0) throw new Error('Parse text xong không có ngày nào');

      // Strip AI reasoning leak khỏi title/tagline (vd model trả "Tour HN... 5 từ. OK."
      // hoặc đoạn suy nghĩ dài) — chỉ giữ 1 dòng đầu, cap chiều dài, bỏ ngoặc kép bao.
      const cleanTitle = (s, max = 60) => {
        if (!s) return '';
        let t = String(s).split('\n')[0].trim();
        if ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith('"') && t.endsWith('"'))) t = t.slice(1, -1).trim();
        // bỏ chú thích kiểu "...(N từ)" hoặc "... 5 từ. OK." hoặc "Thêm X đầu:"
        t = t.replace(/\([^)]{0,30}\)/g, '').replace(/\b\d+\s*từ\.?\s*OK\.?$/i, '').trim();
        if (t.length > max) t = t.slice(0, max - 1).trim() + '…';
        return t;
      };
      const safeName = cleanTitle(parsed.name, 60) || `TOUR ${dest.toUpperCase()}`;
      const safeTag  = cleanTitle(parsed.tag, 80) || `HÀNH TRÌNH ${dest.toUpperCase()}`;

      setMarketing({
        tourName: safeName,
        tagline: safeTag,
        dayTitles: parsed.days.map((d, i) => cleanTitle(d.title, 70) || `Ngày ${i+1} - ${dest}`)
      });

      // Default supplier theo type khi AI trả "NCC" / "TBD" / rỗng (đỡ trơ + chuyên nghiệp)
      const SUPPLIER_DEFAULTS = {
        TRANSPORT: 'Đối tác vận chuyển',
        HOTEL:     'Đối tác khách sạn',
        MEAL:      'Nhà hàng đối tác',
        SIGHTSEEING: 'Tự túc',
        ACTIVITY:  'Đơn vị tổ chức',
        GUIDE:     'Tourkit Internal',
      };
      // costType mặc định theo TYPE — đồng bộ với defaultCostType ở Step 2 modal
      const COST_TYPE_DEFAULTS = {
        HOTEL: 'pax', MEAL: 'pax', TICKET: 'pax',
        TRANSPORT: 'shared', GUIDE: 'shared', SIGHTSEEING: 'shared',
        ACTIVITY: 'shared', ENTERTAINMENT: 'shared', TEAMBUILDING: 'shared',
      };
      // Fix title sai nghiệp vụ (model đôi khi vẫn slip qua rules)
      const fixTitle = (title, type) => {
        if (!title) return title;
        let t = title.trim();
        if (type === 'TRANSPORT') {
          // "Xe máy bay" → "Vé máy bay"; "Máy bay xe" → "Vé máy bay"
          t = t.replace(/^xe\s+máy\s+bay/i, 'Vé máy bay').replace(/máy\s+bay\s+xe/i, 'Vé máy bay');
        }
        return t;
      };
      const cleanSupplier = (s, type) => {
        const trimmed = String(s || '').trim();
        if (!trimmed || /^(ncc|tbd|n\/a|none|null|-)$/i.test(trimmed)) {
          return SUPPLIER_DEFAULTS[type] || 'Đối tác chiến lược';
        }
        return trimmed;
      };
      const itin = parsed.days.map((dayData, i) => ({
        day: i + 1,
        title: dayData.title || `Ngày ${i+1} - ${dest}`,
        activities: (dayData.activities || []).map((a, j) => {
          const type = a.y || 'ACTIVITY';
          return {
            id: `gen-${i}-${j}-${Date.now()}-${Math.random().toString(36).slice(2,6)}`,
            time: a.h || '09:00',
            type,
            title: fixTitle(a.n, type) || 'Hoạt động',
            description: a.d || '',
            cost: Number(a.c) || 0,
            supplier: cleanSupplier(a.s, type),
            costType: COST_TYPE_DEFAULTS[type] || 'shared',
          };
        })
      }));
      setItinerary(itin);
      setGenProgress(p => ({...p, daysDone: itin.length}));

      const meta = { name: safeName, tag: safeTag, titles: parsed.days.map((d, i) => cleanTitle(d.title, 70)) };

      // Auto-derive costing rows from itinerary
      const TYPE_MARKUP = { HOTEL: 15, TRANSPORT: 20, MEAL: 10, ACTIVITY: 25, SIGHTSEEING: 20, GUIDE: 15 };
      const TYPE_VAT = { HOTEL: 10, TRANSPORT: 8, MEAL: 8, ACTIVITY: 10, SIGHTSEEING: 0, GUIDE: 0 };
      // Đối chiếu supplier với NCC thật → verified = NCC nhà (giá hợp đồng) vs nguồn ngoài.
      const isRealNcc = (sup) => {
        if (!sup || sup === 'NCC' || sup === 'TBD') return false;
        const s = sup.toLowerCase();
        return nccNames.some(n => { const x = n.toLowerCase(); return x.includes(s) || s.includes(x); });
      };
      const derivedRows = [];
      itin.forEach((d, dIdx) => d.activities.forEach(a => {
        if (!a.cost) return;
        derivedRows.push({
          type: a.type,
          service: a.title.length > 30 ? a.title.slice(0,30)+'…' : a.title,
          supplier: a.supplier,
          qty: `Ngày ${d.day}`,
          priceNet: a.cost,
          vat: TYPE_VAT[a.type] || 8,
          markup: TYPE_MARKUP[a.type] || 15,
          verified: isRealNcc(a.supplier),
          costType: a.costType || 'shared',  // ← từ Step 2 → Step 3 split view tự động đúng
          dayIdx: dIdx,                       // ← Step 3 by-day view
        });
      }));
      derivedRows.push({type: 'GUIDE', service: 'Hướng dẫn viên', supplier: 'Tourkit Internal', qty: '1 HDV VIP', priceNet: 3000000, vat: 0, markup: 15, verified: true, costType: 'shared'});
      setRows(derivedRows);

      // Lưu nháp tour lên server (Redis theo công ty) — thay localStorage.
      const mkSave = {
        tourName: safeName,
        tagline: safeTag,
        dayTitles: parsed.days.map((d, i) => cleanTitle(d.title, 70) || `Ngày ${i+1} - ${dest}`)
      };
      saveTourToServer(itin, mkSave, derivedRows);

      if (!SKIP_CACHE) {
        const cachedMarketing = {
          tourName: meta.name || `TOUR ${dest.toUpperCase()}`,
          tagline: meta.tag || `HÀNH TRÌNH ${dest.toUpperCase()}`,
          dayTitles: (Array.isArray(meta.titles) && meta.titles.length === request.days)
            ? meta.titles : itin.map(d => d.title)
        };
        const saved = Storage.writeTourCache(request, {
          itinerary: itin, marketing: cachedMarketing, rows: derivedRows
        });
        if (saved) console.log(`[Gen] CACHE SAVED · key=${saved}`);
      }

      success = true;
    } catch (e) {
      console.error(`[Gen] ✗ FAIL after ${Date.now() - tGen0}ms:`, e);
      pushToast('AI lỗi: ' + (e.message || 'unknown').slice(0, 80) + '. Thử lại.', 'warn');
    } finally {
      setGenerating(false);
      setGenStream('');
      setGenProgress({stage: 'idle', daysTotal: 0, daysDone: 0});
      if (success) {
        console.log(`[Gen] ✓ END · total ${Date.now() - tGen0}ms`);
        setStep(2);
        pushToast('✨ Đã tạo tour ' + dest);
      }
    }
  };

  const handleQuoteGen = async () => {
    setStep(4);
    try {
      const prompt = `Tạo marketing copy cho tour ${request.route}, ${request.days} ngày, focus: ${request.preferences.join(', ')}. Days:
${itinerary.map(d => `Day ${d.day}: ${d.activities.map(a => a.title).join(', ')}`).join('\n')}

Output JSON THUẦN:
{
  "tourName": "TÊN TOUR UPPERCASE NGẮN",
  "tagline": "TAGLINE UPPERCASE EVOCATIVE",
  "dayTitles": ["Tên ngày 1", "Tên ngày 2", "Tên ngày 3"]
}

Tránh từ "tuyệt vời", "hoàn hảo", "đáng nhớ". Tiếng Việt tự nhiên.`;
      const raw = await window.tourkit.ai.complete(prompt, { model: pickModel('marketing'), workflow: 'WizardMarketing' });
      const m = raw.match(/\{[\s\S]*\}/);
      if (m) setMarketing(JSON.parse(m[0]));
    } catch (e) {
      console.warn('Marketing gen failed', e);
    }
  };

  // ─── NCC thật + nháp tour server (Redis) ─────────────────────────────────────
  async function loadNcc() {
    if (nccCatalog) return nccCatalog;
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/ncc/providers');
      if (!r.ok) { setNccCatalog([]); return []; }
      const data = await r.json();
      const arr = Array.isArray(data) ? data : (data.items || data.Items || []);
      const names = arr.map(p => p.name || p.Name).filter(Boolean);
      setNccCatalog(names);
      return names;
    } catch { setNccCatalog([]); return []; }
  }

  async function saveTourToServer(itin, mk, costRows) {
    try {
      const verified = costRows.filter(r => r.verified).length;
      const coverage = costRows.length ? Math.round(100 * verified / costRows.length) : 0;
      await window.tourkitAuth.authedFetch('/api/v1/tours', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ title: (mk && mk.tourName) || request.route, request, itinerary: itin, marketing: mk, rows: costRows, nccCoveragePct: coverage }),
      });
    } catch (e) { console.warn('[Gen] save tour failed', e); }
  }

  async function loadSavedTours() {
    try { const r = await window.tourkitAuth.authedFetch('/api/v1/tours'); if (r.ok) setSavedTours(await r.json()); } catch {}
  }
  async function deleteSavedTour(id) {
    try { await window.tourkitAuth.authedFetch('/api/v1/tours/' + encodeURIComponent(id), { method: 'DELETE' }); loadSavedTours(); pushToast('Đã xoá nháp tour'); } catch {}
  }
  function openSavedTour(t) {
    if (t.request) setRequest(t.request);
    if (t.itinerary) setItinerary(t.itinerary);
    if (t.rows) setRows(t.rows);
    if (t.marketing) setMarketing(t.marketing);
    setView('create'); setStep(4);
    pushToast('Đã mở nháp tour');
  }
  _uE(() => { if (view === 'list' || view === 'saved') loadSavedTours(); }, [view]);

  // ── Status derivation từ saved tour (nghiệp vụ thực tế):
  //   sent     → tour đã gửi khách (`sentAt` hoặc `status === 'sent'`)
  //   success  → khách đã chốt/thanh toán (`status === 'success'` hoặc `confirmed`)
  //   draft    → đang nháp (default)
  const tourStatus = (t) => {
    const s = (t.status || '').toLowerCase();
    if (s === 'success' || s === 'confirmed' || t.confirmedAt) return 'success';
    if (s === 'sent' || t.sentAt) return 'sent';
    return 'draft';
  };
  const statusLabel = { success: 'THÀNH CÔNG', sent: 'ĐÃ GỬI KHÁCH', draft: 'ĐANG NHẬP' };
  const statusClass = { success: 'ts-success', sent: 'ts-sent', draft: 'ts-draft' };

  // ── Filter + search list
  const visibleTours = savedTours.filter(t => {
    const st = tourStatus(t);
    if (listFilter !== 'all' && st !== listFilter) return false;
    if (listSearch) {
      const q = listSearch.toLowerCase();
      const hay = [t.id, t.title, t.request?.customerName, t.request?.route,
        t.marketing?.tourName].filter(Boolean).join(' ').toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
  const kpi = {
    total: savedTours.length,
    success: savedTours.filter(t => tourStatus(t) === 'success').length,
    sent: savedTours.filter(t => tourStatus(t) === 'sent').length,
    draft: savedTours.filter(t => tourStatus(t) === 'draft').length,
  };

  return (
    <>
      {view === 'list' ? (
        <main className="page wizard-list">
          {/* ── Hero header: title + 2 action buttons ─────────────────────── */}
          <div className="wl-hero">
            <div className="wl-hero-left">
              <div className="wl-hero-icon"><Icon name="paper" size={22} /></div>
              <div>
                <h1 className="wl-hero-title">BẢNG TÍNH GIÁ &amp; BÁO GIÁ TOUR DU LỊCH</h1>
                <div className="wl-hero-sub">XÂY DỰNG CHƯƠNG TRÌNH &amp; TÍNH GIÁ TOUR THÔNG MINH TÍCH HỢP AI</div>
              </div>
            </div>
            <div className="wl-hero-actions">
              <button className="wl-btn-outline" onClick={() => pushToast('Hướng dẫn sử dụng đang được biên soạn')}>
                <Icon name="book" size={14} /> HƯỚNG DẪN SỬ DỤNG
              </button>
              <button className="wl-btn-primary" onClick={() => { setView('create'); setStep(1); }}>
                <Icon name="plus" size={14} stroke={2.4} /> TẠO ĐƠN TÍNH GIÁ AI MỚI
              </button>
            </div>
          </div>

          {/* ── 4 KPI cards ──────────────────────────────────────────────── */}
          <div className="wl-kpi-grid">
            {[
              {k: 'all',     label: 'TỔNG BÁO GIÁ',  value: kpi.total,   icon: 'paper',       color: '#64748b'},
              {k: 'success', label: 'ĐÃ THÀNH CÔNG', value: kpi.success, icon: 'checkCircle', color: '#10b981'},
              {k: 'sent',    label: 'ĐÃ GỬI KHÁCH',  value: kpi.sent,    icon: 'share',       color: 'var(--primary)'},
              {k: 'draft',   label: 'ĐANG NHẬP',     value: kpi.draft,   icon: 'clock',       color: '#94a3b8'},
            ].map(c => (
              <button key={c.k} data-tone={c.k}
                className={'wl-kpi' + (listFilter === c.k ? ' on' : '')}
                onClick={() => setListFilter(c.k)}>
                <div>
                  <div className="wl-kpi-label">{c.label}</div>
                  <div className="wl-kpi-value">{c.value}<span className="wl-kpi-value-suffix">đơn</span></div>
                </div>
                <div className="wl-kpi-icon" style={{background: c.color + '14', color: c.color}}>
                  <Icon name={c.icon} size={18} />
                </div>
              </button>
            ))}
          </div>

          {/* ── Filter bar: search + pill filters ────────────────────────── */}
          <div className="wl-filter-bar">
            <div className="wl-search">
              <Icon name="search" size={14} />
              <input value={listSearch} onChange={e => setListSearch(e.target.value)}
                placeholder="Tìm mã đơn, khách hàng, điểm đến…" />
            </div>
            <div className="wl-pills">
              {[
                {k: 'all',     lbl: 'TẤT CẢ'},
                {k: 'success', lbl: 'THÀNH CÔNG'},
                {k: 'sent',    lbl: 'ĐÃ GỬI KHÁCH'},
                {k: 'draft',   lbl: 'ĐANG NHẬP'},
              ].map(p => (
                <button key={p.k} className={'wl-pill' + (listFilter === p.k ? ' on' : '')}
                  onClick={() => setListFilter(p.k)}>{p.lbl}</button>
              ))}
            </div>
          </div>

          {/* ── List: card (mobile) / bảng (desktop) ─────────────────────── */}
          {isMobile ? (
            <div className="wl-cards">
              {visibleTours.length === 0 ? (
                <div className="wl-empty" style={{background: 'white', border: '1px solid var(--border)', borderRadius: 12}}>
                  {savedTours.length === 0
                    ? 'Chưa có đơn báo giá nào. Bấm "TẠO ĐƠN TÍNH GIÁ AI MỚI" để bắt đầu.'
                    : 'Không có đơn khớp bộ lọc hiện tại.'}
                </div>
              ) : visibleTours.map(t => {
                const st = tourStatus(t);
                return (
                  <QuoteCard key={t.id} t={t}
                    statusLabel={statusLabel[st]} statusClass={statusClass[st]}
                    onOpen={() => openSavedTour(t)}
                    onDelete={async () => { if (await window.appConfirm('Xoá đơn này?', { title: 'Xoá nháp', confirmLabel: 'Xoá', danger: true })) deleteSavedTour(t.id); }} />
                );
              })}
            </div>
          ) : (
          <div className="wl-table-wrap">
            <table className="wl-table">
              <thead>
                <tr>
                  <th>MÃ ĐƠN</th>
                  <th>KHÁCH HÀNG</th>
                  <th>HÀNH TRÌNH</th>
                  <th>SỐ LƯỢNG</th>
                  <th className="num">TỔNG NET BÁO GIÁ</th>
                  <th className="num">GIÁ BÁN / KHÁCH</th>
                  <th>TRẠNG THÁI</th>
                  <th>THAO TÁC</th>
                </tr>
              </thead>
              <tbody>
                {visibleTours.length === 0 ? (
                  <tr><td colSpan={8} className="wl-empty">
                    {savedTours.length === 0
                      ? 'Chưa có đơn báo giá nào. Bấm "TẠO ĐƠN TÍNH GIÁ AI MỚI" để bắt đầu.'
                      : 'Không có đơn khớp bộ lọc hiện tại.'}
                  </td></tr>
                ) : visibleTours.map(t => {
                  const req = t.request || {};
                  const totalPax_ = (req.adults || 0) + (req.children || 0);
                  const totalNet = (t.rows || []).reduce((s, r) => s + (r.priceNet || 0), 0);
                  const salePerPax = t.salePerPax || (totalPax_ > 0 ? Math.round(totalNet * 1.2 / totalPax_) : 0);
                  const st = tourStatus(t);
                  const createdAt = t.createdAt ? new Date(t.createdAt) : null;
                  return (
                    <tr key={t.id} onClick={() => openSavedTour(t)} style={{cursor: 'pointer'}}>
                      <td><span className="wl-code">{t.code || t.id?.slice(-6).toUpperCase()}</span></td>
                      <td>
                        <div className="wl-cust">{req.customerName || t.title || '—'}</div>
                        <div className="wl-meta">
                          Người tạo: {t.createdBy || 'Hệ thống'}
                          {createdAt && ' · ' + createdAt.toLocaleDateString('vi-VN')}
                        </div>
                      </td>
                      <td>
                        <div>{req.route || '—'}</div>
                        <div className="wl-meta">{req.days}N{req.nights}Đ</div>
                      </td>
                      <td>
                        {req.adults || 0} Người lớn{req.children > 0 ? `, ${req.children} Trẻ em` : ''}
                      </td>
                      <td className="num"><strong>{fmtVND(totalNet)}</strong></td>
                      <td className="num"><span className="wl-good">{fmtVND(salePerPax)}</span></td>
                      <td><span className={'wl-status ' + statusClass[st]}>{statusLabel[st]}</span></td>
                      <td onClick={e => e.stopPropagation()}>
                        <button className="wl-action" title="Xem chi tiết" onClick={() => openSavedTour(t)}>
                          <Icon name="paper" size={14} />
                        </button>
                        <button className="wl-action wl-action-del" title="Xoá"
                          onClick={async () => { if (await window.appConfirm('Xoá đơn này?', { title: 'Xoá nháp', confirmLabel: 'Xoá', danger: true })) deleteSavedTour(t.id); }}>
                          <Icon name="trash" size={14} />
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          )}
        </main>
      ) : (
      <>
      {/* Back button khi đang trong wizard form */}
      <div className="wizard-tabs">
        <button className="wizard-tab on" onClick={() => setView('list')}>
          <Icon name="arrowLeft" size={14} /> Quay lại danh sách báo giá
        </button>
      </div>
      {/* Sub-header band: chứa wizard step indicator. */}
      <div className="wizard-stepbar">
        <div className="steps" data-screen-label={`0${step} ${WIZARD_STEPS[step-1].label}`}>
          {WIZARD_STEPS.map((s, i) => (
            <React.Fragment key={s.id}>
              <button
                className={`step ${s.id === step ? 'active' : ''} ${s.id < step ? 'done' : ''}`}
                onClick={() => setStep(s.id)}
                data-screen-label={`Step ${s.id}: ${s.label}`}>
                <span className="step-num">{s.id < step ? <Icon name="check" size={12} stroke={2.5} /> : s.id}</span>
                <span>{s.label}</span>
              </button>
              {i < WIZARD_STEPS.length - 1 && <span className="step-arrow"><Icon name="chevronRight" size={14} /></span>}
            </React.Fragment>
          ))}
        </div>
        {/* Draft/Commit indicator — visual warning rõ ràng khi unsaved + CTA nổi bật */}
        {draftStatus !== 'pristine' && (() => {
          const fmt = (iso) => { try { return new Date(iso).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }); } catch { return ''; } };
          const isUnsaved = draftStatus === 'dirty' || draftStatus === 'draft' || draftStatus === 'autosaving';
          const isCommitted = draftStatus === 'committed';
          return (
            <div className={'wiz-save-bar ' + (isUnsaved ? 'is-unsaved' : 'is-saved')}>
              {/* Status icon + label */}
              <div className="wiz-save-status">
                {draftStatus === 'dirty' && (<>
                  <span className="wiz-save-dot dot-warn" />
                  <div>
                    <div className="wiz-save-label">Đã sửa · đang chờ autosave</div>
                    <div className="wiz-save-hint">Sẽ tự lưu Redis sau ~1.5s</div>
                  </div>
                </>)}
                {draftStatus === 'autosaving' && (<>
                  <span className="wiz-save-spin" />
                  <div>
                    <div className="wiz-save-label">Đang autosave…</div>
                    <div className="wiz-save-hint">Lưu nháp vào Redis</div>
                  </div>
                </>)}
                {draftStatus === 'draft' && (<>
                  <span className="wiz-save-warn-icon">⚠</span>
                  <div>
                    <div className="wiz-save-label wiz-save-label-warn">CHƯA LƯU vào HỆ THỐNG</div>
                    <div className="wiz-save-hint">Đang ở Redis tạm ({fmt(lastDraftAt)}) · sẽ mất nếu hết 24h hoặc Redis restart</div>
                  </div>
                </>)}
                {isCommitted && (<>
                  <span className="wiz-save-dot dot-ok" />
                  <div>
                    <div className="wiz-save-label wiz-save-label-ok">✓ Đã lưu vào DB</div>
                    <div className="wiz-save-hint">An toàn · {fmt(lastCommitAt)}</div>
                  </div>
                </>)}
              </div>

              {/* Primary CTA — chỉ hiện khi unsaved */}
              {isUnsaved && (
                <button onClick={commitQuote} className="wiz-save-btn"
                  title="Lưu vĩnh viễn vào SQL Server — báo giá xuất hiện ở /quotes">
                  <Icon name="save" size={14} stroke={2.4} />
                  <span>LƯU NGAY VÀO HỆ THỐNG</span>
                </button>
              )}
            </div>
          );
        })()}
      </div>

      <main className="page" data-screen-label={`0${step} ${WIZARD_STEPS[step-1].label}`}>
        {step === 1 && <Step1Form
          request={request} setRequest={setRequest}
          onGenerate={handleGenerate} generating={generating}
          genStream={genStream} genProgress={genProgress}
          aiTone={tweaks.aiTone} pushToast={pushToast}
          hotelStars={hotelStars} setHotelStars={setHotelStars}
          paxRanges={paxRanges}   setPaxRanges={setPaxRanges}
          hotelOptions={hotelOptions} setHotelOptions={setHotelOptions} />}
        {step === 2 && <Step2Itinerary
          itinerary={itinerary} setItinerary={setItinerary} request={request}
          onNext={() => setStep(3)} onBack={() => setStep(1)} density={tweaks.density}
          pushToast={pushToast} />}
        {step === 3 && <Step3Costing
          rows={rows} setRows={setRows} request={request}
          onNext={handleQuoteGen} onBack={() => setStep(2)} pushToast={pushToast}
          hotelStars={hotelStars} setHotelStars={setHotelStars}
          paxRanges={paxRanges}   setPaxRanges={setPaxRanges}
          hotelOptions={hotelOptions}
          activeTier={activeTier} setActiveTier={setActiveTier}
          itinerary={itinerary} />}
        {step === 4 && <Step4Quote
          request={request} itinerary={itinerary} rows={rows} marketing={marketing}
          hotelOptions={hotelOptions} activeTier={activeTier}
          onBack={() => setStep(3)}
          onRestart={() => { setStep(1); pushToast('Bắt đầu tour mới'); }}
          pushToast={pushToast} />}
      </main>

      <window.ConfirmDialog open={confirmNew}
        title="Tạo tour mới?"
        eyebrow="HÀNH ĐỘNG CẦN XÁC NHẬN"
        message="Toàn bộ dữ liệu yêu cầu, lịch trình và bảng giá hiện tại sẽ được reset về form trống. Bạn có chắc chắn muốn bắt đầu một tour mới không?"
        confirmLabel="Tạo tour mới"
        cancelLabel="Tiếp tục tour hiện tại"
        onClose={() => setConfirmNew(false)}
        onConfirm={() => {
          setRequest({...window.DEMO_REQUEST, code: 'GRPS-' + Math.floor(1000 + Math.random() * 9000), route: '', adults: 0, children: 0, preferences: [], notes: ''});
          setItinerary(window.DEMO_ITINERARY);
          setRows(window.COSTING_ROWS);
          setStep(1);
          pushToast('Đã tạo tour mới');
        }} />
      </>
      )}
    </>
  );
}

window.WizardPage = WizardPage;
