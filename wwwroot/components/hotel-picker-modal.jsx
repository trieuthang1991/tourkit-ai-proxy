// components/hotel-picker-modal.jsx — Modal chọn 1 hotel NCC cho 1 activity Step 2.
// Khác NccTierPicker: chọn 1 hotel cụ thể (không phân tier), filter theo pax đoàn,
// tự tính cost theo công thức pricePerPax × pax (1 đêm). User dùng cho dòng HOTEL
// trong itinerary Step 2 để THAY thế tên/giá AI bịa.
//
// Mở qua: openHotelPicker({ pax, currentTitle, onPick: (data) => ... })
// Đóng tự động sau khi user xác nhận pick.

const { useState: _hpS, useEffect: _hpE, useMemo: _hpM } = React;

const HOTEL_CATEGORY_ID_HP = 1;

function HotelPickerModal({ open, pax, currentTitle, onPick, onClose }) {
  const [hotels, setHotels]     = _hpS([]);
  const [loadingH, setLoadingH] = _hpS(true);
  const [errorH, setErrorH]     = _hpS(null);
  const [search, setSearch]     = _hpS('');
  const [picked, setPicked]     = _hpS(null);   // {provider, rooms?, pickedRoom?}

  // Reset state khi mở/đóng
  _hpE(() => {
    if (!open) { setPicked(null); setSearch(''); return; }
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

  if (!open) return null;

  const handleConfirm = (room) => {
    const roomPrice = room.contractPrice > 0 ? room.contractPrice : room.publicPrice;
    // pricePerPax giả định 2 khách/phòng. Tổng = pricePerPax × pax (1 đêm).
    const pricePerPax = Math.round(roomPrice / 2);
    onPick({
      title: picked.provider.name,
      supplier: picked.provider.name,
      supplierId: picked.provider.id,
      roomTypeId: room.id,
      roomTypeName: room.name || 'Phòng đôi',
      pricePerPaxPerNight: pricePerPax,
      cost: pricePerPax * (pax || 1),    // cost cho 1 đêm
      description: `${room.name || 'Phòng đôi'} · ${window.fmtVND(pricePerPax)}/khách/đêm`
    });
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
            <div className="hpm-room-list">
              {picked.loadingR ? <div className="hpm-loading">Đang tải bảng giá hợp đồng…</div>
              : !picked.rooms || picked.rooms.length === 0 ? (
                <div className="hpm-empty">
                  NCC chưa có bảng giá hợp đồng cho hạng mục Khách sạn.
                  <button className="hpm-link" onClick={() => setPicked(null)}>Đổi NCC khác</button>
                </div>
              ) : picked.rooms.map(r => {
                const roomPrice = r.contractPrice > 0 ? r.contractPrice : r.publicPrice;
                const perPax = Math.round(roomPrice / 2);
                const total = perPax * (pax || 1);
                return (
                  <button key={r.id} className="hpm-room" onClick={() => handleConfirm(r)}>
                    <div className="hpm-room-info">
                      <div className="hpm-room-name">{r.name || 'Phòng đôi'}</div>
                      <div className="hpm-room-unit">
                        {window.fmtVND(perPax)}/khách/đêm · 2 khách/phòng
                      </div>
                    </div>
                    <div className="hpm-room-total">
                      <div className="hpm-room-total-val">{window.fmtVND(total)}</div>
                      <div className="hpm-room-total-label">cho {pax || 1} khách / 1 đêm</div>
                    </div>
                  </button>
                );
              })}
            </div>
          </>
        )}
      </div>
    </div>,
    document.body
  );
}

window.HotelPickerModal = HotelPickerModal;
