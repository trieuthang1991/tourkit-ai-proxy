// pages/visa.jsx — Thẩm định Visa AI ("Tỉ lệ đậu Visa").
// Product UI nội bộ, bám design system warm-orange của app (KHÔNG phải landing page).
// Luồng 3 bước: upload hồ sơ → AI vision đọc → NV rà/sửa → chấm tỉ lệ đậu/rớt.

const { useState: _vS, useEffect: _vE, useRef: _vR } = React;

const VISA_LEVELS = {
  cao:        { label: 'Khả năng cao', color: '#16a34a', bg: '#dcfce7' },
  trung_binh: { label: 'Trung bình',   color: '#d97706', bg: '#fef3c7' },
  thap:       { label: 'Rủi ro cao',   color: '#dc2626', bg: '#fee2e2' },
};
const visaLevel = (lv) => VISA_LEVELS[lv] || VISA_LEVELS.trung_binh;

// Thanh bước: Tải hồ sơ → Rà soát → Kết quả.
function VisaSteps({ step }) {
  const steps = ['Tải hồ sơ', 'Rà soát', 'Kết quả'];
  const idx = { upload: 0, extracting: 0, review: 1, scoring: 1, result: 2 }[step] ?? 0;
  return (
    <div className="visa-steps">
      {steps.map((s, i) => (
        <React.Fragment key={s}>
          <div className={'visa-step' + (i === idx ? ' on' : i < idx ? ' done' : '')}>
            <span className="visa-step-dot">{i < idx ? <Icon name="check" size={12} stroke={3} /> : i + 1}</span>
            <span className="visa-step-lbl">{s}</span>
          </div>
          {i < steps.length - 1 && <div className={'visa-step-line' + (i < idx ? ' done' : '')} />}
        </React.Fragment>
      ))}
    </div>
  );
}

// Vòng cung % đậu (data-viz SVG — không phải icon).
function PassGauge({ rate, level }) {
  const lv = visaLevel(level);
  const c = Math.PI * 70;
  const pct = Math.max(0, Math.min(100, rate)) / 100;
  return (
    <div className="visa-gauge">
      <svg viewBox="0 0 180 100" width="180" height="100">
        <path d="M 20 95 A 70 70 0 0 1 160 95" fill="none" stroke="#eef0f3" strokeWidth="14" strokeLinecap="round" />
        <path d="M 20 95 A 70 70 0 0 1 160 95" fill="none" stroke={lv.color} strokeWidth="14" strokeLinecap="round"
          strokeDasharray={c} strokeDashoffset={c * (1 - pct)} style={{ transition: 'stroke-dashoffset .8s cubic-bezier(0.16,1,0.3,1)' }} />
      </svg>
      <div className="visa-gauge-num" style={{ color: lv.color }}>{rate}<span>%</span></div>
      <div className="visa-gauge-lbl">khả năng đậu</div>
    </div>
  );
}

// Loader trong panel (shimmer hàng giấy tờ) — dùng khi AI đang đọc / chấm.
function VisaProcessing({ title, sub, rows = 3 }) {
  return (
    <div className="visa-processing">
      <div className="visa-proc-head">
        <span className="visa-proc-spin" /> <b>{title}</b>
      </div>
      <p className="visa-proc-sub">{sub}</p>
      <div className="visa-proc-rows">
        {Array.from({ length: rows }).map((_, i) => <div className="visa-sk" key={i} style={{ width: `${90 - i * 12}%` }} />)}
      </div>
    </div>
  );
}

