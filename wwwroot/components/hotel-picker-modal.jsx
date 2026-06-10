// components/hotel-picker-modal.jsx — Modal pick hotel + ROOM PACK cho 1 activity Step 2.
// Flow v3:
//   1. List NCC hotel → user pick 1 hotel
//   2. Load room types có giá hợp đồng
//   3. Auto-suggest pack: qty mỗi loại phòng dựa trên pax
//      (default: phòng đôi capacity 2, pax lẻ → 1 phòng đơn + còn lại đôi)
//   4. User chỉnh qty inline → total/đêm update realtime
//   5. Confirm → activity.cost = total × 1 đêm, supplier = hotel name
//
// Rule pack mặc định (helper computeDefaultPack):
//   - Phòng "đôi"/"đôi"/"double" → capacity 2
//   - Phòng "đơn"/"single"      → capacity 1
//   - Phòng "tam"/"triple"      → capacity 3
//   - Phòng "tứ"/"family"/"quad"→ capacity 4
//   - Khác                       → capacity 2 (fallback)
//   - Algorithm: chọn loại có capacity gần nhất với pax/N, tối ưu chia hết
//   - Pax lẻ + không có phòng đơn → 1 phòng "đôi" có giường phụ (qty không đổi)

const { useState: _hpS, useEffect: _hpE, useMemo: _hpM } = React;

const HOTEL_CATEGORY_ID_HP = 1;

// ── Helpers ─────────────────────────────────────────────────────────────────
// Capacity từ tên phòng (heuristic — sales chỉnh sau cũng được).
function roomCapacity(name) {
  const s = String(name || '').toLowerCase();
  if (/đơn|single/.test(s)) return 1;
  if (/tam|triple|3\s*ng/.test(s)) return 3;
  if (/(tứ|quad|family|4\s*ng)/.test(s)) return 4;
  // Default: phòng đôi / double / standard / deluxe → 2
  return 2;
}
function roomPrice(r) {
  return r.contractPrice > 0 ? r.contractPrice : (r.publicPrice || 0);
}
// Auto-pack: chia pax thành room qty tối ưu (ưu tiên phòng đôi).
function computeDefaultPack(pricedRooms, pax) {
  if (!pricedRooms.length) return {};
  // Tìm room "đôi" capacity 2 — primary
  const double = pricedRooms.find(r => roomCapacity(r.name) === 2);
  const single = pricedRooms.find(r => roomCapacity(r.name) === 1);
  const pack = {};
  pricedRooms.forEach(r => { pack[r.id] = 0; });
  if (!double && !single) {
    // Fallback: dùng room đầu tiên
    pack[pricedRooms[0].id] = Math.ceil(pax / roomCapacity(pricedRooms[0].name));
    return pack;
  }
  if (!double) {
    // Chỉ có đơn → qty = pax
    pack[single.id] = pax;
    return pack;
  }
  // Có phòng đôi (và có thể có đơn): pack tối ưu
  if (pax % 2 === 0 || !single) {
    pack[double.id] = Math.ceil(pax / 2);
  } else {
    pack[single.id] = 1;
    pack[double.id] = (pax - 1) / 2;
  }
  return pack;
}

