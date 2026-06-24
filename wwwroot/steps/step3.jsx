// Step 3: Costing table — pricing engine đầy đủ (B3 hybrid + FOC + Red Zone + child 75%).
//
// Pricing model (mirror tmp/ceo-dashboard/AITourPlanner.tsx — audit & fix em đã làm):
//   • Per-type markup là DEFAULT (row markup edit inline).
//   • Margin slider toggle GLOBAL OVERRIDE — kéo slider hoặc click "Cài %" → mọi row dùng % global.
//     Click "↩ Per-type" → reset về markup riêng từng row.
//   • Red Zone: margin total < 15% → text đỏ + banner cảnh báo.
//   • Pax quy đổi: trẻ em = 0.75 người lớn → ảnh hưởng Net/Pax + Profit/Pax (KHÔNG ảnh hưởng tổng).
//   • FOC N+1 cho từng row → effectiveNet = priceNet × N/(N+1). UI badge xanh + giá cũ gạch chéo.

const RED_ZONE_MARGIN_WIZ = 15;   // %

function EditableNum({ value, suffix, onChange, formatter, disabled }) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState('');
  if (disabled) {
    return <span className="editable-cell numeric" style={{opacity: 0.55, cursor: 'not-allowed'}} title="Đang dùng Global Margin — bỏ override để chỉnh row">
      {formatter ? formatter(value) : value}{suffix || ''}
    </span>;
  }
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

// ── Section sub-table (Chi phí Riêng / Chung / By Day) ────────────────────
// Render 1 sub-bảng với section header (avatar icon + code + title + tag)
// + table rows (7 cols compact) + subtotal "CỘNG PHỤ MỤC CỦA BẢNG"
function CostingSection({ code, title, tag, tagIcon, color, bg, sub,
                          list, subNet, subSale, effNet, rowMarkup, rowSale, rowCostType,
                          updateRow, useGlobal, totalPax, empty }) {
  return (
    <div className="costing-section">
      <div className="cs-head">
        <div className="cs-avatar" style={{background: bg, color: color}}>
          <Icon name={tagIcon} size={14} stroke={2} />
        </div>
        <div className="cs-head-text">
          <div className="cs-title">
            <strong>{code}. {title}</strong>
            {tag && <span className="cs-tag" style={{color: color}}>({tag})</span>}
          </div>
          {sub && <div className="cs-sub">{sub}</div>}
        </div>
      </div>

      {list.length === 0 ? (
        <div className="cs-empty">{empty || 'Chưa có dịch vụ'}</div>
      ) : (
        <window.TKTableScroll>
          <table className="costing-table cs-table">
            <thead>
              <tr>
                <th>DỊCH VỤ</th>
                <th>NHÀ CUNG CẤP / CHI TIẾT</th>
                <th>SỐ LƯỢNG</th>
                <th className="num">GIÁ NET</th>
                <th className="num">VAT (%)</th>
                <th className="num">MARKUP</th>
                <th className="num">THÀNH TIỀN (SALE)</th>
              </tr>
            </thead>
            <tbody>
              {list.map(({r, i}) => {
                const en = effNet(r);
                const hasFoc = r.focRatio && r.focRatio > 0;
                const sale = rowSale(r);
                const ct = rowCostType(r);
                return (
                  <tr key={i}>
                    <td>
                      <div className="svc">
                        <span className="svc-icon"><Icon name={SERVICE_TYPES[r.type]?.icon || 'star'} size={14} /></span>
                        <div>
                          <div style={{fontWeight: 600}}>{r.service}</div>
                          <div className="cs-row-type" style={{color: color, background: bg}}>
                            <Icon name={tagIcon} size={9} /> {ct === 'pax' ? 'CHI PHÍ RIÊNG' : 'CHI PHÍ CHUNG'}
                          </div>
                        </div>
                      </div>
                    </td>
                    <td>
                      <div style={{fontSize: 12.5}}>{r.supplier || '—'}</div>
                      {r.detail && <div style={{fontSize: 11, color: 'var(--text-3)', marginTop: 2}}>{r.detail}</div>}
                      {r.verified && <span className="verified"><Icon name="checkCircle" size={11} stroke={2.2} /> verified</span>}
                    </td>
                    <td style={{color: 'var(--text-2)'}}>
                      <span style={{color: color, fontWeight: 600}}>{r.qty}</span>
                      {ct === 'pax' ? ' Pax' : ' Đoàn'}
                    </td>
                    <td className="num">
                      <EditableNum value={r.priceNet} formatter={fmtVND}
                        onChange={v => updateRow(i, 'priceNet', v)} />
                      {(ct === 'pax' || hasFoc) && (
                        <div style={{fontSize: 10, color: hasFoc ? '#10b981' : 'var(--text-3)', fontWeight: 600, marginTop: 2}}>
                          {ct === 'pax' ? `× ${totalPax} = ` : 'eff: '}{fmtVND(Math.round(en))}{hasFoc && ' (FOC)'}
                        </div>
                      )}
                    </td>
                    <td className="num">
                      <EditableNum value={r.vat} suffix="%"
                        onChange={v => updateRow(i, 'vat', v)} />
                    </td>
                    <td className="num markup">
                      <EditableNum value={rowMarkup(r)} suffix="%"
                        onChange={v => updateRow(i, 'markup', v)}
                        formatter={v => '+' + v}
                        disabled={useGlobal} />
                    </td>
                    <td className="num" style={{fontWeight: 700}}>
                      <span className="numeric">{fmtVND(sale)}</span>
                    </td>
                  </tr>
                );
              })}
              <tr className="cs-subtotal">
                <td colSpan={3} style={{textAlign: 'right', fontWeight: 700, color: 'var(--text-2)',
                  textTransform: 'uppercase', fontSize: 11.5, letterSpacing: '0.04em'}}>
                  CỘNG PHỤ MỤC CỦA BẢNG:
                </td>
                <td className="num" colSpan={3} style={{textAlign: 'right', color: 'var(--text-2)', fontWeight: 600}}>
                  Cộng Net: <span className="numeric" style={{color: 'var(--text)', fontWeight: 700, marginLeft: 6}}>{fmtVND(Math.round(subNet))}</span>
                </td>
                <td className="num" style={{fontWeight: 800, color: color}}>
                  <span style={{fontSize: 11, color: 'var(--text-3)', fontWeight: 600, marginRight: 6}}>Cộng Sale:</span>
                  <span className="numeric">{fmtVND(Math.round(subSale))}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </window.TKTableScroll>
      )}
    </div>
  );
}
window.CostingSection = CostingSection;