// ─── Bước 1: Upload ───────────────────────────────────────────────────────────
function VisaUploader({ onExtracted, onBusy, busy, pushToast }) {
  const [files, setFiles] = _vS([]);
  const [name, setName] = _vS('');
  const inputRef = _vR(null);

  const cfg = window.tourkit.ai.getConfig();
  const visionOk = cfg.provider === 'openai' || cfg.provider === 'anthropic';

  function addFiles(list) {
    const arr = Array.from(list).filter(f => /^image\//.test(f.type));
    const rejected = Array.from(list).length - arr.length;
    if (rejected > 0) pushToast(`${rejected} file bị bỏ qua (chỉ nhận ảnh JPG/PNG)`, 'error');
    setFiles(prev => [...prev, ...arr].slice(0, 10));
  }
  const onPick = (e) => { addFiles(e.target.files); e.target.value = ''; };
  const onDrop = (e) => { e.preventDefault(); addFiles(e.dataTransfer.files); };
  const removeAt = (i) => setFiles(fs => fs.filter((_, idx) => idx !== i));

  async function submit() {
    if (files.length === 0) { pushToast('Chọn ít nhất 1 ảnh hồ sơ', 'error'); return; }
    if (!visionOk) { pushToast('Cần chọn ChatGPT/Claude trong Cấu hình AI (góc trên) để đọc ảnh', 'error'); return; }
    onBusy(true);
    try {
      const fd = new FormData();
      files.forEach(f => fd.append('files', f));
      if (name.trim()) fd.append('applicantName', name.trim());
      fd.append('provider', cfg.provider);
      fd.append('model', cfg.model || '');
      const key = window.tourkit.ai.getKey(cfg.provider);
      if (key) fd.append('apiKey', key);

      const r = await window.tourkitAuth.authedFetch('/api/v1/visa/assess', { method: 'POST', body: fd });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Đọc hồ sơ lỗi', 'error'); return; }
      pushToast('AI đã đọc xong hồ sơ');
      onExtracted(data);
    } catch (e) { pushToast('Lỗi: ' + e.message, 'error'); }
    finally { onBusy(false); }
  }

  if (busy)
    return <VisaProcessing title={`AI đang đọc ${files.length} giấy tờ…`}
      sub="Nhận diện loại giấy tờ và trích thông tin tài chính, ràng buộc. Quá trình này mất vài giây mỗi ảnh." rows={Math.min(files.length, 4)} />;

  return (
    <div>
      <div className="visa-panel-head">
        <h2>Hồ sơ mới</h2>
        <p>Tải ảnh giấy tờ của 1 đương đơn (hộ chiếu, sao kê ngân hàng, hợp đồng lao động, sổ đỏ…). AI sẽ đọc và chấm tỉ lệ đậu.</p>
      </div>

      {!visionOk && (
        <div className="visa-warn">
          <Icon name="warning" size={15} />
          <span>Tính năng đọc ảnh cần <b>ChatGPT (OpenAI)</b> hoặc <b>Claude</b>. Mở <b>“AI: …”</b> ở góc trên phải để chọn và nhập API key.</span>
        </div>
      )}

      <label className="visa-lbl">Tên đương đơn <span className="visa-opt">(tuỳ chọn)</span></label>
      <input type="text" className="visa-field" placeholder="AI tự đọc từ hộ chiếu nếu để trống"
        value={name} onChange={e => setName(e.target.value)} />

      <div className="visa-drop" onClick={() => inputRef.current?.click()}
        onDragOver={e => e.preventDefault()} onDrop={onDrop}>
        <input ref={inputRef} type="file" accept="image/*" multiple hidden onChange={onPick} />
        <div className="visa-drop-icon"><Icon name="paper" size={24} /></div>
        <div className="visa-drop-main">Kéo thả ảnh vào đây hoặc bấm để chọn</div>
        <div className="visa-drop-sub">JPG / PNG / WEBP · tối đa 10 file · ≤10MB mỗi file</div>
      </div>

      {files.length > 0 && (
        <div className="visa-thumbs">
          {files.map((f, i) => (
            <div className="visa-thumb" key={i}>
              <img src={URL.createObjectURL(f)} alt={f.name} />
              <button className="visa-thumb-x" onClick={() => removeAt(i)} title="Bỏ"><Icon name="close" size={12} stroke={2.5} /></button>
              <div className="visa-thumb-name">{f.name}</div>
            </div>
          ))}
        </div>
      )}

      <button className="visa-btn primary block" disabled={files.length === 0} onClick={submit}>
        <Icon name="sparkle" size={15} /> Đọc hồ sơ{files.length > 0 ? ` (${files.length} file)` : ''}
      </button>
    </div>
  );
}

// ─── Bước 2: Review hồ sơ AI đọc + chấm điểm ──────────────────────────────────
function VisaReview({ assessment, onScored, onBusy, busy, pushToast }) {
  const [profile, setProfile] = _vS(assessment.extraction?.profile || '');

  async function score() {
    onBusy(true);
    try {
      const cfg = window.tourkit.ai.getConfig();
      const key = window.tourkit.ai.getKey(cfg.provider);
      const r = await window.tourkitAuth.authedFetch(`/api/v1/visa/assess/${assessment.id}/score`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profile, provider: cfg.provider, model: cfg.model, apiKey: key || undefined }),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Chấm điểm lỗi', 'error'); return; }
      onScored(data);
    } catch (e) { pushToast('Lỗi: ' + e.message, 'error'); }
    finally { onBusy(false); }
  }

  if (busy)
    return <VisaProcessing title="AI đang thẩm định hồ sơ…"
      sub="Đối chiếu chứng minh tài chính, ràng buộc về nước và lịch sử du lịch để ước lượng khả năng đậu." rows={3} />;

  const files = assessment.extraction?.files || [];
  return (
    <div>
      <div className="visa-panel-head">
        <h2>{assessment.applicantName}{assessment.country ? ` · ${assessment.country}` : ''}</h2>
        <p>AI đã đọc {assessment.fileCount} giấy tờ. Rà soát / chỉnh sửa nội dung dưới đây nếu cần, rồi chấm điểm.</p>
      </div>

      <div className="visa-files">
        {files.map((f, i) => (
          <div className={'visa-doc' + (f.readable ? '' : ' bad')} key={i}>
            <div className="visa-doc-type">{f.docTypeLabel}</div>
            <div className="visa-doc-sum">{f.readable ? (f.summary || '—') : (f.note || 'Không đọc được')}</div>
          </div>
        ))}
      </div>

      <label className="visa-lbl">Hồ sơ tổng hợp <span className="visa-opt">(sửa được trước khi chấm)</span></label>
      <textarea className="visa-area" rows={8} value={profile} onChange={e => setProfile(e.target.value)} />

      <button className="visa-btn primary block" onClick={score}>
        <Icon name="shield" size={15} /> Chấm tỉ lệ đậu
      </button>
    </div>
  );
}

