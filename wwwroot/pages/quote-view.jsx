// pages/quote-view.jsx — Public viewer cho báo giá tour. Route /q/:id.
// KHÔNG cần login (gọi /api/v1/tour-quotes/{id}/public). Khách click link Zalo/SMS từ Sale.
// Layout: hero brand → info strip → pricing showcase → itinerary → bao gồm → contact CTA → footer.
// Print-friendly: hide CTA buttons + print button khi @media print.

function QuoteViewPage({ id }) {
  const [data, setData]       = React.useState(null);
  const [loading, setLoading] = React.useState(true);
  const [err, setErr]         = React.useState(null);

  React.useEffect(() => {
    (async () => {
      setLoading(true); setErr(null);
      try {
        const r = await fetch('/api/v1/tour-quotes/' + encodeURIComponent(id) + '/public');
        const j = await r.json();
        if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
        setData(j.item);
      } catch (e) { setErr(e.message); }
      finally { setLoading(false); }
    })();
  }, [id]);

  const fmtVND = window.fmtVND || (n => (n || 0).toLocaleString('vi-VN') + 'đ');

  if (loading) {
    return <div style={{padding: 80, textAlign: 'center', color: '#6b7280'}}>Đang tải báo giá…</div>;
  }
  if (err || !data) {
    return (
      <div style={{padding: 80, textAlign: 'center'}}>
        <div style={{fontSize: 48, marginBottom: 16}}>📋</div>
        <div style={{fontSize: 18, fontWeight: 600, marginBottom: 8}}>Không tìm thấy báo giá</div>
        <div style={{color: '#6b7280', fontSize: 14}}>{err || 'Báo giá có thể đã bị xóa hoặc link không đúng'}</div>
      </div>
    );
  }

  const d = data.data || {};
  const isWizard = !!(d.request && d.itinerary && d.rows);

  return (
    <div className="qv-root">
      <QuoteStyles />
      <main className="qv-paper">
        {isWizard
          ? <WizardQuoteBody quote={data} d={d} fmtVND={fmtVND} />
          : <SimpleQuoteBody quote={data} d={d} fmtVND={fmtVND} />}

        <QuoteContact quote={data} />

        <footer className="qv-foot">
          <div className="qv-foot-brand">
            <div className="qv-foot-mark">TOURKIT</div>
            <div className="qv-foot-tag">Đối tác du lịch của bạn</div>
          </div>
          <div className="qv-foot-meta">
            <div>Mã báo giá <b>{data.id?.slice(0, 12).toUpperCase() || '—'}</b></div>
            <div>Phát hành {fmtDateVN(data.updatedAt || data.createdAt)}</div>
          </div>
          <button onClick={() => window.print()} className="qv-print no-print">
            In / Lưu PDF
          </button>
        </footer>
      </main>
    </div>
  );
}