function Step3Costing({ rows, setRows, request, onNext, onBack, pushToast,
                       hotelStars, setHotelStars, paxRanges, setPaxRanges,
                       hotelOptions, activeTier, setActiveTier, itinerary }) {
  // ── B3 Hybrid: marginOverride null = per-type, number = global override.
  const [marginOverride, setMarginOverride] = React.useState(null);
  const useGlobal = marginOverride !== null;
  const [autoMatchPax, setAutoMatchPax] = React.useState(false);
  // ── View mode (v3.3): tách Riêng vs Chung (default) · danh sách dẹt · xếp theo ngày
  const [viewMode, setViewMode] = React.useState('split'); // 'split' | 'flat' | 'by-day'

  // ── Hotel star tiers — config từ Step 1 (lifted state). Fallback default nếu Step 1
  // chưa pass (legacy / standalone).
  const HOTEL_TIER_PRICE = { 3: 650000, 4: 1150000, 5: 2150000, 6: 4250000 };
  // Fallback nếu props không có
  paxRanges = paxRanges || [
    { from: 1,  to: 14,  markup: 28 },
    { from: 15, to: 30,  markup: 22 },
    { from: 31, to: 200, markup: 18 },
  ];
  hotelStars = hotelStars || [3, 4, 5];
  const [showOptionsMatrix, setShowOptionsMatrix] = React.useState(false);

  // Pax quy đổi: trẻ em 0.75
  const paxEquiv = (request.adults + request.children * 0.75) || 1;
  const totalPax = request.adults + request.children;

  // Auto-link margin khi adults thay đổi (chỉ khi autoMatch bật)
  React.useEffect(() => {
    if (!autoMatchPax) return;
    const matched = paxRanges.find(r => request.adults >= r.from && request.adults <= r.to);
    if (matched) setMarginOverride(matched.markup);
  }, [request.adults, paxRanges, autoMatchPax]);

  // ── costType per row:
  //   'shared'  → priceNet là tổng toàn đoàn (vd xe 8tr/chuyến)
  //   'pax'     → priceNet là đơn giá / pax (vd KS 1.15tr/khách/đêm) → total = priceNet × paxCount
  // Fallback theo TYPE — đồng bộ với badge ở Step 2 activity card + defaultCostType() ở modal:
  //   HOTEL / MEAL / TICKET → pax (Chi phí Riêng)
  //   else → shared (Chi phí Chung)
  // Vì priceNet trong COSTING_ROWS demo ghi theo NGÀY (qty="Ngày X"), khi flip sang pax sẽ × pax →
  // hiển thị eff lớn hơn. User vẫn có thể nhập lại giá (theo /khách) khi cần.
  const PAX_TYPES = { HOTEL: 1, MEAL: 1, TICKET: 1 };
  const rowCostType = (r) => r.costType || (PAX_TYPES[r.type] ? 'pax' : 'shared');
  // Net "toàn đoàn" cho 1 row sau khi áp costType + FOC.
  const effNet = (r) => {
    const base = rowCostType(r) === 'pax' ? (r.priceNet || 0) * totalPax : (r.priceNet || 0);
    return r.focRatio && r.focRatio > 0
      ? base * r.focRatio / (r.focRatio + 1)
      : base;
  };
  const rowMarkup = (r) => useGlobal ? marginOverride : (r.markup || 0);
  const rowSale = (r) => effNet(r) * (1 + (r.vat || 0) / 100) * (1 + rowMarkup(r) / 100);

  // Tổng (raw — double, round chỉ ở display)
  const totalNet   = rows.reduce((s, r) => s + effNet(r), 0);
  const totalSale  = rows.reduce((s, r) => s + rowSale(r), 0);
  const totalProfit = totalSale - totalNet;
  const netPerPax    = totalNet / paxEquiv;
  const salePerPax   = totalSale / paxEquiv;
  const profitPerPax = totalProfit / paxEquiv;

  // Margin% (gross): profit / sale — derived khi per-type, global khi override
  const effectiveMargin = totalSale > 0 ? (totalProfit / totalSale) * 100 : 0;
  const displayedMargin = useGlobal ? marginOverride : effectiveMargin;
  const isRedZone = totalSale > 0 && displayedMargin < RED_ZONE_MARGIN_WIZ;

  // Hiển thị giá đoàn (giữ logic adult/child 75% cho output)
  const adultPrice = salePerPax;          // = totalSale / paxEquiv
  const childPrice = salePerPax * 0.75;

  // ── Multi-pricing-options preview (v2 logic) ───────────────────────────────
  // Cross-product (hotelStars × paxRanges) → mỗi phương án có Net/Pax + Price/Pax riêng.
  // Cách tính: hotel cost dùng tier price từ HOTEL_TIER_PRICE × nights (lấy từ request);
  // non-hotel cost giữ nguyên (sum effNet của các row !== HOTEL) chia cho pCount giả định.
  // Apply → ghi đè priceNet hotel rows + setMargin (KHÔNG đổi adults vì state nằm ở Step 1).
  const nights = request.nights || Math.max((request.days || 1) - 1, 1);
  const nonHotelNetTotal = rows.reduce((s, r) =>
    s + (r.type === 'HOTEL' ? 0 : effNet(r)), 0);
  const pricingOptions = hotelStars.flatMap(star =>
    paxRanges.map(range => {
      const pCount = Math.round((range.from + range.to) / 2) || 30;
      const hotelPerPax = (HOTEL_TIER_PRICE[star] || 1000000) * nights;
      const nonHotelPerPax = pCount > 0 ? nonHotelNetTotal / pCount : 0;
      const net = hotelPerPax + nonHotelPerPax;
      const price = net / (1 - range.markup / 100);
      const isActive = request.adults >= range.from && request.adults <= range.to
        && rows.some(r => r.type === 'HOTEL' && String(r.service).includes(`${star} sao`));
      return { star, range, pCount, hotelPerPax, net, price, profit: price - net, isActive };
    }));

  const applyPricingOption = (opt) => {
    // Override margin (B3 global override)
    setMarginOverride(opt.range.markup);
    // Ghi đè HOTEL rows: costType='pax' + priceNet = tier × nights (per pax).
    // → effNet tự nhân totalPax → đổi adults/children sau Apply, cost AUTO-sync (không phải Apply lại).
    setRows(rs => rs.map(r => r.type === 'HOTEL'
      ? { ...r,
          priceNet: opt.hotelPerPax,    // unit cost per pax per stay (tier × nights)
          costType: 'pax',              // ← KEY: auto-multiply by paxCount khi adults đổi
          service: r.service.replace(/\d+\s*sao/i, '').replace(/\s*\(.*?\)\s*/g, '').trim() + ` ${opt.star} sao`,
          supplier: r.supplier || `Khách sạn ${opt.star}*` }
      : r));
    pushToast && pushToast(`Áp dụng: KS ${opt.star}* · ${opt.range.from}-${opt.range.to} pax · margin ${opt.range.markup}% (cost auto-sync khi đổi pax)`);
  };

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
          <div style={{display: 'flex', alignItems: 'center', gap: 12, fontSize: 13, color: 'var(--text-2)', flexWrap: 'wrap', marginTop: 2}}>
            {request.customerName && (<>
              <span><Icon name="user" size={12} /> <strong>{request.customerName}</strong></span>
              <span style={{color: 'var(--text-3)'}}>·</span>
            </>)}
            <span>{request.route || '—'}</span>
            <span style={{color: 'var(--text-3)'}}>·</span>
            <span>{request.adults}NL{request.children > 0 ? ` + ${request.children}TE` : ''}</span>
            <span style={{color: 'var(--text-3)'}}>·</span>
            <span>{request.days}N{request.nights}Đ</span>
          </div>
          <p style={{fontSize: 12, color: 'var(--text-3)', margin: '6px 0 0'}}>
            Per-type markup mặc định · Click cell chỉnh inline · Slider Margin để override toàn bảng
          </p>
        </div>
        <div style={{display: 'flex', gap: 8}}>
          <button className="btn btn-outline btn-sm" onClick={() => {
            setRows([...window.COSTING_ROWS]);
            setMarginOverride(null);
            pushToast && pushToast('Đã tải lại dữ liệu costing');
          }}><Icon name="refresh" size={14} /> Tải lại dữ liệu</button>
          <button className="btn btn-outline btn-sm" onClick={() => {
            const headers = ['Dịch vụ', 'Nhà cung cấp', 'Số lượng', 'Loại giá', 'Giá NET (đơn vị)', 'Net tổng (sau FOC)', 'FOC N+1', 'VAT %', 'Markup %', 'Thành tiền (Sale)'];
            const data = rows.map(r => [
              r.service, r.supplier, r.qty,
              rowCostType(r) === 'pax' ? `Per Pax × ${totalPax}` : 'Toàn đoàn',
              r.priceNet, Math.round(effNet(r)),
              r.focRatio || 0, r.vat, rowMarkup(r),
              Math.round(rowSale(r))
            ]);
            const csv = '﻿' + [headers, ...data].map(row =>
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

      {/* Bảng so sánh phương án giá (v3.2): cards per tier user đã chọn ở Step 1
          + đêm hotel tự derive từ itinerary. Click card → setActiveTier → Step 4 dùng. */}
      {hotelStars && hotelStars.length > 0 && (() => {
        const nights = request.nights || Math.max((request.days || 1) - 1, 1);
        const totalPax_ = request.adults + request.children;
        const paxRangeLbl = (() => {
          // Khoảng pax matching hotel pricing matrix (lấy range chứa totalPax)
          const m = (paxRanges || []).find(r => totalPax_ >= r.from && totalPax_ <= r.to);
          return m ? `${m.from}-${m.to} PAX` : `${totalPax_} PAX`;
        })();
        const nonHotelSale_ = rows.filter(r => r.type !== 'HOTEL').reduce((s, r) =>
          s + (r.priceNet || 0) * (1 + (r.vat || 0)/100) * (1 + rowMarkup(r)/100), 0);
        const nonHotelNet_  = rows.filter(r => r.type !== 'HOTEL').reduce((s, r) =>
          s + (r.priceNet || 0), 0);
        const hRow_ = rows.find(r => r.type === 'HOTEL');
        const hMk_  = hRow_ ? rowMarkup(hRow_) : 20;
        // Hotel price/pax/đêm fallback: nếu chưa pick ở Bước 2 thì dùng giá tham khảo
        const TIER_FALLBACK_PRICE = { 2: 380000, 3: 650000, 4: 1150000, 5: 2150000, 6: 4250000 };
        const cards = hotelStars.map(star => {
          const opt = hotelOptions?.[star];
          const hotelPerPaxPerNight = opt?.pricePerPaxPerNight || TIER_FALLBACK_PRICE[star] || 1000000;
          const hotelNetPerPax  = hotelPerPaxPerNight * nights;
          const hotelSalePerPax = hotelNetPerPax * (1 + hMk_/100);
          // Per-pax price + per-pax profit
          const pricePerPax  = (nonHotelSale_ + hotelSalePerPax * totalPax_) / Math.max(totalPax_, 1);
          const netPerPax    = (nonHotelNet_ + hotelNetPerPax * totalPax_) / Math.max(totalPax_, 1);
          const profitPerPax = pricePerPax - netPerPax;
          const markup       = netPerPax > 0 ? Math.round(((pricePerPax - netPerPax) / netPerPax) * 100) : 0;
          return { star, pricePerPax, profitPerPax, markup, isFallback: !opt };
        });
        const active = activeTier || hotelStars[0];
        return (
          <div className="bsg-panel">
            <div className="bsg-head">
              <div>
                <div className="bsg-title">BẢNG SO SÁNH PHƯƠNG ÁN GIÁ</div>
                <div className="bsg-sub">Tính theo tổ hợp Option Khách sạn và Khoảng Khách (Pax Range)</div>
              </div>
              <button className="bsg-quick" title="Chọn phương án có lợi nhuận cao nhất"
                onClick={() => {
                  const best = cards.reduce((a, b) => b.profitPerPax > a.profitPerPax ? b : a);
                  setActiveTier && setActiveTier(best.star);
                  pushToast && pushToast(`Chọn nhanh: ${best.star}★ (lợi nhuận cao nhất)`);
                }}>
                CHỌN NHANH
              </button>
            </div>
            <div className="bsg-cards">
              {cards.map(c => {
                const isActive = c.star === active;
                return (
                  <div key={c.star}
                    className={'bsg-card' + (isActive ? ' on' : '')}
                    onClick={() => setActiveTier && setActiveTier(c.star)}>
                    <div className="bsg-card-head">
                      <div className="bsg-card-stars">
                        {'★'.repeat(c.star)}<span className="bsg-card-tier"> {c.star} SAO</span>
                        <span className="bsg-card-dot">·</span>
                        <span className="bsg-card-pax">{paxRangeLbl}</span>
                      </div>
                      {isActive && <span className="bsg-active-badge">ĐANG CHỌN</span>}
                    </div>
                    <div className="bsg-card-row">
                      <div className="bsg-card-price-col">
                        <div className="bsg-card-label">GIÁ ĐỀ XUẤT / PAX</div>
                        <div className="bsg-card-price numeric">{fmtVND(Math.round(c.pricePerPax))}</div>
                        {c.isFallback && <div className="bsg-card-warn">⚠ Chưa chọn NCC — giá ước lượng</div>}
                      </div>
                      <div className="bsg-card-stat-col">
                        <div className="bsg-card-stat-row">
                          <span className="bsg-card-stat-label">Markup:</span>
                          <span className="bsg-card-stat-val pos">+{c.markup}%</span>
                        </div>
                        <div className="bsg-card-stat-row">
                          <span className="bsg-card-stat-label">Lợi nhuận:</span>
                          <span className="bsg-card-stat-val">{fmtVND(Math.round(c.profitPerPax))}</span>
                        </div>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        );
      })()}

      {/* ── Margin Control Bar (B3 Hybrid + Red Zone) ───────────────────────── */}
      <div style={{padding: '16px 28px', borderBottom: '1px solid var(--border)', background: isRedZone ? '#fef2f2' : '#fafafa'}}>
        <div style={{display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap'}}>
          <div style={{display: 'flex', alignItems: 'center', gap: 10}}>
            <span style={{fontSize: 12, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', letterSpacing: '0.05em'}}>Margin %</span>
            <span style={{
              fontSize: 10, fontWeight: 800, padding: '3px 8px', borderRadius: 999,
              background: useGlobal ? '#fff3e8' : 'var(--bg)',
              color: useGlobal ? '#c2410c' : 'var(--text-2)',
              border: '1px solid ' + (useGlobal ? '#fdba74' : 'var(--border)')
            }}>
              {useGlobal ? 'GLOBAL OVERRIDE' : 'PER-TYPE DERIVED'}
            </span>
            <span style={{
              fontSize: 22, fontWeight: 800, marginLeft: 8,
              color: isRedZone ? '#dc2626' : (displayedMargin >= 25 ? '#10b981' : '#f59e0b')
            }}>
              {totalSale > 0 ? displayedMargin.toFixed(1) + '%' : '—'}
            </span>
          </div>

          <div style={{flex: 1, minWidth: 200, display: 'flex', alignItems: 'center', gap: 10}}>
            <input
              type="range" min={0} max={60} step={0.5}
              value={useGlobal ? marginOverride : effectiveMargin}
              onChange={(e) => setMarginOverride(parseFloat(e.target.value))}
              style={{flex: 1, accentColor: 'var(--accent)', cursor: 'pointer'}}
              title={useGlobal ? 'Kéo để chỉnh global margin' : 'Kéo để bật Global Override'}
            />
            <div style={{display: 'flex', gap: 4, fontSize: 10, color: 'var(--text-3)', fontWeight: 600}}>
              <span>0%</span>
              <span style={{color: '#dc2626'}}>⚠ Red {RED_ZONE_MARGIN_WIZ}%</span>
              <span>60%</span>
            </div>
          </div>

          {useGlobal && (
            <button onClick={() => setMarginOverride(null)}
              style={{background: 'transparent', border: '1px solid #fdba74', color: '#c2410c',
                fontSize: 11, fontWeight: 700, padding: '4px 10px', borderRadius: 8, cursor: 'pointer'}}>
              ↩ Bỏ override (về Per-type)
            </button>
          )}
        </div>

        {isRedZone && (
          <div style={{marginTop: 10, padding: '8px 12px', background: 'white', border: '1px solid #fecaca',
            borderRadius: 8, fontSize: 12, fontWeight: 600, color: '#b91c1c', display: 'flex', alignItems: 'center', gap: 8}}>
            ⚠ <span>Red Zone — Margin {displayedMargin.toFixed(1)}% dưới ngưỡng tối thiểu {RED_ZONE_MARGIN_WIZ}%. Kiểm duyệt giá trước khi gửi khách.</span>
          </div>
        )}

        {/* ── Pricing Options Matrix (v2 logic) ────────────────────────────────
            Cross product hotelStars × paxRanges → so sánh nhiều phương án. Apply 1 → ghi
            đè hotel rows + margin. Default collapsed để khỏi tốn chiều cao trên page. */}
        <div style={{marginTop: 12, padding: '10px 12px', background: 'white',
          border: '1px solid var(--border)', borderRadius: 8}}>
          <button onClick={() => setShowOptionsMatrix(o => !o)}
            style={{background: 'transparent', border: 'none', cursor: 'pointer',
              display: 'flex', alignItems: 'center', gap: 8, width: '100%', padding: 0,
              color: 'var(--text-2)', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em'}}>
            <Icon name={showOptionsMatrix ? 'chevronDown' : 'chevronRight'} size={12} />
            Bảng phương án giá ({hotelStars.length}× {paxRanges.length} = {hotelStars.length * paxRanges.length} option)
            <span style={{marginLeft: 'auto', fontSize: 10, color: 'var(--text-3)', fontWeight: 600}}>
              Cross product · KS sao × pax range
            </span>
          </button>

          {showOptionsMatrix && (
            <div style={{marginTop: 10}}>
              {/* Tier KS + Pax range config từ Step 1 — hiển thị summary read-only */}
              <div style={{marginBottom: 10, fontSize: 10, color: 'var(--text-3)', fontStyle: 'italic'}}>
                Config từ Step 1: {hotelStars.length} tier ({hotelStars.map(s => `${s}*`).join(', ')}) ×{' '}
                {paxRanges.length} pax range = {hotelStars.length * paxRanges.length} phương án.
                Quay lại Step 1 để chỉnh.
              </div>

              {pricingOptions.length === 0 ? (
                <div style={{padding: 12, color: 'var(--text-3)', fontSize: 12, textAlign: 'center'}}>
                  Chọn ít nhất 1 hotel tier để xem phương án.
                </div>
              ) : (
                <window.TKTableScroll style={{border: 'none', borderRadius: 0, boxShadow: 'none', background: 'transparent'}}>
                  <table style={{width: '100%', borderCollapse: 'collapse', fontSize: 12}}>
                    <thead>
                      <tr style={{background: 'var(--bg)', textAlign: 'left'}}>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase'}}>KS</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase'}}>Pax range</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase'}}>Markup</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', textAlign: 'right'}}>Net / Pax</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', textAlign: 'right'}}>Giá bán / Pax</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', textAlign: 'right'}}>Lãi / Pax</th>
                        <th style={{padding: '6px 10px', fontSize: 10, fontWeight: 700, color: 'var(--text-3)', textTransform: 'uppercase', textAlign: 'right'}}>Tổng tour</th>
                        <th style={{padding: '6px 10px'}}></th>
                      </tr>
                    </thead>
                    <tbody>
                      {pricingOptions.map((opt, i) => (
                        <tr key={i} style={{
                          borderTop: '1px solid var(--border)',
                          background: opt.isActive ? '#fff3e8' : 'transparent',
                        }}>
                          <td style={{padding: '8px 10px', fontWeight: 700}}>{opt.star}*</td>
                          <td style={{padding: '8px 10px', color: 'var(--text-2)'}}>{opt.range.from}-{opt.range.to}</td>
                          <td style={{padding: '8px 10px', color: opt.range.markup < 15 ? '#dc2626' : opt.range.markup >= 25 ? '#10b981' : '#f59e0b', fontWeight: 700}}>{opt.range.markup}%</td>
                          <td style={{padding: '8px 10px', textAlign: 'right', color: 'var(--text-2)'}}>{fmtVND(Math.round(opt.net))}</td>
                          <td style={{padding: '8px 10px', textAlign: 'right', fontWeight: 700}}>{fmtVND(Math.round(opt.price))}</td>
                          <td style={{padding: '8px 10px', textAlign: 'right', color: '#10b981', fontWeight: 700}}>{fmtVND(Math.round(opt.profit))}</td>
                          <td style={{padding: '8px 10px', textAlign: 'right', color: 'var(--text-3)', fontSize: 11}}>
                            <span title={`${opt.pCount} pax giả định`}>{fmtVND(Math.round(opt.price * opt.pCount))}</span>
                          </td>
                          <td style={{padding: '8px 10px', textAlign: 'right'}}>
                            <button onClick={() => applyPricingOption(opt)}
                              style={{
                                padding: '3px 10px', fontSize: 10, fontWeight: 700,
                                background: 'var(--accent)', color: 'white',
                                border: 'none', borderRadius: 4, cursor: 'pointer',
                              }}>
                              Apply
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </window.TKTableScroll>
              )}
              <div style={{marginTop: 8, fontSize: 10, color: 'var(--text-3)', fontStyle: 'italic'}}>
                💡 Apply: ghi đè priceNet HOTEL rows + set margin slider · KHÔNG đổi pax (về Step 1 chỉnh nếu cần).
              </div>
            </div>
          )}
        </div>

        {/* ── PaxRange Auto-match (Step 1 owns editing) ────────────────────────
            Step 1 đã có UI sửa paxRanges + hotelStars (lifted state). Step 3 chỉ
            cho phép bật auto-match: khi adults → set margin theo range matched. */}
        <div style={{marginTop: 12, padding: '8px 12px', background: 'white',
          border: '1px solid var(--border)', borderRadius: 8, display: 'flex',
          alignItems: 'center', gap: 12, flexWrap: 'wrap'}}>
          <label style={{display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, fontWeight: 700,
            color: 'var(--text-2)', cursor: 'pointer', textTransform: 'uppercase', letterSpacing: '0.05em'}}>
            <input type="checkbox" checked={autoMatchPax}
              onChange={e => setAutoMatchPax(e.target.checked)} />
            Auto-match markup theo pax (từ Step 1)
          </label>
          <span style={{fontSize: 10, color: 'var(--text-3)'}}>
            {request.adults} người lớn · matched →{' '}
            <strong style={{color: 'var(--accent)'}}>
              {(() => {
                const m = paxRanges.find(r => request.adults >= r.from && request.adults <= r.to);
                return m ? `${m.from}-${m.to} pax @ ${m.markup}%` : 'không khớp';
              })()}
            </strong>
          </span>
        </div>
      </div>

      {/* ── View Mode Toggle (v3.3) ─────────────────────────────────────────── */}
      <div style={{padding: '14px 28px', borderBottom: '1px solid var(--border)',
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap'}}>
        <div>
          <div style={{fontSize: 11, fontWeight: 700, color: 'var(--text-3)',
            textTransform: 'uppercase', letterSpacing: '0.05em'}}>CHẾ ĐỘ HIỂN THỊ DỰ TOÁN DỊCH VỤ</div>
          <div style={{fontSize: 11.5, color: 'var(--text-3)', marginTop: 3}}>
            Đảm bảo cấu trúc hợp lý để khái niệm dữ liệu kế toán giá kiểm toán trực quan
          </div>
        </div>
        <div style={{display: 'inline-flex', background: '#f1f5f9', border: '1px solid var(--border)',
          borderRadius: 8, padding: 3, gap: 2}}>
          {[
            {k: 'split',  lbl: 'Tách Chi Phí Chung - Riêng (Mặc định)', icon: 'sliders'},
            {k: 'flat',   lbl: 'Danh sách dẹt (List)', icon: 'list'},
            {k: 'by-day', lbl: 'Xếp theo ngày đi',      icon: 'calendar'},
          ].map(m => (
            <button key={m.k} onClick={() => setViewMode(m.k)}
              style={{
                padding: '6px 12px', fontSize: 11.5, fontWeight: 700,
                background: viewMode === m.k ? 'white' : 'transparent',
                color: viewMode === m.k ? 'var(--text)' : 'var(--text-3)',
                border: '1px solid ' + (viewMode === m.k ? 'var(--border)' : 'transparent'),
                borderRadius: 6, cursor: 'pointer',
                boxShadow: viewMode === m.k ? '0 1px 2px rgba(0,0,0,0.04)' : 'none',
                display: 'inline-flex', alignItems: 'center', gap: 6,
              }}>
              <Icon name={m.icon} size={12} />{m.lbl}
            </button>
          ))}
        </div>
      </div>

      {/* ── Render rows by mode ─────────────────────────────────────────────── */}
      {viewMode === 'split' && (() => {
        const rowsRieng  = rows.map((r, i) => ({r, i})).filter(({r}) => rowCostType(r) === 'pax');
        const rowsChung  = rows.map((r, i) => ({r, i})).filter(({r}) => rowCostType(r) === 'shared');
        const subNet  = list => list.reduce((s, {r}) => s + effNet(r), 0);
        const subSale = list => list.reduce((s, {r}) => s + rowSale(r), 0);
        return (
          <>
            <CostingSection
              code="A" title="BẢNG CHI PHÍ RIÊNG"
              tag="CHI PHÍ RIÊNG LẺ - TÍNH THEO PAX KHÁCH"
              tagIcon="user" color="#a855f7" bg="#f5f3ff"
              sub={`Chi phí phát sinh nhân theo trực tiếp theo từng khách hàng (${totalPax} Pax)`}
              list={rowsRieng} subNet={subNet(rowsRieng)} subSale={subSale(rowsRieng)}
              effNet={effNet} rowMarkup={rowMarkup} rowSale={rowSale} rowCostType={rowCostType}
              updateRow={updateRow} useGlobal={useGlobal} totalPax={totalPax}
              empty="Chưa có dịch vụ tính theo pax — chuyển 'Loại giá' sang × Pax ở section List để chuyển vào đây" />

            <CostingSection
              code="B" title="BẢNG CHI PHÍ CHUNG"
              tag="CHI PHÍ CHUNG CẢ ĐOÀN - TRỌN GÓI ĐOÀN"
              tagIcon="paper" color="#0891b2" bg="#ecfeff"
              sub="Chi phí cố định không phụ thuộc vào số khách"
              list={rowsChung} subNet={subNet(rowsChung)} subSale={subSale(rowsChung)}
              effNet={effNet} rowMarkup={rowMarkup} rowSale={rowSale} rowCostType={rowCostType}
              updateRow={updateRow} useGlobal={useGlobal} totalPax={totalPax}
              empty="Chưa có dịch vụ trọn gói đoàn" />
          </>
        );
      })()}

      {viewMode === 'by-day' && (() => {
        // Group by day-index lưu trong row (r.dayIdx). Nếu chưa có → fallback split mode.
        const grouped = {};
        rows.forEach((r, i) => {
          const d = r.dayIdx != null ? r.dayIdx : -1;
          (grouped[d] = grouped[d] || []).push({r, i});
        });
        const dayKeys = Object.keys(grouped).map(Number).sort((a, b) => a - b);
        const dayList = (itinerary && itinerary.days) || [];
        if (dayKeys.length === 1 && dayKeys[0] === -1) {
          return (
            <div style={{padding: 28, textAlign: 'center', color: 'var(--text-3)', fontSize: 13}}>
              Dữ liệu costing chưa được gắn vào ngày cụ thể. Vui lòng quay lại Step 2 để khớp dịch vụ với từng ngày.
            </div>
          );
        }
        return dayKeys.map(d => {
          const list = grouped[d];
          const dayLabel = d === -1
            ? 'Chưa gắn ngày'
            : (dayList[d]?.title || `Ngày ${d + 1}`);
          return (
            <CostingSection
              key={d} code={d === -1 ? '—' : `N${d + 1}`}
              title={dayLabel} tag={`${list.length} dịch vụ`}
              tagIcon="calendar" color="#f97316" bg="#fff7ed"
              sub={d === -1 ? '' : `Ngày ${d + 1} của hành trình`}
              list={list}
              subNet={list.reduce((s, {r}) => s + effNet(r), 0)}
              subSale={list.reduce((s, {r}) => s + rowSale(r), 0)}
              effNet={effNet} rowMarkup={rowMarkup} rowSale={rowSale} rowCostType={rowCostType}
              updateRow={updateRow} useGlobal={useGlobal} totalPax={totalPax}
              empty="" />
          );
        });
      })()}

      {viewMode === 'flat' && (
      <window.TKTableScroll style={{border: 'none', borderRadius: 0, boxShadow: 'none', background: 'transparent'}}>
        <table className="costing-table">
          <thead>
            <tr>
              <th>Dịch vụ</th>
              <th>Nhà cung cấp</th>
              <th>Số lượng</th>
              <th title="Đơn giá tính trên TOÀN ĐOÀN (shared) hay theo từng KHÁCH (pax × N)">Loại giá</th>
              <th className="num">Giá NET</th>
              <th className="num" title="FOC N+1: 1 miễn phí mỗi N suất (vd Anyia 16+1)">FOC</th>
              <th className="num">VAT %</th>
              <th className="num">Markup</th>
              <th className="num">Thành tiền (Sale)</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => {
              const en = effNet(r);
              const hasFoc = r.focRatio && r.focRatio > 0;
              const sale = rowSale(r);
              const focSavings = hasFoc ? (r.priceNet - en) : 0;
              return (
                <tr key={i}>
                  <td>
                    <div className="svc">
                      <span className="svc-icon"><Icon name={SERVICE_TYPES[r.type]?.icon || 'star'} size={14} /></span>
                      <div>
                        <div>{r.service}</div>
                        {hasFoc && (
                          <span style={{fontSize: 10, fontWeight: 700, color: '#10b981',
                            background: '#dcfce7', padding: '1px 6px', borderRadius: 999, marginTop: 2, display: 'inline-block'}}
                            title={`Tiết kiệm ${fmtVND(focSavings)}đ nhờ FOC ${r.focRatio}+1`}>
                            FOC {r.focRatio}+1 · −{fmtVND(focSavings)}đ
                          </span>
                        )}
                      </div>
                    </div>
                  </td>
                  <td>
                    {r.supplier}
                    {r.verified && <span className="verified"><Icon name="checkCircle" size={12} stroke={2.2} /> verified</span>}
                  </td>
                  <td style={{color: 'var(--text-2)'}}>{r.qty}</td>
                  <td>
                    {/* Toggle costType — pill 2 nút. Đổi → priceNet đại lượng đổi nghĩa
                        (toàn đoàn ↔ per pax). KHÔNG auto-convert giá; user tự nhập lại nếu cần. */}
                    {(() => {
                      const ct = rowCostType(r);
                      const Pill = ({val, label, hint}) => (
                        <button onClick={() => updateRow(i, 'costType', val)}
                          title={hint}
                          style={{
                            padding: '2px 8px', fontSize: 10, fontWeight: 700,
                            border: '1px solid ' + (ct === val ? 'var(--accent)' : 'var(--border)'),
                            background: ct === val ? 'var(--accent)' : 'white',
                            color: ct === val ? 'white' : 'var(--text-2)',
                            cursor: 'pointer',
                            borderRadius: 4,
                          }}>
                          {label}
                        </button>
                      );
                      return (
                        <div style={{display: 'inline-flex', gap: 0}}>
                          <Pill val="shared" label="Đoàn" hint="Giá NET cố định toàn đoàn" />
                          <Pill val="pax"    label={`× ${totalPax} Pax`} hint={`Giá NET / khách × ${totalPax} khách`} />
                        </div>
                      );
                    })()}
                  </td>
                  <td className="num">
                    {(() => {
                      const ct = rowCostType(r);
                      // base = giá user nhập (toàn đoàn HAY per pax tuỳ ct)
                      // total = effNet(r) đã áp pax-multiplier + FOC
                      const showTotal = ct === 'pax' || hasFoc;
                      return (
                        <div>
                          <EditableNum value={r.priceNet} formatter={fmtVND}
                            onChange={v => updateRow(i, 'priceNet', v)} />
                          {showTotal && (
                            <div style={{fontSize: 10, color: hasFoc ? '#10b981' : 'var(--text-3)', fontWeight: 600, marginTop: 2}}>
                              {ct === 'pax' ? `× ${totalPax} = ` : 'eff: '}{fmtVND(Math.round(en))}{hasFoc && ' (FOC)'}
                            </div>
                          )}
                        </div>
                      );
                    })()}
                  </td>
                  <td className="num">
                    <EditableNum value={r.focRatio || 0}
                      onChange={v => updateRow(i, 'focRatio', v)}
                      formatter={v => v > 0 ? v + '+1' : '—'} />
                  </td>
                  <td className="num">
                    <EditableNum value={r.vat} suffix="%"
                      onChange={v => updateRow(i, 'vat', v)} />
                  </td>
                  <td className="num markup">
                    <EditableNum value={rowMarkup(r)} suffix="%"
                      onChange={v => updateRow(i, 'markup', v)}
                      formatter={v => '+' + v}
                      disabled={useGlobal} />
                  </td>
                  <td className="num" style={{fontWeight: 700}}>
                    <span className="numeric">{fmtVND(sale)}</span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </window.TKTableScroll>
      )}

      <div style={{padding: '0 28px 28px'}}>
        {/* Tổng bán + per-pax breakdown (4 cards) */}
        <div className="cost-total-bar">
          <div>
            <div className="cost-total-label">TỔNG GIÁ TRỊ TOUR DỰ KIẾN (BÁN)</div>
            <div style={{fontSize: 12, color: 'rgba(255,255,255,0.5)', marginTop: 4}}>
              {totalPax} khách · {request.days}N{request.nights}Đ · {request.route}
            </div>
          </div>
          <div className="cost-total-amount numeric">{fmtVND(totalSale)}</div>
        </div>

        {/* 4 cards: Net/Pax + Sale/Pax NL + Sale/Pax TE + Profit/Pax */}
        <div className="per-pax-row" style={{gap: 10, flexWrap: 'wrap'}}>
          <div className="per-pax-card">
            <div className="per-pax-label">Giá vốn / Pax</div>
            <div className="per-pax-amount numeric" style={{fontSize: 16, color: 'var(--text-2)'}}>{fmtVND(netPerPax)}</div>
            <div style={{fontSize: 10, color: 'var(--text-3)', marginTop: 2}}>Pax quy đổi: {paxEquiv.toFixed(2)} (TE × 0.75)</div>
          </div>
          <div className="per-pax-card">
            <div className="per-pax-label">Giá / Người lớn</div>
            <div className="per-pax-amount numeric">{fmtVND(adultPrice)}</div>
          </div>
          <div className="per-pax-card">
            <div className="per-pax-label">Giá / Trẻ em (75%)</div>
            <div className="per-pax-amount accent numeric">{fmtVND(childPrice)}</div>
          </div>
          <div className="per-pax-card">
            <div className="per-pax-label">Lãi / Pax</div>
            <div className="per-pax-amount numeric" style={{color: profitPerPax >= 0 ? '#10b981' : '#dc2626'}}>{fmtVND(profitPerPax)}</div>
            <div style={{fontSize: 10, color: 'var(--text-3)', marginTop: 2}}>Tổng lãi: {fmtVND(totalProfit)}</div>
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