function HotelPickerModal({ open, pax, currentTitle, hotelCount, currentDayNum, onPick, onClose }) {
  const [hotels, setHotels]     = _hpS([]);
  const [loadingH, setLoadingH] = _hpS(true);
  const [errorH, setErrorH]     = _hpS(null);
  const [search, setSearch]     = _hpS('');
  const [picked, setPicked]     = _hpS(null);   // {provider, rooms?, pickedRoom?}
  // Scope: 'single' = chỉ activity này, 'all' = tất cả HOTEL activity trong tour.
  // Default 'all' vì tour đoàn thường ở 1 hotel xuyên suốt.
  const [scope, setScope]       = _hpS('all');

  // Reset state khi mở/đóng
  _hpE(() => {
    if (!open) { setPicked(null); setSearch(''); setScope('all'); return; }
    setLoadingH(true); setErrorH(null);
    window.tourkitAuth.authedFetch(`/api/v1/ncc/providers?serviceId=${HOTEL_CATEGORY_ID_HP}`)
      .then(r => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
      .then(d => { setHotels(Array.isArray(d) ? d : []); setLoadingH(false); })
      .catch(e => { setErrorH(e.message); setLoadingH(false); });
  }, [open]);

  // Khi pick provider → load rooms
  _hpE(() => {
    if (!picked?.provider?.id) return;
    setPicked(p => ({ ...p, rooms: null, loadingR: true }));
    window.tourkitAuth.authedFetch(`/api/v1/ncc/providers/${picked.provider.id}/services?categoryId=${HOTEL_CATEGORY_ID_HP}`)
      .then(r => r.ok ? r.json() : [])
      .then(d => {
        const priced = (Array.isArray(d) ? d : []).filter(r =>
          (r.contractPrice > 0) || (r.publicPrice > 0));
        setPicked(p => ({ ...p, rooms: priced, loadingR: false }));
      })
      .catch(() => setPicked(p => ({ ...p, rooms: [], loadingR: false })));
  }, [picked?.provider?.id]);

  const filtered = _hpM(() => {
    const q = search.trim().toLowerCase();
    if (!q) return hotels.slice(0, 80);
    return hotels.filter(h =>
      (h.name || '').toLowerCase().includes(q) ||
      (h.city || '').toLowerCase().includes(q) ||
      (h.code || '').toLowerCase().includes(q)
    ).slice(0, 120);
  }, [hotels, search]);

  // Pack qty state — { roomId: qty } cho phòng có giá > 0 ở picked hotel.
  const [pack, setPack] = _hpS({});
  // Khi picked rooms load xong → auto-compute default pack theo pax.
  _hpE(() => {
    if (!picked?.rooms) return;
    const priced = picked.rooms.filter(r => roomPrice(r) > 0);
    if (priced.length === 0) { setPack({}); return; }
    setPack(computeDefaultPack(priced, pax || 1));
  }, [picked?.rooms, pax]);

  if (!open) return null;

  // Tính total/đêm theo pack hiện tại.
  const pricedRooms = (picked?.rooms || []).filter(r => roomPrice(r) > 0);
  const totalPerNight = pricedRooms.reduce((s, r) => s + roomPrice(r) * (pack[r.id] || 0), 0);
  const totalCapacity = pricedRooms.reduce((s, r) => s + roomCapacity(r.name) * (pack[r.id] || 0), 0);
  const overflow      = totalCapacity < (pax || 1);
  const surplus       = totalCapacity > (pax || 1);
  const pricePerPax   = (pax || 1) > 0 ? totalPerNight / (pax || 1) : 0;

  const handleConfirm = () => {
    // Build description ngắn: "5 phòng đôi + 1 phòng đơn"
    const parts = pricedRooms.filter(r => (pack[r.id] || 0) > 0)
      .map(r => `${pack[r.id]} ${(r.name || 'Phòng đôi').toLowerCase()}`);
    onPick({
      title: picked.provider.name,
      supplier: picked.provider.name,
      supplierId: picked.provider.id,
      pack: pack,   // { roomId: qty }
      packDescription: parts.join(' + '),
      pricePerPaxPerNight: Math.round(pricePerPax),
      cost: Math.round(totalPerNight),   // tổng / 1 đêm
      description: `${parts.join(' + ')} · ${window.fmtVND(Math.round(totalPerNight))}/đêm`
    }, scope);   // ← truyền scope ('single' | 'all') cho parent xử lý
    onClose();
  };

  // Render bằng Portal để không bị clip
  return ReactDOM.createPortal(
    <div className="hpm-backdrop" onClick={onClose}>
      <div className="hpm-modal" onClick={e => e.stopPropagation()}>
        <div className="hpm-head">
          <div>
            <div className="hpm-title">Chọn khách sạn từ NCC</div>
            <div className="hpm-sub">
              Đang chọn cho <strong>{pax || 1} khách</strong>
              {currentTitle && <> · thay thế <em>"{currentTitle}"</em></>}
            </div>
          </div>
          <button className="hpm-close" onClick={onClose}><Icon name="close" size={14} /></button>
        </div>

        {!picked ? (
          <>
            <div className="hpm-search">
              <Icon name="search" size={13} />
              <input value={search} onChange={e => setSearch(e.target.value)}
                placeholder="Tìm hotel theo tên / mã / thành phố…" autoFocus />
            </div>
            <div className="hpm-list">
              {loadingH ? <div className="hpm-loading">Đang tải danh sách NCC…</div>
              : errorH ? <div className="hpm-error">⚠ {errorH}</div>
              : filtered.length === 0 ? <div className="hpm-empty">Không tìm thấy hotel khớp</div>
              : filtered.map(h => (
                <button key={h.id} className="hpm-item"
                  onClick={() => setPicked({ provider: h })}>
                  <div className="hpm-item-name">{h.name}</div>
                  <div className="hpm-item-meta">
                    <span className="hpm-code">{h.code}</span>
                    {h.city && <span>{h.city}</span>}
                  </div>
                </button>
              ))}
            </div>
          </>
        ) : (
          <>
            <div className="hpm-back-row">
              <button className="hpm-back" onClick={() => setPicked(null)}>
                <Icon name="arrowLeft" size={12} /> Đổi NCC khác
              </button>
              <div className="hpm-picked-name">{picked.provider.name}</div>
            </div>
            <div className="hpm-pack">
              {picked.loadingR ? <div className="hpm-loading">Đang tải bảng giá hợp đồng…</div>
              : pricedRooms.length === 0 ? (
                <div className="hpm-empty">
                  NCC chưa có bảng giá hợp đồng cho hạng mục Khách sạn.
                  <button className="hpm-link" onClick={() => setPicked(null)}>Đổi NCC khác</button>
                </div>
              ) : (
                <>
                  <div className="hpm-pack-label">
                    Pack phòng — gợi ý {pax || 1} khách (sửa qty nếu cần):
                  </div>
                  {pricedRooms.map(r => {
                    const price = roomPrice(r);
                    const cap   = roomCapacity(r.name);
                    const qty   = pack[r.id] || 0;
                    const subTotal = price * qty;
                    return (
                      <div key={r.id} className="hpm-pack-row">
                        <div className="hpm-pack-info">
                          <div className="hpm-pack-name">{r.name || 'Phòng đôi'}</div>
                          <div className="hpm-pack-meta">
                            <span>{window.fmtVND(price)}/phòng/đêm</span>
                            <span>·</span>
                            <span>{cap} khách/phòng</span>
                          </div>
                        </div>
                        <div className="hpm-pack-qty">
                          <button className="hpm-qty-btn" disabled={qty <= 0}
                            onClick={() => setPack(p => ({ ...p, [r.id]: Math.max(0, (p[r.id] || 0) - 1) }))}>
                            −
                          </button>
                          <input className="hpm-qty-num" type="number" min={0} value={qty}
                            onChange={e => setPack(p => ({ ...p, [r.id]: Math.max(0, parseInt(e.target.value) || 0) }))} />
                          <button className="hpm-qty-btn"
                            onClick={() => setPack(p => ({ ...p, [r.id]: (p[r.id] || 0) + 1 }))}>
                            +
                          </button>
                        </div>
                        <div className="hpm-pack-sub">
                          {qty > 0 ? window.fmtVND(subTotal) : '—'}
                        </div>
                      </div>
                    );
                  })}
                  {/* Total bar — sticky bottom */}
                  <div className="hpm-pack-summary">
                    <div className="hpm-pack-cap-row">
                      <span className={'hpm-pack-cap' + (overflow ? ' bad' : surplus ? ' warn' : ' ok')}>
                        {overflow ? '⚠ Thiếu' : surplus ? 'Dư' : '✓ Khớp'} chỗ:
                        <strong> {totalCapacity}/{pax || 1} khách</strong>
                      </span>
                      <span className="hpm-pack-per-pax">
                        {window.fmtVND(Math.round(pricePerPax))}<em>/khách/đêm</em>
                      </span>
                    </div>
                    <div className="hpm-pack-total-row">
                      <span className="hpm-pack-total-label">Tổng / 1 đêm</span>
                      <span className="hpm-pack-total-val">{window.fmtVND(totalPerNight)}</span>
                    </div>

                    {/* SCOPE: áp dụng cho 1 ngày hay tất cả đêm HOTEL trong tour */}
                    <div className="hpm-scope">
                      <div className="hpm-scope-label">PHẠM VI ÁP DỤNG</div>
                      <label className={'hpm-scope-opt' + (scope === 'all' ? ' on' : '')}>
                        <input type="radio" name="hpm-scope" checked={scope === 'all'}
                          onChange={() => setScope('all')} />
                        <div className="hpm-scope-text">
                          <div className="hpm-scope-title">Tất cả đêm HOTEL trong tour</div>
                          <div className="hpm-scope-sub">
                            Áp pack này cho {hotelCount || 1} HOTEL activity
                            {hotelCount > 1 ? ` (tổng ${window.fmtVND(totalPerNight * (hotelCount || 1))})` : ''}
                          </div>
                        </div>
                      </label>
                      <label className={'hpm-scope-opt' + (scope === 'single' ? ' on' : '')}>
                        <input type="radio" name="hpm-scope" checked={scope === 'single'}
                          onChange={() => setScope('single')} />
                        <div className="hpm-scope-text">
                          <div className="hpm-scope-title">Chỉ ngày này{currentDayNum ? ` (Ngày ${currentDayNum})` : ''}</div>
                          <div className="hpm-scope-sub">
                            Chỉ áp pack cho activity hiện tại — đêm khác giữ nguyên
                          </div>
                        </div>
                      </label>
                    </div>

                    <button className="hpm-confirm"
                      onClick={handleConfirm}
                      disabled={totalPerNight === 0 || overflow}
                      title={overflow ? 'Pack thiếu chỗ — thêm phòng đi' : ''}>
                      ✓ Áp dụng {scope === 'all' ? `cho tất cả ${hotelCount || 1} đêm` : 'cho ngày này'}
                    </button>
                  </div>
                </>
              )}
            </div>
          </>
        )}
      </div>
    </div>,
    document.body
  );
}

window.HotelPickerModal = HotelPickerModal;