// ─── Body: wizard shape (Step 4 → save) ────────────────────────────────────────
function WizardQuoteBody({ quote, d, fmtVND }) {
  const req = d.request || {};
  const itin = d.itinerary || [];
  const rows = d.rows || [];
  const mkt  = d.marketing || {};
  const hotelOpts = d.hotelOptions || {};
  const totalPax = (req.adults || 0) + (req.children || 0);
  const nights = req.nights || Math.max((req.days || 1) - 1, 1);
  const totalSale = rows.reduce((s, r) =>
    s + (r.priceNet || 0) * (1 + (r.vat || 0)/100) * (1 + (r.markup || 0)/100), 0);
  const perPax = Math.round(totalSale / Math.max(totalPax, 1));

  // Tier quotes (nếu có hotelOptions từ Step 1.5 — copy logic từ step4)
  const avgMarkup = (() => {
    const h = rows.find(r => r.type === 'HOTEL');
    return h ? (h.markup || 20) : 20;
  })();
  const nonHotelSale = rows.filter(r => r.type !== 'HOTEL').reduce((s, r) =>
    s + (r.priceNet || 0) * (1 + (r.vat || 0)/100) * (1 + (r.markup || 0)/100), 0);
  const tierQuotes = [3, 4, 5].map(star => {
    const opt = hotelOpts[star];
    if (!opt) return null;
    const hotelSale = (opt.pricePerPaxPerNight || 0) * nights * (1 + avgMarkup/100) * totalPax;
    const total = nonHotelSale + hotelSale;
    return { star, hotel: opt, total, perPax: total / Math.max(totalPax, 1) };
  }).filter(Boolean);
  const hasTiers = tierQuotes.length > 0;

  return (<>
    {/* HERO */}
    <header className="qv-hero">
      <div className="qv-hero-mark">TOURKIT</div>
      <div className="qv-hero-body">
        <div className="qv-eyebrow">Báo giá tour {req.code ? '· ' + req.code : ''}</div>
        <h1 className="qv-title">{mkt.tourName || quote.title || 'Báo giá tour'}</h1>
        {mkt.tagline && (
          <div className="qv-tagline">{mkt.tagline}</div>
        )}
        <div className="qv-hero-meta">{req.days || '?'} ngày {req.nights || nights} đêm</div>
      </div>
    </header>

    {/* INFO STRIP */}
    <section className="qv-info-row">
      <div className="qv-info">
        <div className="qv-info-label">Thời gian</div>
        <div className="qv-info-val">{req.days || '?'}N{req.nights || nights}Đ</div>
      </div>
      {req.route && (
        <div className="qv-info">
          <div className="qv-info-label">Hành trình</div>
          <div className="qv-info-val">{req.route}</div>
        </div>
      )}
      <div className="qv-info">
        <div className="qv-info-label">Đoàn khách</div>
        <div className="qv-info-val">{req.adults || 0} người lớn{req.children > 0 ? ' + ' + req.children + ' trẻ em' : ''}</div>
      </div>
    </section>

    {/* PRICING SHOWCASE (đặt cao, trên itinerary — khách thấy ngay) */}
    {!hasTiers && (
      <section className="qv-pricing">
        <div className="qv-pricing-label">Giá trọn gói / khách</div>
        <div className="qv-pricing-amount">{fmtVND(perPax)}</div>
        <div className="qv-pricing-sub">
          Đoàn {totalPax} khách · Tổng: <b>{fmtVND(Math.round(totalSale))}</b>
        </div>
        <div className="qv-pricing-note">Đã bao gồm VAT · Áp dụng cho khởi hành đã chốt</div>
      </section>
    )}

    {/* TIER CARDS (nếu có hotel options) */}
    {hasTiers && (
      <section className="qv-section">
        <div className="qv-section-head">
          <div className="qv-eyebrow-sub">Phương án giá</div>
          <h2 className="qv-section-title">{tierQuotes.length} hạng khách sạn để lựa chọn</h2>
        </div>
        <div className="qv-tier-grid">
          {tierQuotes.map(q => (
            <div key={q.star} className={'qv-tier qv-tier-' + q.star}>
              <div className="qv-tier-star">{q.star}★</div>
              <div className="qv-tier-name">Khách sạn {q.star} sao</div>
              <div className="qv-tier-price">{fmtVND(Math.round(q.perPax))}</div>
              <div className="qv-tier-unit">/ khách</div>
              <div className="qv-tier-hotel">
                <div className="qv-tier-hotel-label">Lưu trú đề xuất</div>
                <div className="qv-tier-hotel-name">{q.hotel.providerName}</div>
                {q.hotel.roomTypeName && <div className="qv-tier-hotel-room">{q.hotel.roomTypeName}</div>}
              </div>
              <div className="qv-tier-total">Tổng đoàn {totalPax} khách: <b>{fmtVND(Math.round(q.total))}</b></div>
            </div>
          ))}
        </div>
      </section>
    )}

    {/* ITINERARY */}
    {itin.length > 0 && (
      <section className="qv-section">
        <div className="qv-section-head">
          <div className="qv-eyebrow-sub">Lịch trình</div>
          <h2 className="qv-section-title">Hành trình {req.days} ngày {nights} đêm</h2>
        </div>
        <div className="qv-itin">
          {itin.map((day, i) => (
            <div key={i} className="qv-day">
              <div className="qv-day-num">{String(day.day || i + 1).padStart(2, '0')}</div>
              <div className="qv-day-content">
                <h3 className="qv-day-title">{mkt.dayTitles?.[i] || day.title}</h3>
                {(day.activities || []).length > 0 && (
                  <ul className="qv-day-list">
                    {day.activities.map((a, j) => (
                      <li key={j}>
                        <span className="qv-day-time">{a.time}</span>
                        <span className="qv-day-name">{a.title}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </div>
          ))}
        </div>
      </section>
    )}

    {/* DỊCH VỤ BAO GỒM (default checklist — chuẩn tour Việt Nam) */}
    <section className="qv-section">
      <div className="qv-section-head">
        <div className="qv-eyebrow-sub">Dịch vụ</div>
        <h2 className="qv-section-title">Báo giá đã bao gồm</h2>
      </div>
      <div className="qv-incl-grid">
        <IncludeItem title="Lưu trú" text={`${nights} đêm khách sạn theo tiêu chuẩn`} />
        <IncludeItem title="Phương tiện" text="Xe du lịch đời mới · máy bay khứ hồi (nếu có)" />
        <IncludeItem title="Ăn uống" text="Theo chương trình · đặc sản địa phương" />
        <IncludeItem title="Hướng dẫn viên" text="HDV chuyên nghiệp suốt tuyến" />
        <IncludeItem title="Vé tham quan" text="Vé vào cửa các điểm trong lịch trình" />
        <IncludeItem title="Bảo hiểm du lịch" text="Bảo hiểm tai nạn toàn tuyến" />
      </div>
    </section>
  </>);
}

// ─── Body: tour-builder shape (Trang "Tính giá Tour") ──────────────────────────
function SimpleQuoteBody({ quote, d, fmtVND }) {
  const totalPax = (quote.adultCount || 0) + (quote.childCount || 0);
  const perPax = totalPax > 0 ? Math.round((quote.totalRevenue || 0) / totalPax) : 0;

  return (<>
    <header className="qv-hero">
      <div className="qv-hero-mark">TOURKIT</div>
      <div className="qv-hero-body">
        <div className="qv-eyebrow">Báo giá tour</div>
        <h1 className="qv-title">{quote.title || 'Báo giá tour'}</h1>
        {quote.marketName && <div className="qv-tagline">{quote.marketName}</div>}
        {(quote.startDate || quote.endDate) && (
          <div className="qv-hero-meta">{quote.startDate || ''}{quote.endDate ? ' → ' + quote.endDate : ''}</div>
        )}
      </div>
    </header>

    <section className="qv-info-row">
      {quote.startDate && (
        <div className="qv-info">
          <div className="qv-info-label">Khởi hành</div>
          <div className="qv-info-val">{quote.startDate}</div>
        </div>
      )}
      {quote.endDate && (
        <div className="qv-info">
          <div className="qv-info-label">Kết thúc</div>
          <div className="qv-info-val">{quote.endDate}</div>
        </div>
      )}
      <div className="qv-info">
        <div className="qv-info-label">Đoàn khách</div>
        <div className="qv-info-val">{quote.adultCount || 0} NL{quote.childCount > 0 ? ' + ' + quote.childCount + ' TE' : ''}</div>
      </div>
    </section>

    <section className="qv-pricing">
      <div className="qv-pricing-label">Giá trọn gói / khách</div>
      <div className="qv-pricing-amount">{fmtVND(perPax)}</div>
      <div className="qv-pricing-sub">
        Đoàn {totalPax} khách · Tổng: <b>{fmtVND(quote.totalRevenue || 0)}</b>
      </div>
      <div className="qv-pricing-note">Đã bao gồm VAT</div>
    </section>

    {Array.isArray(d.services) && d.services.length > 0 && (
      <section className="qv-section">
        <div className="qv-section-head">
          <div className="qv-eyebrow-sub">Dịch vụ</div>
          <h2 className="qv-section-title">Báo giá đã bao gồm</h2>
        </div>
        <div className="qv-incl-grid">
          {d.services.slice(0, 8).map((s, i) => (
            <IncludeItem key={i} title={s.name || s.label || s.title || 'Dịch vụ'} text={s.note || ''} />
          ))}
        </div>
      </section>
    )}
  </>);
}

// ─── Subcomponents ──────────────────────────────────────────────────────────────
function IncludeItem({ title, text }) {
  return (
    <div className="qv-incl">
      <div className="qv-incl-check">✓</div>
      <div>
        <div className="qv-incl-title">{title}</div>
        {text && <div className="qv-incl-text">{text}</div>}
      </div>
    </div>
  );
}

function QuoteContact({ quote }) {
  // Sales contact: nếu có customerPhone/Name lưu trong báo giá (KH đặt) hoặc createdBy (nhân viên sale)
  // Cho v1 ưu tiên hiện CTA gọi/Zalo — số điện thoại tổng đài; cá nhân hóa lần sau.
  const salesPhone = '1900 1234';   // TODO: lấy từ tenant config
  const salesName  = quote.createdBy || 'Đội tư vấn TOURKIT';
  return (
    <section className="qv-cta no-print">
      <div className="qv-cta-body">
        <div className="qv-cta-eyebrow">Cần tư vấn thêm?</div>
        <h2 className="qv-cta-title">Liên hệ trực tiếp với {salesName}</h2>
        <div className="qv-cta-sub">Chốt nhanh trong 5 phút · báo giá có thể tùy chỉnh theo nhu cầu của Anh/Chị</div>
      </div>
      <div className="qv-cta-actions">
        <a href={'tel:' + salesPhone.replace(/\s/g, '')} className="qv-cta-btn qv-cta-btn-primary">
          Gọi {salesPhone}
        </a>
        <a href={'https://zalo.me/' + salesPhone.replace(/\s/g, '')} target="_blank" rel="noopener" className="qv-cta-btn qv-cta-btn-ghost">
          Zalo
        </a>
      </div>
    </section>
  );
}

// ─── Helpers ────────────────────────────────────────────────────────────────────
function fmtDateVN(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d)) return iso;
  return d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

// ─── Styles (inject once, scoped với `.qv-` prefix) ────────────────────────────
function QuoteStyles() {
  return (
    <style>{`
      .qv-root {
        min-height: 100vh;
        background: #f4f1ec;     /* nền giấy ấm — giúp paper trắng nổi lên */
        padding: 32px 16px;
        font-family: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
        color: #1a1a1a;
        -webkit-font-smoothing: antialiased;
      }
      .qv-paper {
        max-width: 880px;
        margin: 0 auto;
        background: #fff;
        border-radius: 16px;
        box-shadow: 0 1px 2px rgba(0,0,0,0.04), 0 16px 48px rgba(15, 23, 42, 0.08);
        overflow: hidden;
      }

      /* ── Hero ── */
      .qv-hero {
        background: linear-gradient(135deg, #1c1815 0%, #2a1f17 100%);
        color: #fff;
        padding: 56px 56px 48px;
        position: relative;
      }
      .qv-hero-mark {
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.28em;
        color: #F97316;
        margin-bottom: 32px;
      }
      .qv-hero-body { max-width: 640px; }
      .qv-eyebrow {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.16em;
        text-transform: uppercase;
        color: #F97316;
        margin-bottom: 12px;
      }
      .qv-title {
        font-size: 38px;
        font-weight: 700;
        line-height: 1.1;
        letter-spacing: -0.02em;
        margin: 0 0 12px;
      }
      .qv-tagline {
        font-size: 15px;
        color: rgba(255,255,255,0.75);
        line-height: 1.5;
        margin-bottom: 8px;
        font-weight: 400;
      }
      .qv-hero-meta {
        font-size: 13px;
        color: rgba(255,255,255,0.55);
        letter-spacing: 0.05em;
        margin-top: 16px;
      }

      /* ── Info strip ── */
      .qv-info-row {
        display: grid;
        grid-template-columns: repeat(3, 1fr);
        border-bottom: 1px solid #ece6db;
      }
      .qv-info {
        padding: 22px 28px;
        border-right: 1px solid #ece6db;
      }
      .qv-info:last-child { border-right: none; }
      .qv-info-label {
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        color: #9ca3af;
        margin-bottom: 6px;
      }
      .qv-info-val {
        font-size: 14px;
        font-weight: 600;
        color: #1a1a1a;
      }

      /* ── Pricing showcase ── */
      .qv-pricing {
        text-align: center;
        padding: 44px 32px 40px;
        background: linear-gradient(180deg, #fff 0%, #fffbf5 100%);
        border-bottom: 1px solid #ece6db;
      }
      .qv-pricing-label {
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.16em;
        text-transform: uppercase;
        color: #9ca3af;
        margin-bottom: 8px;
      }
      .qv-pricing-amount {
        font-size: 52px;
        font-weight: 700;
        line-height: 1;
        letter-spacing: -0.02em;
        color: #F97316;
        margin-bottom: 10px;
        font-variant-numeric: tabular-nums;
      }
      .qv-pricing-sub {
        font-size: 14px;
        color: #4b5563;
        margin-bottom: 4px;
      }
      .qv-pricing-sub b { color: #1a1a1a; font-weight: 700; }
      .qv-pricing-note {
        font-size: 11px;
        color: #9ca3af;
        font-style: italic;
        margin-top: 8px;
      }

      /* ── Section header ── */
      .qv-section { padding: 40px 56px; border-bottom: 1px solid #ece6db; }
      .qv-section:last-of-type { border-bottom: none; }
      .qv-section-head { margin-bottom: 24px; }
      .qv-eyebrow-sub {
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: #F97316;
        margin-bottom: 6px;
      }
      .qv-section-title {
        font-size: 22px;
        font-weight: 700;
        letter-spacing: -0.01em;
        line-height: 1.3;
        margin: 0;
        color: #1a1a1a;
      }

      /* ── Itinerary ── */
      .qv-itin { display: flex; flex-direction: column; gap: 18px; }
      .qv-day { display: flex; gap: 20px; }
      .qv-day-num {
        flex: 0 0 44px;
        height: 44px;
        background: #1c1815;
        color: #F97316;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 14px;
        font-weight: 700;
        font-variant-numeric: tabular-nums;
      }
      .qv-day-content { flex: 1; padding-top: 8px; }
      .qv-day-title {
        font-size: 15px;
        font-weight: 700;
        letter-spacing: 0.02em;
        margin: 0 0 10px;
        color: #1a1a1a;
        text-transform: uppercase;
      }
      .qv-day-list { list-style: none; padding: 0; margin: 0; }
      .qv-day-list li {
        display: flex;
        gap: 14px;
        font-size: 13.5px;
        line-height: 1.6;
        padding: 4px 0;
        color: #374151;
      }
      .qv-day-time {
        flex: 0 0 56px;
        color: #F97316;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
      }
      .qv-day-name { flex: 1; }

      /* ── Tier cards ── */
      .qv-tier-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
        gap: 14px;
      }
      .qv-tier {
        border: 1px solid #ece6db;
        border-radius: 12px;
        padding: 22px 20px;
        background: #fff;
        text-align: center;
      }
      .qv-tier-5 { border-color: #F97316; background: linear-gradient(180deg, #fffbf5 0%, #fff 60%); }
      .qv-tier-star {
        font-size: 14px;
        color: #F97316;
        font-weight: 700;
        letter-spacing: 0.05em;
        margin-bottom: 4px;
      }
      .qv-tier-name {
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        color: #6b7280;
        margin-bottom: 16px;
      }
      .qv-tier-price {
        font-size: 28px;
        font-weight: 700;
        color: #1a1a1a;
        font-variant-numeric: tabular-nums;
        line-height: 1;
      }
      .qv-tier-unit { font-size: 11px; color: #9ca3af; margin-bottom: 18px; margin-top: 4px; }
      .qv-tier-hotel {
        padding: 12px 0;
        border-top: 1px solid #ece6db;
        border-bottom: 1px solid #ece6db;
        margin-bottom: 12px;
      }
      .qv-tier-hotel-label {
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.1em;
        color: #9ca3af;
        margin-bottom: 4px;
      }
      .qv-tier-hotel-name {
        font-size: 13px;
        font-weight: 600;
        color: #1a1a1a;
        line-height: 1.4;
      }
      .qv-tier-hotel-room { font-size: 11px; color: #6b7280; margin-top: 2px; }
      .qv-tier-total {
        font-size: 11px;
        color: #6b7280;
      }
      .qv-tier-total b { color: #1a1a1a; font-weight: 700; }

      /* ── Bao gồm grid (2-col) ── */
      .qv-incl-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 16px 24px;
      }
      .qv-incl {
        display: flex;
        gap: 12px;
        align-items: flex-start;
      }
      .qv-incl-check {
        flex: 0 0 22px;
        height: 22px;
        border-radius: 50%;
        background: #fff7ed;
        color: #F97316;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 12px;
        font-weight: 700;
        margin-top: 2px;
      }
      .qv-incl-title { font-size: 13.5px; font-weight: 600; color: #1a1a1a; }
      .qv-incl-text { font-size: 12px; color: #6b7280; margin-top: 2px; line-height: 1.5; }

      /* ── CTA contact ── */
      .qv-cta {
        background: linear-gradient(135deg, #1c1815 0%, #2a1f17 100%);
        color: #fff;
        padding: 36px 56px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 32px;
        flex-wrap: wrap;
      }
      .qv-cta-eyebrow {
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: #F97316;
        margin-bottom: 8px;
      }
      .qv-cta-title {
        font-size: 20px;
        font-weight: 700;
        margin: 0 0 6px;
        letter-spacing: -0.01em;
      }
      .qv-cta-sub {
        font-size: 13px;
        color: rgba(255,255,255,0.7);
        line-height: 1.5;
        max-width: 480px;
      }
      .qv-cta-actions { display: flex; gap: 10px; flex-shrink: 0; }
      .qv-cta-btn {
        padding: 12px 22px;
        border-radius: 999px;
        font-size: 14px;
        font-weight: 700;
        text-decoration: none;
        display: inline-block;
        transition: transform 0.12s ease;
      }
      .qv-cta-btn:active { transform: translateY(1px); }
      .qv-cta-btn-primary {
        background: #F97316;
        color: #fff;
      }
      .qv-cta-btn-primary:hover { background: #ea670d; }
      .qv-cta-btn-ghost {
        background: rgba(255,255,255,0.1);
        color: #fff;
        border: 1px solid rgba(255,255,255,0.18);
      }
      .qv-cta-btn-ghost:hover { background: rgba(255,255,255,0.18); }

      /* ── Footer ── */
      .qv-foot {
        padding: 22px 32px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 16px;
        flex-wrap: wrap;
        font-size: 12px;
        color: #9ca3af;
        background: #faf7f1;
      }
      .qv-foot-brand { display: flex; flex-direction: column; gap: 2px; }
      .qv-foot-mark {
        font-size: 12px;
        font-weight: 700;
        letter-spacing: 0.18em;
        color: #1a1a1a;
      }
      .qv-foot-tag { font-size: 11px; color: #9ca3af; }
      .qv-foot-meta { font-size: 11px; line-height: 1.5; text-align: right; }
      .qv-foot-meta b { color: #1a1a1a; font-weight: 600; font-variant-numeric: tabular-nums; }
      .qv-print {
        padding: 8px 18px;
        border: 1px solid #d1d5db;
        background: #fff;
        border-radius: 999px;
        font-size: 12.5px;
        font-weight: 600;
        cursor: pointer;
        color: #1a1a1a;
      }
      .qv-print:hover { background: #f9fafb; }

      /* ── Responsive (single column < 768px) ── */
      @media (max-width: 768px) {
        .qv-root { padding: 16px 8px; }
        .qv-paper { border-radius: 12px; }
        .qv-hero { padding: 36px 24px 32px; }
        .qv-title { font-size: 26px; }
        .qv-info-row { grid-template-columns: 1fr; }
        .qv-info { border-right: none; border-bottom: 1px solid #ece6db; }
        .qv-info:last-child { border-bottom: none; }
        .qv-section { padding: 28px 24px; }
        .qv-pricing { padding: 32px 20px; }
        .qv-pricing-amount { font-size: 38px; }
        .qv-incl-grid { grid-template-columns: 1fr; }
        .qv-cta { padding: 28px 24px; flex-direction: column; align-items: flex-start; }
        .qv-cta-actions { width: 100%; }
        .qv-cta-btn { flex: 1; text-align: center; }
        .qv-foot { padding: 18px 20px; }
        .qv-foot-meta { text-align: left; }
      }

      /* ── Print ── */
      @media print {
        .qv-root { background: #fff; padding: 0; }
        .qv-paper { box-shadow: none; border-radius: 0; max-width: 100%; }
        .no-print { display: none !important; }
        .qv-hero { padding: 32px 40px 28px; }
        .qv-section { padding: 28px 40px; }
        .qv-day { break-inside: avoid; }
        .qv-tier { break-inside: avoid; }
      }
    `}</style>
  );
}

window.QuoteViewPage   = QuoteViewPage;
window.WizardQuoteBody = WizardQuoteBody;     // step4 sale preview dùng chung body
window.QuoteStyles     = QuoteStyles;