// ─── Kết quả ──────────────────────────────────────────────────────────────────
const VISA_BLOCKS = [
  { key: 'strengths',   title: 'Điểm mạnh',              icon: 'checkCircle', tone: 'good' },
  { key: 'weaknesses',  title: 'Điểm yếu / rủi ro',      icon: 'warning',     tone: 'warn' },
  { key: 'missingDocs', title: 'Giấy tờ cần bổ sung',    icon: 'paper',       tone: 'info' },
  { key: 'suggestions', title: 'Đề xuất tăng tỉ lệ đậu', icon: 'zap',         tone: 'tip'  },
];
function VisaResultView({ assessment, onNew }) {
  const res = assessment.result;
  if (!res) return null;
  const lv = visaLevel(res.level);
  return (
    <div>
      <div className="visa-result-top">
        <PassGauge rate={res.passRate} level={res.level} />
        <div className="visa-result-meta">
          <div className="visa-badge" style={{ color: lv.color, background: lv.bg }}>{lv.label}</div>
          <h2>{assessment.applicantName}{assessment.country ? ` · ${assessment.country}` : ''}</h2>
          <p>{res.summary}</p>
        </div>
      </div>

      <div className="visa-blocks">
        {VISA_BLOCKS.map(b => {
          const items = res[b.key];
          if (!items || items.length === 0) return null;
          return (
            <div className={'visa-block ' + b.tone} key={b.key}>
              <div className="visa-block-head"><Icon name={b.icon} size={15} /> {b.title}</div>
              <ul>{items.map((x, i) => <li key={i}>{x}</li>)}</ul>
            </div>
          );
        })}
      </div>
      <div className="visa-foot-note">AI ước lượng tham khảo dựa trên hồ sơ. Quyết định cuối thuộc lãnh sự quán.</div>
    </div>
  );
}

