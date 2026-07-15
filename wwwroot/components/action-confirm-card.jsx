// components/action-confirm-card.jsx — Thẻ xác nhận hành động (ActionConfirmCard) +
// danh sách làm rõ khi AI mơ hồ (ActionClarifyList). Dùng chung /assistant + /travai
// (Jarvis) cho luồng "action tools" — AI đề xuất 1 hành động ghi (tạo lịch hẹn, gửi
// mail, v.v.), user sửa field rồi Xác nhận/Hủy; hoặc AI cần làm rõ trước khi đề xuất.
//
// proposal shape (camelCase từ backend, xem ActionField):
//   { title, summary, fields: [{ key, label, value, type, options }], estimate }
//   f.type: "text" (mặc định) | "textarea" | "datetime" | "select"
//   f.options (chỉ khi type="select"): [{ value, label }]
// clarify shape (xem ActionChoice):
//   { question, choices: [{ id, label, hint }] }

// Select CÓ Ô TÌM KIẾM — cho field nhiều option (vd "Người phụ trách": doanh nghiệp nhiều NV,
// select thường phải cuộn rất lâu). Lọc theo label (không phân biệt hoa/thường), click để chọn,
// click ra ngoài để đóng. Value luôn trả về string (khớp o.value) để executor dùng thẳng.
function ActionSearchSelect({ value, options, onChange, placeholder }) {
  const [open, setOpen] = React.useState(false);
  const [q, setQ] = React.useState("");
  const ref = React.useRef(null);
  const opts = options || [];
  const selected = opts.find(o => String(o.value) === String(value));
  const norm = s => String(s || "").toLowerCase();
  const filtered = q.trim() ? opts.filter(o => norm(o.label).includes(norm(q))) : opts;
  React.useEffect(() => {
    if (!open) return;
    const onDoc = e => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);
  return (
    <div className={"jv-ss" + (open ? " open" : "")} ref={ref}>
      <button type="button" className="jv-ss-trigger" onClick={() => { setOpen(o => !o); setQ(""); }}>
        <span className={selected ? "" : "ph"}>{selected ? selected.label : (placeholder || "— Chọn —")}</span>
        <span className="jv-ss-caret">▾</span>
      </button>
      {open && (
        <div className="jv-ss-pop">
          <input className="jv-ss-search" autoFocus placeholder="Tìm nhanh…" value={q}
            onChange={e => setQ(e.target.value)} />
          <div className="jv-ss-list">
            {filtered.length === 0
              ? <div className="jv-ss-empty">Không tìm thấy</div>
              : filtered.map(o => (
                  <button type="button" key={o.value}
                    className={"jv-ss-opt" + (String(o.value) === String(value) ? " sel" : "")}
                    onClick={() => { onChange(String(o.value)); setOpen(false); }}>
                    {o.label}
                  </button>
                ))}
          </div>
        </div>
      )}
    </div>
  );
}

function ActionConfirmCard({ proposal, onConfirm, onCancel }) {
  const [vals, setVals] = React.useState(() =>
    Object.fromEntries((proposal.fields || []).map(f => [f.key, f.value ?? ""])));

  return (
    <div className="jv-action-card">
      <div className="jv-action-title">🔔 {proposal.title}</div>
      {proposal.summary && <div className="jv-action-summary">{proposal.summary}</div>}

      {(proposal.fields || []).map(f => (
        <label key={f.key} className="jv-action-field">
          <span>{f.label}</span>
          {f.type === "textarea"
            ? <textarea
                value={vals[f.key]}
                onChange={e => setVals({ ...vals, [f.key]: e.target.value })}
              />
            : f.type === "select"
            ? ((f.options || []).length > 8
                ? <ActionSearchSelect
                    value={vals[f.key]}
                    options={f.options}
                    onChange={v => setVals({ ...vals, [f.key]: v })}
                  />
                : <select
                    value={vals[f.key]}
                    onChange={e => setVals({ ...vals, [f.key]: e.target.value })}
                  >
                    {(f.options || []).map(o => (
                      <option key={o.value} value={o.value}>{o.label}</option>
                    ))}
                  </select>)
            : <input
                type={f.type === "datetime" ? "datetime-local" : "text"}
                value={vals[f.key]}
                onChange={e => setVals({ ...vals, [f.key]: e.target.value })}
              />}
        </label>
      ))}

      {proposal.estimate && <div className="jv-action-estimate">{proposal.estimate}</div>}

      <div className="jv-action-actions">
        <button type="button" className="jv-btn-cancel" onClick={onCancel}>Hủy</button>
        <button type="button" className="jv-btn-confirm" onClick={() => onConfirm(vals)}>Xác nhận</button>
      </div>
    </div>
  );
}
window.ActionConfirmCard = ActionConfirmCard;

function ActionClarifyList({ clarify, onChoose }) {
  return (
    <div className="jv-action-card">
      {clarify.question && <div className="jv-action-summary">{clarify.question}</div>}
      {(clarify.choices || []).map(c => (
        <button
          key={c.id}
          type="button"
          className="jv-clarify-choice"
          onClick={() => onChoose(c.id, c.label, c.hint)}
        >
          {c.label}
          {c.hint ? <small> · {c.hint}</small> : null}
        </button>
      ))}
    </div>
  );
}
window.ActionClarifyList = ActionClarifyList;
