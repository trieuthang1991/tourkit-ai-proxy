// components/ncc-tier-picker.jsx — Picker NCC theo 3 tier (3*/4*/5*).
// Dùng cho Step 1.5 Wizard. Load NCC từ /api/v1/ncc/providers?serviceId=1 (Khách sạn).
// User chọn 1 hotel per tier → component load /api/v1/ncc/providers/{id}/services
// để lấy roomTypes + contractPrice → emit hotelOptions = { 3: {...}, 4: {...}, 5: {...} }.
//
// HOTEL_CATEGORY_ID = 1 (verified từ /api/v1/ncc/categories).
// Hotel khác (Khu nghỉ dưỡng / Resort) cùng dùng category 1.

const { useState: _nS, useEffect: _nE, useMemo: _nM, useRef: _nR } = React;

const HOTEL_CATEGORY_ID = 1;
const TIERS = [3, 4, 5];

// Tier label hợp lý cho VN (1*/2* hiếm cho tour, 6* superlux).
const TIER_LABEL = { 3: '3 sao', 4: '4 sao', 5: '5 sao' };
// Hint tier giá tham khảo nếu user chưa chọn NCC (giữ matrix tính được).
const TIER_FALLBACK_VND = { 3: 650_000, 4: 1_150_000, 5: 2_150_000 };

