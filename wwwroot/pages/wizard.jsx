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

function WizardPage({ pushToast, tweaks }) {
  const Storage = window.tourkitStorage;
  const Parsers = window.tourkitParsers;

  const [step, setStep] = _uS(1);
  const [view, setView] = _uS('create');         // 'create' | 'saved' — tab gộp "Tour đã lưu"
  const [savedTours, setSavedTours] = _uS([]);
  const [nccCatalog, setNccCatalog] = _uS(null);  // tên NCC thật (TourKit) để ưu tiên khi sinh tour
  const [request, setRequest]       = _uS(window.DEMO_REQUEST);
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
- Tên activity ≤50ch. SIGHTSEEING dùng địa danh THỰC ở ${dest}. Còn lại tên loại hình ("Khách sạn 4 sao trung tâm", "Nhà hàng hải sản địa phương")
- giá là số nguyên VND cho cả đoàn ${totalPax}, không dấu phẩy/dot
- supplier = ĐÚNG tên 1 NCC trong "NCC ƯU TIÊN" nếu khớp loại dịch vụ; KHÔNG khớp thì ghi "NCC"
- Mô tả 1 câu Việt 15-25 từ, không thêm tên riêng mới
- TUYỆT ĐỐI KHÔNG bịa tên hotel/nhà hàng (vd "Bö Hing Hotel" = SAI)
${nccBlock}
Bắt đầu output ngay:`;

      console.log(`[Gen] Step ALL · mega-batch text format (${request.days} ngày)`);
      const tCall0 = Date.now();
      const rawAll = await window.tourkit.ai.completeStream(promptAll, (_delta, full) => {
        setGenStream(full);
      }, { model: pickModel('mega'), tag: 'mega', maxTokens: 8192, system: megaSystem });
      console.log(`[Gen] Step ALL DONE in ${Date.now() - tCall0}ms, raw=${rawAll.length}ch`);

      const parsed = Parsers.parseTourText(rawAll, request.days);
      console.log(`[Gen] Parsed → ${parsed.days.length}/${request.days} ngày, ${parsed.days.reduce((s, d) => s + d.activities.length, 0)} activities`);

      if (parsed.days.length === 0) throw new Error('Parse text xong không có ngày nào');

      setMarketing({
        tourName: parsed.name || `TOUR ${dest.toUpperCase()}`,
        tagline: parsed.tag || `HÀNH TRÌNH ${dest.toUpperCase()}`,
        dayTitles: parsed.days.map((d, i) => d.title || `Ngày ${i+1} - ${dest}`)
      });

      const itin = parsed.days.map((dayData, i) => ({
        day: i + 1,
        title: dayData.title || `Ngày ${i+1} - ${dest}`,
        activities: (dayData.activities || []).map((a, j) => ({
          id: `gen-${i}-${j}-${Date.now()}-${Math.random().toString(36).slice(2,6)}`,
          time: a.h || '09:00',
          type: a.y || 'ACTIVITY',
          title: a.n || 'Hoạt động',
          description: a.d || '',
          cost: Number(a.c) || 0,
          supplier: a.s || ''
        }))
      }));
      setItinerary(itin);
      setGenProgress(p => ({...p, daysDone: itin.length}));

      const meta = { name: parsed.name, tag: parsed.tag, titles: parsed.days.map(d => d.title) };

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
      itin.forEach(d => d.activities.forEach(a => {
        if (!a.cost) return;
        derivedRows.push({
          type: a.type,
          service: a.title.length > 30 ? a.title.slice(0,30)+'…' : a.title,
          supplier: a.supplier || 'TBD',
          qty: `Ngày ${d.day}`,
          priceNet: a.cost,
          vat: TYPE_VAT[a.type] || 8,
          markup: TYPE_MARKUP[a.type] || 15,
          verified: isRealNcc(a.supplier)
        });
      }));
      derivedRows.push({type: 'GUIDE', service: 'Hướng dẫn viên', supplier: 'Tourkit Internal', qty: '1 HDV VIP', priceNet: 3000000, vat: 0, markup: 15, verified: true});
      setRows(derivedRows);

      // Lưu nháp tour lên server (Redis theo công ty) — thay localStorage.
      const mkSave = {
        tourName: parsed.name || `TOUR ${dest.toUpperCase()}`,
        tagline: parsed.tag || `HÀNH TRÌNH ${dest.toUpperCase()}`,
        dayTitles: parsed.days.map((d, i) => d.title || `Ngày ${i+1} - ${dest}`)
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
      const raw = await window.tourkit.ai.complete(prompt, { model: pickModel('marketing') });
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
  _uE(() => { if (view === 'saved') loadSavedTours(); }, [view]);

  return (
    <>
      {/* Tab gộp: Tạo tour | Tour đã lưu (server Redis) */}
      <div className="wizard-tabs">
        <button className={'wizard-tab' + (view === 'create' ? ' on' : '')} onClick={() => setView('create')}>
          <Icon name="sparkle" size={14} /> Tạo tour
        </button>
        <button className={'wizard-tab' + (view === 'saved' ? ' on' : '')} onClick={() => setView('saved')}>
          <Icon name="paper" size={14} /> Tour đã lưu
        </button>
      </div>

      {view === 'saved' ? (
        <main className="page">
          <div className="saved-tours">
            <div className="saved-tours-head">
              <h2>Tour đã lưu</h2>
              <span className="saved-tours-count">{savedTours.length} nháp · lưu trên hệ thống</span>
            </div>
            {savedTours.length === 0 ? (
              <div className="saved-empty">Chưa có nháp tour nào. Sang tab “Tạo tour” để tạo & lưu.</div>
            ) : (
              <div className="saved-grid">
                {savedTours.map(t => (
                  <div key={t.id} className="saved-card">
                    <div className="saved-card-body" onClick={() => openSavedTour(t)}>
                      <div className="saved-card-title">{(t.marketing && t.marketing.tourName) || t.title || 'Tour'}</div>
                      <div className="saved-card-meta">
                        {t.request && <span>{t.request.route} · {t.request.days}N{t.request.nights}Đ</span>}
                        {typeof t.nccCoveragePct === 'number' && <span className="saved-ncc">✓ {t.nccCoveragePct}% NCC nhà</span>}
                      </div>
                      <div className="saved-card-date">{t.createdAt ? new Date(t.createdAt).toLocaleString('vi-VN') : ''}{t.createdBy ? ' · ' + t.createdBy : ''}</div>
                    </div>
                    <button className="saved-del" title="Xoá" onClick={() => deleteSavedTour(t.id)}>✕</button>
                  </div>
                ))}
              </div>
            )}
          </div>
        </main>
      ) : (
      <>
      {/* Sub-header band: chứa wizard step indicator. Tách khỏi app-header để giữ App shell
          thuần nav, đồng thời tạo khoảng cách thoáng (padding-top) và center steps trên màn. */}
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
      </div>

      <main className="page" data-screen-label={`0${step} ${WIZARD_STEPS[step-1].label}`}>
        {step === 1 && <Step1Form
          request={request} setRequest={setRequest}
          onGenerate={handleGenerate} generating={generating}
          genStream={genStream} genProgress={genProgress}
          aiTone={tweaks.aiTone} pushToast={pushToast} />}
        {step === 2 && <Step2Itinerary
          itinerary={itinerary} setItinerary={setItinerary} request={request}
          onNext={() => setStep(3)} onBack={() => setStep(1)} density={tweaks.density} />}
        {step === 3 && <Step3Costing
          rows={rows} setRows={setRows} request={request}
          onNext={handleQuoteGen} onBack={() => setStep(2)} pushToast={pushToast} />}
        {step === 4 && <Step4Quote
          request={request} itinerary={itinerary} rows={rows} marketing={marketing}
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
