// Step 1: Form + AI Assistant panel
const { useState, useEffect, useRef } = React;

function Step1Form({ request, setRequest, onGenerate, generating, genStream, genProgress, aiTone, pushToast }) {
  const [showAddPref, setShowAddPref] = React.useState(false);
  const [historyOpen, setHistoryOpen] = React.useState(false);
  const [history, setHistory] = React.useState(() => window.tourkitHistory?.load() || []);

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
            <span style={{fontSize: 11, color: 'var(--text-3)', letterSpacing: '0.05em'}}>
              MÃ: <strong style={{color: 'var(--text)'}}>{request.code}</strong>
            </span>
          </div>

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

        <div className="field-row">
          <div>
            <label className="label">Thời gian</label>
            <div className="input-icon">
              <Icon name="calendar" size={16} />
              <input className="input" value={`${request.days} Ngày ${request.nights} Đêm`}
                onChange={e => {
                  const m = e.target.value.match(/(\d+)\s*Ngày\s*(\d+)/i);
                  if (m) setRequest(r => ({...r, days: +m[1], nights: +m[2]}));
                }} />
            </div>
          </div>
          <div>
            <label className="label">Ngày khởi hành</label>
            <div className="input-icon">
              <Icon name="calendar" size={16} />
              <input className="input" type="date" value={request.startDate}
                onChange={e => setRequest(r => ({...r, startDate: e.target.value}))} />
            </div>
          </div>
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
