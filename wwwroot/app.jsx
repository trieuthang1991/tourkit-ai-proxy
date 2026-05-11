// Main app shell
const { useState: uS, useEffect: uE } = React;

const STEPS = [
  { id: 1, label: 'Yêu cầu khách hàng', icon: 'users' },
  { id: 2, label: 'AI Lập lịch trình', icon: 'list' },
  { id: 3, label: 'Bảng tính giá', icon: 'chart' },
  { id: 4, label: 'Xuất báo giá', icon: 'paper' }
];

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "theme": "orange",
  "aiTone": "Thân thiện, gọi Anh/Chị",
  "density": "comfortable",
  "demoLoaded": true
}/*EDITMODE-END*/;

function App() {
  const [t, set] = window.useTweaks(TWEAK_DEFAULTS);

  const [step, setStep] = uS(1);
  const [request, setRequest] = uS(window.DEMO_REQUEST);
  const [itinerary, setItinerary] = uS(window.DEMO_ITINERARY);
  const [rows, setRows] = uS(window.COSTING_ROWS);
  const [generating, setGenerating] = uS(false);
  const [genStream, setGenStream] = uS('');   // text đang stream từ Step A meta — show ở AIAssistantPanel
  const [genProgress, setGenProgress] = uS({stage: 'idle', daysTotal: 0, daysDone: 0});
  const [confirmNew, setConfirmNew] = uS(false);
  const [aiSettingsOpen, setAiSettingsOpen] = uS(false);
  const [aiCfg, setAiCfg] = uS(() => window.tourkit?.ai?.getConfig?.() || {provider: 'claude-builtin', model: 'deepseek-v4-flash'});
  const [marketing, setMarketing] = uS({
    tourName: 'TOUR DU LỊCH QUY NHƠN',
    tagline: 'HÀNH TRÌNH DI SẢN & BIỂN XANH',
    dayTitles: window.DEMO_ITINERARY.map(d => d.title)
  });
  const [toasts, setToasts] = uS([]);

  // Apply theme
  uE(() => {
    document.body.classList.toggle('editorial', t.theme === 'editorial');
    const density = t.density === 'compact' ? 0.8 : t.density === 'cozy' ? 0.9 : 1;
    document.documentElement.style.setProperty('--density', density);
  }, [t.theme, t.density]);

  const pushToast = (text, kind = 'success') => {
    const id = Date.now();
    setToasts(ts => [...ts, { id, text, kind }]);
    setTimeout(() => setToasts(ts => ts.filter(x => x.id !== id)), 3000);
  };

  const handleGenerate = async () => {
    setGenerating(true);
    setGenStream('');
    setGenProgress({stage: 'meta', daysTotal: request.days, daysDone: 0});
    const dest = request.route.split('-').pop().trim() || request.route;
    let success = false;
    try {
      const totalPax = request.adults + request.children;
      const budgetPerDay = Math.round((request.budgetPerPax * totalPax) / request.days);

      // Helper: parse JSON with aggressive repair for truncated responses
      const parseJSON = (raw) => {
        const cleaned = raw.replace(/```json\s*/gi, '').replace(/```\s*/g, '').trim();
        const start = cleaned.indexOf('{');
        if (start < 0) throw new Error('Không có JSON: ' + raw.slice(0, 80));
        let text = cleaned.slice(start);
        // Normalize quotes
        text = text.replace(/[""]/g, '"').replace(/['']/g, "'");
        // Try direct first
        try { return JSON.parse(text); } catch (e) {}
        // Remove trailing comma
        try { return JSON.parse(text.replace(/,\s*([}\]])/g, '$1')); } catch (e) {}
        // Aggressive: find last complete object in array, truncate there, close brackets
        // Walk balanced braces to find truncation point
        let depth = 0, inStr = false, esc = false, lastValidEnd = -1;
        for (let i = 0; i < text.length; i++) {
          const ch = text[i];
          if (esc) { esc = false; continue; }
          if (ch === '\\') { esc = true; continue; }
          if (ch === '"') { inStr = !inStr; continue; }
          if (inStr) continue;
          if (ch === '{' || ch === '[') depth++;
          else if (ch === '}' || ch === ']') { depth--; if (depth === 0) lastValidEnd = i; }
        }
        // If truncated mid-array, find last complete element
        // Find last "}," at depth 2 (inside array of objects), cut there, close array and outer
        depth = 0; inStr = false; esc = false;
        let lastObjEnd = -1, arrayDepth = -1;
        for (let i = 0; i < text.length; i++) {
          const ch = text[i];
          if (esc) { esc = false; continue; }
          if (ch === '\\') { esc = true; continue; }
          if (ch === '"') { inStr = !inStr; continue; }
          if (inStr) continue;
          if (ch === '[') { if (arrayDepth < 0) arrayDepth = depth; depth++; }
          else if (ch === '{') depth++;
          else if (ch === ']' || ch === '}') {
            depth--;
            if (ch === '}' && depth === arrayDepth + 1) lastObjEnd = i;
          }
        }
        if (lastObjEnd > 0 && arrayDepth >= 0) {
          // Truncate after last complete object, then close array and any outer braces
          let repaired = text.slice(0, lastObjEnd + 1);
          // Close remaining open brackets in order
          depth = 0; inStr = false; esc = false;
          const stack = [];
          for (let i = 0; i < repaired.length; i++) {
            const ch = repaired[i];
            if (esc) { esc = false; continue; }
            if (ch === '\\') { esc = true; continue; }
            if (ch === '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch === '{') stack.push('}');
            else if (ch === '[') stack.push(']');
            else if (ch === '}' || ch === ']') stack.pop();
          }
          while (stack.length) repaired += stack.pop();
          try { return JSON.parse(repaired); } catch (e) {
            throw new Error('Parse fail sau repair: ' + e.message);
          }
        }
        throw new Error('Không parse được JSON cụt');
      };

      // Step A: generate tour name + day titles (1 small call, STREAMING để UX responsive)
      const promptMeta = `Tour ${request.route}, ${request.days}N${request.nights}Đ, focus: ${request.preferences.join(', ') || 'tổng hợp'}. Trả JSON thuần (no markdown):
{"name":"TÊN TOUR NGẮN UPPERCASE","tag":"TAGLINE UPPERCASE","titles":["Ngày 1: ...","Ngày 2: ...","Ngày ${request.days}: ..."]}
Mỗi title có địa danh thật ở ${dest}. titles có đúng ${request.days} phần tử.`;
      const rawMeta = await window.tourkit.ai.completeStream(promptMeta, (_delta, full) => {
        setGenStream(full);
      });
      console.log('[Meta raw]', rawMeta);
      const meta = parseJSON(rawMeta);
      setGenProgress(p => ({...p, stage: 'days'}));

      // Step B: generate mỗi ngày song song (Promise.all) — cắt N×latency → 1×latency
      const dayPromises = Array.from({length: request.days}, async (_, i) => {
        const dayTitle = (meta.titles && meta.titles[i]) || `Ngày ${i+1} - ${dest}`;
        const promptDay = `Ngày ${i+1} tour ${request.route} (${dayTitle}), ${totalPax} khách, ngân sách ${fmtVND(budgetPerDay)}.

CHỈ trả JSON thuần (no markdown, no description):
{"a":[
{"h":"08:00","y":"TRANSPORT","n":"Xe đón đi ${dest}","c":6000000,"s":"NCC"},
{"h":"10:00","y":"SIGHTSEEING","n":"Tham quan X","c":2000000,"s":"NCC"}
]}

- 4-5 activity, sắp xếp theo giờ
- y ∈ TRANSPORT|SIGHTSEEING|MEAL|HOTEL|ACTIVITY
- n ngắn gọn (max 50 ký tự), địa danh thật ở ${dest}
- c là VND cho cả đoàn ${totalPax} khách
- KHÔNG có field d (description)`;
        const rawDay = await window.claude.complete(promptDay);
        console.log(`[Day ${i+1} raw]`, rawDay);
        const dayData = parseJSON(rawDay);
        setGenProgress(p => ({...p, daysDone: p.daysDone + 1}));
        return {
          day: i + 1,
          title: dayTitle,
          activities: (dayData.a || []).map((a, j) => ({
            id: `gen-${i}-${j}-${Date.now()}`,
            time: a.h || '09:00',
            type: a.y || 'ACTIVITY',
            title: a.n || 'Hoạt động',
            description: a.d || '',
            cost: Number(a.c) || 0,
            supplier: a.s || ''
          }))
        };
      });
      const itin = await Promise.all(dayPromises);
      if (itin.length === 0) throw new Error('Lịch trình rỗng');

      // Set itinerary FIRST so user sees activities immediately
      setItinerary(itin);
      setMarketing({
        tourName: meta.name || `TOUR ${dest.toUpperCase()}`,
        tagline: meta.tag || `HÀNH TRÌNH ${dest.toUpperCase()}`,
        dayTitles: itin.map(d => d.title)
      });

      // Step C: enrich từng ngày song song (fail-safe per day) — fire-and-forget
      (async () => {
        await Promise.all(itin.map(async (day, i) => {
          try {
            const acts = day.activities.map((a, j) => `${j+1}. ${a.time} ${a.title}`).join('\n');
            const promptDesc = `Tour ${dest}, ngày ${i+1}. Viết mô tả ngắn (1 câu, 15-25 từ, tiếng Việt tự nhiên) cho từng activity:
${acts}

CHỈ trả JSON thuần:
{"d":["Mô tả 1","Mô tả 2","Mô tả 3"]}

- Đúng ${day.activities.length} phần tử trong d
- Mỗi mô tả gợi cảm xúc, có chi tiết cụ thể về ${dest}
- Không lặp lại tên activity`;
            const rawDesc = await window.claude.complete(promptDesc);
            const descData = parseJSON(rawDesc);
            if (Array.isArray(descData.d)) {
              setItinerary(prev => prev.map((d, di) => di !== i ? d : {
                ...d,
                activities: d.activities.map((a, ai) => ({
                  ...a,
                  description: descData.d[ai] || a.description
                }))
              }));
            }
          } catch (e) {
            console.warn(`Description enrichment day ${i+1} failed:`, e);
          }
        }));
        pushToast('📝 Đã bổ sung mô tả chi tiết');
      })();

      // Auto-derive costing rows from itinerary
      const TYPE_MARKUP = { HOTEL: 15, TRANSPORT: 20, MEAL: 10, ACTIVITY: 25, SIGHTSEEING: 20, GUIDE: 15 };
      const TYPE_VAT = { HOTEL: 10, TRANSPORT: 8, MEAL: 8, ACTIVITY: 10, SIGHTSEEING: 0, GUIDE: 0 };
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
          verified: !!a.supplier
        });
      }));
      derivedRows.push({type: 'GUIDE', service: 'Hướng dẫn viên', supplier: 'Tourkit Internal', qty: '1 HDV VIP', priceNet: 3000000, vat: 0, markup: 15, verified: true});
      setRows(derivedRows);

      success = true;
    } catch (e) {
      console.error('Itinerary generation failed:', e);
      pushToast('AI lỗi: ' + (e.message || 'unknown').slice(0, 80) + '. Thử lại.', 'warn');
    } finally {
      setGenerating(false);
      setGenStream('');
      setGenProgress({stage: 'idle', daysTotal: 0, daysDone: 0});
      if (success) {
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
      const raw = await window.claude.complete(prompt);
      const m = raw.match(/\{[\s\S]*\}/);
      if (m) setMarketing(JSON.parse(m[0]));
    } catch (e) {
      console.warn('Marketing gen failed', e);
    }
  };

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-title-row">
          <div className="app-brand">
            <div className="app-logo">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 2L3 7v6c0 5 4 9 9 9s9-4 9-9V7l-9-5z" />
                <path d="M12 8v8M8 12h8" />
              </svg>
            </div>
            <div>
              <h1 className="app-title">AI TOUR OPERATION & QUOTATION</h1>
              <p className="app-tagline">Xây dựng chương trình & tính giá tour thông minh · Tourkit v1.0</p>
            </div>
          </div>
          <div className="app-actions">
            <button className="btn btn-ghost btn-sm" onClick={() => setAiSettingsOpen(true)} title={`AI: ${aiCfg.provider === 'opencode-go' ? aiCfg.model : 'Claude built-in'}`}>
              <Icon name="sparkle" size={14} /> AI: {aiCfg.provider === 'opencode-go' ? aiCfg.model : 'Claude'}
            </button>
            <button className="btn btn-ghost btn-sm" onClick={() => setConfirmNew(true)}><Icon name="refresh" size={14} /> Tour mới</button>
            <div className="user-chip">
              <div className="user-avatar">AH</div>
              <span>Anh Hùng · Sales</span>
            </div>
          </div>
        </div>

        <div className="steps" data-screen-label={`0${step} ${STEPS[step-1].label}`}>
          {STEPS.map((s, i) => (
            <React.Fragment key={s.id}>
              <button
                className={`step ${s.id === step ? 'active' : ''} ${s.id < step ? 'done' : ''}`}
                onClick={() => setStep(s.id)}
                data-screen-label={`Step ${s.id}: ${s.label}`}>
                <span className="step-num">{s.id < step ? <Icon name="check" size={12} stroke={2.5} /> : s.id}</span>
                <span>{s.label}</span>
              </button>
              {i < STEPS.length - 1 && <span className="step-arrow"><Icon name="chevronRight" size={14} /></span>}
            </React.Fragment>
          ))}
        </div>
      </header>

      <main className="page" data-screen-label={`0${step} ${STEPS[step-1].label}`}>
        {step === 1 && (
          <Step1Form
            request={request}
            setRequest={setRequest}
            onGenerate={handleGenerate}
            generating={generating}
            genStream={genStream}
            genProgress={genProgress}
            aiTone={t.aiTone}
          />
        )}
        {step === 2 && (
          <Step2Itinerary
            itinerary={itinerary}
            setItinerary={setItinerary}
            request={request}
            onNext={() => setStep(3)}
            onBack={() => setStep(1)}
            density={t.density}
          />
        )}
        {step === 3 && (
          <Step3Costing
            rows={rows}
            setRows={setRows}
            request={request}
            onNext={handleQuoteGen}
            onBack={() => setStep(2)}
            pushToast={pushToast}
          />
        )}
        {step === 4 && (
          <Step4Quote
            request={request}
            itinerary={itinerary}
            rows={rows}
            marketing={marketing}
            onBack={() => setStep(3)}
            onRestart={() => { setStep(1); pushToast('Bắt đầu tour mới'); }}
            pushToast={pushToast}
          />
        )}
      </main>

      <div className="toast-container">
        {toasts.map(t => (
          <div key={t.id} className={`toast ${t.kind}`}>
            <Icon name="check" size={14} stroke={2.5} /> {t.text}
          </div>
        ))}
      </div>

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
        }}
      />

      {window.AISettingsDialog && <window.AISettingsDialog
        open={aiSettingsOpen}
        onClose={() => setAiSettingsOpen(false)}
        onSaved={(cfg) => {
          setAiCfg(cfg);
          pushToast(`AI: ${cfg.provider === 'opencode-go' ? cfg.model : 'Claude built-in'}`);
        }}
      />}

      {window.TweaksPanel && (
        <window.TweaksPanel title="Tweaks">
          <window.TweakSection label="Visual">
            <window.TweakRadio label="Color" value={t.theme} options={[
              { value: 'orange', label: 'Orange' },
              { value: 'editorial', label: 'Editorial' }
            ]} onChange={v => set('theme', v)} />
            <window.TweakRadio label="Density" value={t.density} options={[
              { value: 'compact', label: 'Compact' },
              { value: 'cozy', label: 'Cozy' },
              { value: 'comfortable', label: 'Roomy' }
            ]} onChange={v => set('density', v)} />
          </window.TweakSection>
          <window.TweakSection label="AI Behavior">
            <window.TweakSelect label="Tông giọng" value={t.aiTone} options={[
              { value: 'Thân thiện, gọi Anh/Chị', label: 'Thân thiện' },
              { value: 'Chuyên nghiệp, ngắn gọn', label: 'Chuyên nghiệp' },
              { value: 'Sales hăng hái, đầy nhiệt huyết', label: 'Sales hăng hái' }
            ]} onChange={v => set('aiTone', v)} />
          </window.TweakSection>
          <window.TweakSection label="Demo data">
            <window.TweakButton label="Reset Quy Nhơn demo" onClick={() => {
              setRequest(window.DEMO_REQUEST);
              setItinerary(window.DEMO_ITINERARY);
              setRows(window.COSTING_ROWS);
              pushToast('Đã reset về demo Quy Nhơn');
            }} />
            <window.TweakButton label="Xóa form (empty state)" secondary onClick={() => {
              setRequest(r => ({...r, route: '', adults: 0, children: 0, preferences: [], notes: ''}));
              pushToast('Form đã xóa', 'warn');
            }} />
          </window.TweakSection>
        </window.TweaksPanel>
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