function NccTierPicker({ hotelOptions, setHotelOptions, marketId, pushToast }) {
  const [hotels, setHotels]   = _nS([]);     // list NCC type khách sạn
  const [loading, setLoading] = _nS(true);
  const [error, setError]     = _nS(null);

  // Load NCC khách sạn 1 lần khi mount (cache 60s ở frontend qua state).
  // marketId hiện chưa truyền lên server (provider endpoint chưa filter theo market) →
  // load tất cả + filter client-side khi cần.
  _nE(() => {
    let alive = true;
    setLoading(true); setError(null);
    window.tourkitAuth.authedFetch(`/api/v1/ncc/providers?serviceId=${HOTEL_CATEGORY_ID}`)
      .then(r => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
      .then(d => { if (alive) { setHotels(Array.isArray(d) ? d : []); setLoading(false); } })
      .catch(e => { if (alive) { setError(e.message); setLoading(false); } });
    return () => { alive = false; };
  }, []);

  return (
    <div className="ncc-tier-picker">
      <div className="ntp-head">
        <Icon name="bed" size={15} />
        <div>
          <div className="ntp-title">KHÁCH SẠN THEO TIER</div>
          <div className="ntp-sub">Chọn từ NCC thực — giá tự lấy theo hợp đồng đã ký</div>
        </div>
      </div>

      {error && (
        <div className="ntp-error">
          ⚠ Không tải được NCC: {error}. Sẽ dùng giá tham khảo mặc định.
        </div>
      )}

      <div className="ntp-grid">
        {TIERS.map(star => (
          <NccTierCard
            key={star}
            star={star}
            hotels={hotels}
            loading={loading}
            selected={hotelOptions[star]}
            onChange={(opt) => {
              setHotelOptions(o => ({ ...o, [star]: opt }));
              if (opt) pushToast?.(`Đã chọn ${opt.providerName} cho tier ${star}*`);
            }}
            onClear={() => setHotelOptions(o => { const n = { ...o }; delete n[star]; return n; })}
          />
        ))}
      </div>

      <div className="ntp-foot">
        <Icon name="info" size={11} />
        <span>
          {Object.keys(hotelOptions).length === 0
            ? 'Bỏ trống = dùng giá tham khảo (3* 650k · 4* 1.15tr · 5* 2.15tr/khách/đêm).'
            : `Đã cấu hình ${Object.keys(hotelOptions).length}/3 tier. Báo giá sẽ hiện ${Object.keys(hotelOptions).length} phương án.`}
        </span>
      </div>
    </div>
  );
}

// ── 1 thẻ cấu hình tier ────────────────────────────────────────────────────
// User chọn hotel từ dropdown searchable → component load room types + giá.
function NccTierCard({ star, hotels, loading, selected, onChange, onClear }) {
  const [open, setOpen]       = _nS(false);
  const [search, setSearch]   = _nS('');
  const [rooms, setRooms]     = _nS([]);     // services của hotel đang chọn
  const [loadingR, setLoadR]  = _nS(false);
  const inputRef = _nR(null);

  // Khi selected.providerId đổi → load room types
  _nE(() => {
    if (!selected?.providerId) { setRooms([]); return; }
    let alive = true;
    setLoadR(true);
    window.tourkitAuth.authedFetch(`/api/v1/ncc/providers/${selected.providerId}/services?categoryId=${HOTEL_CATEGORY_ID}`)
      .then(r => r.ok ? r.json() : [])
      .then(d => { if (alive) { setRooms(Array.isArray(d) ? d : []); setLoadR(false); } })
      .catch(() => { if (alive) { setRooms([]); setLoadR(false); } });
    return () => { alive = false; };
  }, [selected?.providerId]);

  const filteredHotels = _nM(() => {
    const q = search.trim().toLowerCase();
    if (!q) return hotels.slice(0, 50);   // top 50 khi chưa search
    return hotels.filter(h =>
      (h.name || '').toLowerCase().includes(q) ||
      (h.city || '').toLowerCase().includes(q) ||
      (h.code || '').toLowerCase().includes(q)
    ).slice(0, 80);
  }, [hotels, search]);

  const pickHotel = (h) => {
    onChange({
      providerId: h.id,
      providerName: h.name,
      city: h.city,
      // roomType + price sẽ set khi user pick room — hoặc auto-pick room đầu tiên có giá
      roomTypeId: null,
      roomTypeName: null,
      pricePerPaxPerNight: null
    });
    setOpen(false);
    setSearch('');
  };

  const pickRoom = (r) => {
    // contractPrice dạng giá phòng/đêm. Quy ước 2 khách/phòng → /pax = /2.
    // User có thể chỉnh thủ công sau. publicPrice fallback nếu contract = 0.
    const roomPrice = r.contractPrice > 0 ? r.contractPrice : r.publicPrice;
    const pricePerPax = Math.round(roomPrice / 2);
    onChange({
      ...selected,
      roomTypeId: r.id,
      roomTypeName: r.name || '(không tên)',
      pricePerPaxPerNight: pricePerPax,
      roomPrice
    });
  };

  return (
    <div className={'ntp-card' + (selected ? ' on' : '')}>
      <div className="ntp-card-head">
        <span className="ntp-card-star">{star}★</span>
        <span className="ntp-card-label">{TIER_LABEL[star]}</span>
        {selected && (
          <button className="ntp-card-clear" onClick={onClear} title="Bỏ chọn tier này">
            <Icon name="close" size={11} />
          </button>
        )}
      </div>

      {!selected ? (
        <button className="ntp-pick-btn" onClick={() => setOpen(true)} disabled={loading}>
          {loading ? 'Đang tải NCC…' : '+ Chọn khách sạn'}
        </button>
      ) : (
        <div className="ntp-selected">
          <div className="ntp-hotel-name" title={selected.providerName}>{selected.providerName}</div>
          {selected.city && <div className="ntp-hotel-city">{selected.city}</div>}

          {selected.roomTypeName && (
            <div className="ntp-room-info">
              <span className="ntp-room-name">{selected.roomTypeName}</span>
              <span className="ntp-room-price">
                {window.fmtVND(selected.pricePerPaxPerNight)}<em>/khách/đêm</em>
              </span>
            </div>
          )}

          {/* Pick room type */}
          {(() => {
            if (loadingR) return <div className="ntp-room-loading">Đang tải bảng giá…</div>;
            // Filter rooms có giá > 0 (contractPrice ưu tiên, fallback publicPrice)
            const pricedRooms = rooms.filter(r =>
              (r.contractPrice > 0) || (r.publicPrice > 0)
            );
            if (pricedRooms.length === 0) {
              return (
                <div className="ntp-room-empty">
                  NCC chưa có bảng giá hợp đồng. <button className="ntp-link" onClick={() => setOpen(true)}>Đổi NCC khác</button>
                </div>
              );
            }
            return (
              <div className="ntp-room-list">
                <div className="ntp-room-list-label">Loại phòng:</div>
                {pricedRooms.slice(0, 6).map(r => {
                  const price = r.contractPrice > 0 ? r.contractPrice : r.publicPrice;
                  const isActive = selected.roomTypeId === r.id;
                  return (
                    <button key={r.id} className={'ntp-room-chip' + (isActive ? ' on' : '')}
                      onClick={() => pickRoom(r)}>
                      <span className="ntp-room-chip-name">{r.name || 'Phòng đôi'}</span>
                      <span className="ntp-room-chip-price">{window.fmtVND(Math.round(price/2))}</span>
                    </button>
                  );
                })}
              </div>
            );
          })()}
        </div>
      )}

      {/* Search dropdown */}
      {open && (
        <div className="ntp-dropdown">
          <div className="ntp-dropdown-search">
            <Icon name="search" size={12} />
            <input
              ref={inputRef}
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Tìm hotel theo tên / mã / thành phố…"
              autoFocus
            />
            <button onClick={() => setOpen(false)}><Icon name="close" size={12} /></button>
          </div>
          <div className="ntp-dropdown-list">
            {filteredHotels.length === 0 ? (
              <div className="ntp-no-match">Không tìm thấy hotel khớp.</div>
            ) : (
              filteredHotels.map(h => (
                <button key={h.id} className="ntp-dropdown-item" onClick={() => pickHotel(h)}>
                  <div className="ntp-dropdown-name">{h.name}</div>
                  <div className="ntp-dropdown-meta">
                    <span className="ntp-dropdown-code">{h.code}</span>
                    {h.city && <span>{h.city}</span>}
                  </div>
                </button>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}

// Helper tính giá fallback khi chưa chọn NCC (cho Step 3 matrix dùng được).
window.nccTierGetPrice = (hotelOptions, star) => {
  const opt = hotelOptions?.[star];
  if (opt?.pricePerPaxPerNight) return opt.pricePerPaxPerNight;
  return TIER_FALLBACK_VND[star] || 1_000_000;
};
window.nccTierGetHotel = (hotelOptions, star) => {
  const opt = hotelOptions?.[star];
  if (opt?.providerName) return opt;
  // Fallback object dùng cho UI hiển thị "giá tham khảo"
  return {
    providerName: `Khách sạn ${star}★ (giá tham khảo)`,
    roomTypeName: 'Phòng đôi',
    pricePerPaxPerNight: TIER_FALLBACK_VND[star],
    isFallback: true
  };
};

window.NccTierPicker = NccTierPicker;
