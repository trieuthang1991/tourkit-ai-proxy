// Step 2: Timeline + Costing sidebar + edit modal
const { useState: useS2 } = React;

function Step2Itinerary({ itinerary, setItinerary, request, onNext, onBack, density }) {
  const [activeDay, setActiveDay] = useS2(0);
  const [editing, setEditing] = useS2(null); // { dayIdx, actIdx } or { dayIdx, actIdx: 'new' }
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
                  <div className="activity-type">{(SERVICE_TYPES[a.type] || {}).label || a.type}</div>
                  <h4 className="activity-title">{a.title}</h4>
                  <p className="activity-desc">{a.description}</p>
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
          onClose={() => setEditing(null)}
          onSave={saveAct}
          onDelete={() => { if (editing.actIdx !== 'new') removeAct(editing.dayIdx, editing.actIdx); setEditing(null); }}
        />
      )}
    </>
  );
}

function EditServiceModal({ data, dayNum, onClose, onSave, onDelete }) {
  const [form, setForm] = useS2(data || {
    type: 'SIGHTSEEING', time: '10:00', title: '', description: '', cost: 0, supplier: ''
  });
  const lib = SUPPLIER_LIBRARY[form.type] || [];
  // Default tĩnh từ supplier library — không call AI cho đến khi user bấm.
  const defaultAdvice = `Mặc định: sử dụng <<${lib[0]?.name || 'supplier hiện có'}>>. Bấm "Hỏi AI" để có gợi ý chi tiết hơn theo ngân sách & mùa.`;
  const [advice, setAdvice] = useS2(defaultAdvice);
  const [adviceLoading, setAdviceLoading] = useS2(false);
  const [advicePristine, setAdvicePristine] = useS2(true);

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
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-left">
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
            <input className="input" value={form.title}
              onChange={e => setForm(f => ({...f, title: e.target.value}))} />
          </div>

          <div className="field">
            <label className="label">Nội dung chi tiết chương trình</label>
            <textarea className="textarea" rows={4} value={form.description}
              onChange={e => setForm(f => ({...f, description: e.target.value}))} />
          </div>

          <div className="field">
            <label className="label">Dự toán chi phí (NET)</label>
            <div style={{position: 'relative'}}>
              <input className="input numeric" value={fmtNum(form.cost)}
                onChange={e => setForm(f => ({...f, cost: parseInt(e.target.value.replace(/\D/g, '')) || 0}))}
                style={{paddingRight: 56}} />
              <span className="input-suffix">VND</span>
            </div>
          </div>

          <div className="modal-footer">
            {data && <button className="btn btn-ghost" onClick={onDelete} style={{color: 'var(--danger)'}}><Icon name="trash" size={14} /> Xóa</button>}
            <div style={{marginLeft: 'auto', display: 'flex', gap: 10}}>
              <button className="btn btn-outline" onClick={onClose}>Hủy bỏ</button>
              <button className="btn btn-primary" onClick={() => onSave(form)}>
                <Icon name="save" size={14} stroke={2} /> Lưu thay đổi
              </button>
            </div>
          </div>
        </div>

        <div className="modal-right">
          <div style={{display: 'flex', alignItems: 'center', gap: 8, marginBottom: 14}}>
            <Icon name="search" size={14} />
            <span style={{fontSize: 11, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--text-3)'}}>
              Thư viện gợi ý NCC
            </span>
          </div>

          {lib.map((s, i) => (
            <div key={i} className="supplier-card" onClick={() => applySupplier(s)}>
              <div className="supplier-card-head">
                <div className="supplier-card-name">{s.name}</div>
                <div className="supplier-card-price numeric">{fmtVND(s.price)}</div>
              </div>
              <p className="supplier-card-desc">{s.desc}</p>
              <div className="supplier-card-ncc">NCC: {s.ncc}</div>
            </div>
          ))}

          <div className="card-dark" style={{marginTop: 16, padding: 18, borderRadius: 12}}>
            <div style={{display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10}}>
              <div className="ai-spark" style={{width: 24, height: 24, borderRadius: 6}}><Icon name="sparkle" size={14} /></div>
              <span className="ai-title" style={{fontSize: 10}}>AI Expert Advice</span>
            </div>
            <div className="ai-bubble" style={{fontSize: 12.5, padding: 12, margin: 0}}>
              {adviceLoading ? (
                <>
                  <div className="sk" style={{height: 10, marginBottom: 6, width: '90%'}} />
                  <div className="sk" style={{height: 10, width: '65%'}} />
                </>
              ) : renderAdvice(advice)}
            </div>
            {!adviceLoading && (
              <button className="btn btn-outline-white btn-full" style={{padding: 10, marginTop: 10, fontSize: 12}}
                onClick={runAdvice} disabled={adviceLoading}>
                <Icon name="sparkle" size={12} /> {advicePristine ? 'Hỏi AI gợi ý' : 'Hỏi AI lại'}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

window.Step2Itinerary = Step2Itinerary;
