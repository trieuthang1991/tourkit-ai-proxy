// Step 3: Costing table
function EditableNum({ value, suffix, onChange, formatter }) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState('');
  if (!editing) {
    return <span className="editable-cell numeric" onClick={() => { setDraft(String(value)); setEditing(true); }}>
      {formatter ? formatter(value) : value}{suffix || ''}
    </span>;
  }
  return <input className="editable-cell numeric" autoFocus
    value={draft}
    onChange={e => setDraft(e.target.value)}
    onBlur={() => { const n = parseFloat(draft.replace(/[^\d.]/g, '')) || 0; onChange(n); setEditing(false); }}
    onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); if (e.key === 'Escape') setEditing(false); }}
    style={{width: 90, textAlign: 'right', font: 'inherit'}} />;
}

function Step3Costing({ rows, setRows, request, onNext, onBack, pushToast }) {
  const totalPax = request.adults + request.children;
  const totalSale = rows.reduce((s, r) => {
    const total = r.priceNet * (1 + r.vat/100) * (1 + r.markup/100);
    return s + total;
  }, 0);
  const adultPrice = totalSale / Math.max(totalPax, 1);
  const childPrice = adultPrice * 0.75;

  const updateRow = (idx, key, val) => {
    setRows(rs => rs.map((r, i) => i === idx ? {...r, [key]: val} : r));
  };

  return (
    <div className="card" style={{padding: 0, overflow: 'hidden'}}>
      <div style={{padding: '24px 28px', borderBottom: '1px solid var(--border)', display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 16, flexWrap: 'wrap'}}>
        <div>
          <h2 style={{fontSize: 18, fontWeight: 700, margin: '0 0 4px', letterSpacing: '-0.01em'}}>
            BẢNG TÍNH GIÁ TOUR ĐOÀN ({request.code})
          </h2>
          <p style={{fontSize: 13, color: 'var(--text-3)', margin: 0}}>
            Tự động tổng hợp dữ liệu từ timeline dịch vụ · Click cell để chỉnh sửa inline
          </p>
        </div>
        <div style={{display: 'flex', gap: 8}}>
          <button className="btn btn-outline btn-sm" onClick={() => {
            setRows([...window.COSTING_ROWS]);
            pushToast && pushToast('Đã tải lại dữ liệu costing');
          }}><Icon name="refresh" size={14} /> Tải lại dữ liệu</button>
          <button className="btn btn-outline btn-sm" onClick={() => {
            const headers = ['Dịch vụ', 'Nhà cung cấp', 'Số lượng', 'Giá NET', 'VAT %', 'Markup %', 'Thành tiền (Sale)'];
            const data = rows.map(r => [
              r.service, r.supplier, r.qty, r.priceNet, r.vat, r.markup,
              Math.round(r.priceNet * (1 + r.vat/100) * (1 + r.markup/100))
            ]);
            const csv = '\uFEFF' + [headers, ...data].map(row =>
              row.map(c => `"${String(c).replace(/"/g, '""')}"`).join(',')
            ).join('\n');
            const blob = new Blob([csv], {type: 'text/csv;charset=utf-8'});
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `Costing_${request.code}_${new Date().toISOString().slice(0,10)}.csv`;
            a.click();
            URL.revokeObjectURL(url);
            pushToast && pushToast('Đã xuất file CSV costing');
          }}><Icon name="download" size={14} /> Xuất Excel Costing</button>
        </div>
      </div>

      <div style={{overflowX: 'auto'}}>
        <table className="costing-table">
          <thead>
            <tr>
              <th>Dịch vụ</th>
              <th>Nhà cung cấp</th>
              <th>Số lượng</th>
              <th className="num">Giá NET</th>
              <th className="num">VAT %</th>
              <th className="num">Markup</th>
              <th className="num">Thành tiền (Sale)</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => {
              const total = r.priceNet * (1 + r.vat/100) * (1 + r.markup/100);
              return (
                <tr key={i}>
                  <td>
                    <div className="svc">
                      <span className="svc-icon"><Icon name={SERVICE_TYPES[r.type]?.icon || 'star'} size={14} /></span>
                      <div>
                        <div>{r.service}</div>
                      </div>
                    </div>
                  </td>
                  <td>
                    {r.supplier}
                    {r.verified && <span className="verified"><Icon name="checkCircle" size={12} stroke={2.2} /> verified</span>}
                  </td>
                  <td style={{color: 'var(--text-2)'}}>{r.qty}</td>
                  <td className="num">
                    <EditableNum value={r.priceNet} formatter={fmtVND}
                      onChange={v => updateRow(i, 'priceNet', v)} />
                  </td>
                  <td className="num">
                    <EditableNum value={r.vat} suffix="%"
                      onChange={v => updateRow(i, 'vat', v)} />
                  </td>
                  <td className="num markup">
                    <EditableNum value={r.markup} suffix="%"
                      onChange={v => updateRow(i, 'markup', v)}
                      formatter={v => '+' + v} />
                  </td>
                  <td className="num" style={{fontWeight: 700}}>
                    <span className="numeric">{fmtVND(total)}</span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div style={{padding: '0 28px 28px'}}>
        <div className="cost-total-bar">
          <div>
            <div className="cost-total-label">TỔNG GIÁ TRỊ TOUR DỰ KIẾN (BÁN)</div>
            <div style={{fontSize: 12, color: 'rgba(255,255,255,0.5)', marginTop: 4}}>
              {totalPax} khách · {request.days}N{request.nights}Đ · {request.route}
            </div>
          </div>
          <div className="cost-total-amount numeric">{fmtVND(totalSale)}</div>
        </div>

        <div className="per-pax-row">
          <div className="per-pax-card">
            <div className="per-pax-label">Giá / Người lớn</div>
            <div className="per-pax-amount numeric">{fmtVND(adultPrice)}</div>
          </div>
          <div className="per-pax-card">
            <div className="per-pax-label">Giá / Trẻ em (75%)</div>
            <div className="per-pax-amount accent numeric">{fmtVND(childPrice)}</div>
          </div>
          <div style={{display: 'flex', gap: 10, alignItems: 'center', marginLeft: 'auto'}}>
            <button className="btn btn-outline btn-lg" onClick={onBack}>
              <Icon name="arrowLeft" size={14} /> Quay lại
            </button>
            <button className="btn btn-primary btn-lg" onClick={onNext}>
              <Icon name="sparkle" size={14} /> Sinh báo giá đẹp
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

window.Step3Costing = Step3Costing;