// ─── History rail ─────────────────────────────────────────────────────────────
function VisaHistory({ items, activeId, onPick, onNew, onDelete }) {
  return (
    <aside className="visa-rail">
      <button className="visa-btn primary block" onClick={onNew}><Icon name="plus" size={15} stroke={2.5} /> Hồ sơ mới</button>
      <div className="visa-rail-title">Lịch sử ({items.length})</div>
      <div className="visa-rail-list">
        {items.length === 0 && (
          <div className="visa-rail-empty">
            <Icon name="shield" size={22} /><span>Chưa có hồ sơ nào.<br />Tải hồ sơ đầu tiên để bắt đầu.</span>
          </div>
        )}
        {items.map(a => {
          const lv = a.result ? visaLevel(a.result.level) : null;
          return (
            <div key={a.id} className={'visa-rail-item' + (a.id === activeId ? ' on' : '')} onClick={() => onPick(a)}>
              <div className="visa-rail-row">
                <span className="visa-rail-name">{a.applicantName}</span>
                {a.result
                  ? <span className="visa-rail-rate" style={{ color: lv.color }}>{a.result.passRate}%</span>
                  : <span className="visa-rail-pending">chưa chấm</span>}
              </div>
              <div className="visa-rail-sub">
                <span>{a.country || 'Chưa rõ nước'} · {a.fileCount} giấy tờ</span>
                <button className="visa-rail-del" title="Xóa"
                  onClick={(e) => { e.stopPropagation(); onDelete(a.id); }}><Icon name="trash" size={13} /></button>
              </div>
            </div>
          );
        })}
      </div>
    </aside>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────
function VisaPage({ pushToast }) {
  const [history, setHistory] = _vS([]);
  const [current, setCurrent] = _vS(null);
  const [view, setView] = _vS('upload');     // upload | review | result
  const [busy, setBusy] = _vS(false);

  const loadHistory = async () => {
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/visa/assessments');
      if (r.ok) setHistory(await r.json());
    } catch { /* ignore */ }
  };
  _vE(() => { loadHistory(); }, []);

  const goNew = () => { setCurrent(null); setView('upload'); setBusy(false); };
  const onExtracted = (a) => { setCurrent(a); setView('review'); loadHistory(); };
  const onScored = (a) => { setCurrent(a); setView('result'); loadHistory(); };
  const onPick = (a) => { setCurrent(a); setView(a.result ? 'result' : 'review'); setBusy(false); };

  const onDelete = async (id) => {
    if (!window.confirm('Xóa hồ sơ thẩm định này?')) return;
    try {
      const r = await window.tourkitAuth.authedFetch(`/api/v1/visa/assessments/${id}`, { method: 'DELETE' });
      if (r.ok) { pushToast('Đã xóa'); if (current?.id === id) goNew(); loadHistory(); }
    } catch (e) { pushToast('Xóa lỗi: ' + e.message, 'error'); }
  };

  const stepState = busy ? (view === 'upload' ? 'extracting' : 'scoring') : view;

  const scored = history.filter(a => a.result).length;
  return (
    <main className="page" style={{padding: '18px 28px 60px', maxWidth: 1500, margin: '0 auto'}}>
      <window.PageShell.PageHero
        icon="shield"
        title="Thẩm định Visa AI"
        badge="vision"
        sub="Upload hồ sơ → AI vision đọc → chấm tỉ lệ đậu/rớt + đề xuất bổ sung."
        status={{ label: history.length > 0 ? `${history.length} HỒ SƠ` : 'CHƯA CÓ HỒ SƠ',
          detail: scored > 0 ? `${scored} đã chấm` : 'Tải hồ sơ đầu tiên',
          tone: history.length > 0 ? 'live' : 'idle' }}
        actions={<button className="visa-btn primary" onClick={goNew}><Icon name="plus" size={15} stroke={2.5} /> Hồ sơ mới</button>}
      />
      <div className="visa">
      <VisaHistory items={history} activeId={current?.id} onPick={onPick} onNew={goNew} onDelete={onDelete} />
      <section className="visa-stage">
        <div className="visa-panel">
          <VisaSteps step={stepState} />
          {view === 'upload' && <VisaUploader onExtracted={onExtracted} onBusy={setBusy} busy={busy} pushToast={pushToast} />}
          {view === 'review' && current && <VisaReview assessment={current} onScored={onScored} onBusy={setBusy} busy={busy} pushToast={pushToast} />}
          {view === 'result' && current && <VisaResultView assessment={current} onNew={goNew} />}
        </div>
        {view === 'result' && (
          <button className="visa-btn block" onClick={goNew} style={{ marginTop: 14 }}>
            <Icon name="plus" size={15} stroke={2.5} /> Thẩm định hồ sơ mới
          </button>
        )}
      </section>
      </div>
    </main>
  );
}

window.VisaPage = VisaPage;
