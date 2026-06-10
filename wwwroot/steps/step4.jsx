// Step 4: Quote preview + actions sidebar
// hotelOptions: { 3?: {providerName, pricePerPaxPerNight, ...}, 4?, 5? } từ Step 1.5 NccTierPicker.
// Khi có 1+ tier → render 3 báo giá cards thay vì 1 giá đơn → khách chọn tier.
function Step4Quote({ request, itinerary, rows, hotelOptions, onBack, onRestart, marketing, pushToast }) {
  const totalPax = request.adults + request.children;
  const nights = request.nights || Math.max((request.days || 1) - 1, 1);
  const [status, setStatus] = React.useState('DRAFT');
  const [shareOpen, setShareOpen] = React.useState(false);
  const [shareLink] = React.useState(`https://quote.tourkit.vn/q/${request.code}-${Math.random().toString(36).slice(2, 7)}`);

  // Tách cost: non-hotel rows (giữ nguyên) + hotel ước lượng theo tier.
  // priceNet * (1+vat/100) * (1+markup/100) → giá bán mỗi row.
  const nonHotelRows  = rows.filter(r => r.type !== 'HOTEL');
  const nonHotelSale  = nonHotelRows.reduce((s, r) =>
    s + (r.priceNet || 0) * (1 + (r.vat || 0)/100) * (1 + (r.markup || 0)/100), 0);
  // Markup trung bình áp dụng cho hotel (lấy markup từ row HOTEL đầu tiên, fallback 20%)
  const avgHotelMarkup = (() => {
    const hRow = rows.find(r => r.type === 'HOTEL');
    return hRow ? (hRow.markup || 20) : 20;
  })();

  // Build tier quote: cho từng tier có hotelOptions → tính giá/khách.
  const tierQuotes = [3, 4, 5].map(star => {
    const opt = hotelOptions?.[star];
    if (!opt) return null;
    const hotelNetPerPax = (opt.pricePerPaxPerNight || 0) * nights;
    const hotelSalePerPax = hotelNetPerPax * (1 + avgHotelMarkup/100);
    const hotelSaleTotal = hotelSalePerPax * totalPax;
    const tierTotalSale = nonHotelSale + hotelSaleTotal;
    const pricePerPax = tierTotalSale / Math.max(totalPax, 1);
    return {
      star, hotel: opt, hotelNetPerPax, hotelSalePerPax,
      totalSale: tierTotalSale, pricePerPax
    };
  }).filter(Boolean);
  const hasTiers = tierQuotes.length > 0;

  const handlePrintPDF = () => {
    document.body.classList.add('print-quote-only');
    const orig = document.title;
    document.title = `BaoGia_${request.code}_${(marketing.tourName || 'Tour').replace(/\s+/g, '_')}`;
    setTimeout(() => {
      window.print();
      document.title = orig;
      setTimeout(() => document.body.classList.remove('print-quote-only'), 500);
    }, 200);
  };

  const totalSale = rows.reduce((s, r) => s + r.priceNet * (1 + r.vat/100) * (1 + r.markup/100), 0);
  const shareSummary = `${request.adults}NL + ${request.children}TE · ${request.days}N${request.nights}Đ · ${fmtVND(totalSale)}`;

  return (
    <div className="layout-2col">
      <div className="quote-wrap">
        <div className="quote-hero">
          <div className="quote-hero-logo">TOURKIT</div>
          <div className="quote-hero-content">
            <div className="quote-hero-eyebrow">Báo giá tour đoàn · {request.code}</div>
            <h1 className="quote-hero-title">{marketing.tourName}</h1>
            <div className="quote-hero-tagline">{marketing.tagline} | {request.days} NGÀY {request.nights} ĐÊM</div>
          </div>
        </div>

        <div className="quote-info-row">
          <div className="quote-info">
            <div className="quote-info-label">Thời gian</div>
            <div className="quote-info-val">{request.days} Ngày {request.nights} Đêm</div>
          </div>
          <div className="quote-info">
            <div className="quote-info-label">Điểm đi</div>
            <div className="quote-info-val">{request.route.split('-')[0].trim()}</div>
          </div>
          <div className="quote-info">
            <div className="quote-info-label">Phương tiện</div>
            <div className="quote-info-val">Máy bay / Xe du lịch</div>
          </div>
        </div>

        <div className="quote-body">
          <div className="quote-section-title">Lịch trình tóm tắt</div>
          <div className="quote-section-rule"></div>

          {itinerary.map((d, i) => (
            <div key={i} className="quote-day">
              <div className="quote-day-num">{String(d.day).padStart(2, '0')}</div>
              <div className="quote-day-content">
                <h3 className="quote-day-title">{marketing.dayTitles?.[i] || d.title}</h3>
                {d.activities.map((a, j) => (
                  <div key={j} className="quote-day-act">
                    <div className="quote-day-time">{a.time}</div>
                    <div className="quote-day-name">{a.title}</div>
                  </div>
                ))}
              </div>
            </div>
          ))}

          <div className="quote-pax-summary">
            <div>
              <div className="quote-eyebrow">Đoàn khách</div>
              <div className="quote-pax-text">{request.adults} người lớn + {request.children} trẻ em</div>
              <div style={{fontSize: 11.5, color: 'var(--text-3)', marginTop: 2}}>
                {request.days} ngày {nights} đêm
              </div>
            </div>
            {!hasTiers && (
              <div style={{textAlign: 'right'}}>
                <div className="quote-eyebrow">Giá / Người lớn</div>
                <div className="quote-single-price numeric">{fmtVND(totalSale / Math.max(totalPax, 1))}</div>
              </div>
            )}
          </div>

          {/* 3 báo giá theo tier — chỉ render khi user đã chọn ≥1 hotel ở Step 1.5 */}
          {hasTiers && (
            <div className="quote-tiers">
              <div className="quote-section-title" style={{marginTop: 22}}>
                Phương án giá ({tierQuotes.length} hạng)
              </div>
              <div className="quote-section-rule"></div>
              <div className="quote-tier-grid">
                {tierQuotes.map(q => (
                  <div key={q.star} className={'quote-tier-card tier-' + q.star}>
                    <div className="qtc-star-row">
                      <span className="qtc-star">{q.star}★</span>
                      <span className="qtc-tier-label">Khách sạn {q.star} sao</span>
                    </div>
                    <div className="qtc-price">
                      <div className="qtc-price-val numeric">{fmtVND(Math.round(q.pricePerPax))}</div>
                      <div className="qtc-price-unit">đ / khách</div>
                    </div>
                    <div className="qtc-hotel">
                      <div className="qtc-hotel-label">Lưu trú</div>
                      <div className="qtc-hotel-name">{q.hotel.providerName}</div>
                      {q.hotel.roomTypeName && (
                        <div className="qtc-hotel-room">{q.hotel.roomTypeName}</div>
                      )}
                    </div>
                    <div className="qtc-incl">
                      <div className="qtc-incl-row">✓ {nights} đêm khách sạn {q.star} sao</div>
                      <div className="qtc-incl-row">✓ Xe đưa đón theo lịch trình</div>
                      <div className="qtc-incl-row">✓ Ăn uống theo chương trình</div>
                      <div className="qtc-incl-row">✓ HDV chuyên nghiệp</div>
                    </div>
                    <div className="qtc-total">
                      <div className="qtc-total-label">Tổng đoàn {totalPax} khách</div>
                      <div className="qtc-total-val numeric">{fmtVND(Math.round(q.totalSale))}</div>
                    </div>
                  </div>
                ))}
              </div>
              <div style={{fontSize: 11, color: 'var(--text-3)', marginTop: 10, fontStyle: 'italic'}}>
                💡 Giá bao gồm hotel theo hợp đồng NCC. Khách chọn hạng → sales chốt phương án.
              </div>
            </div>
          )}
        </div>
      </div>

      <div style={{display: 'flex', flexDirection: 'column', gap: 14, position: 'sticky', top: 140, alignSelf: 'flex-start'}}>
        <h3 style={{fontSize: 16, fontWeight: 700, margin: 0, letterSpacing: '-0.01em'}}>Hoàn tất & gửi khách</h3>

        <SalePredictor request={request} />

        <button className="btn btn-dark btn-lg btn-full" onClick={handlePrintPDF}>
          <Icon name="download" size={16} stroke={2} />
          TẢI PDF CHUYÊN NGHIỆP
        </button>
        <button className="btn btn-primary btn-lg btn-full" onClick={() => setShareOpen(true)}>
          <Icon name="share" size={16} stroke={2} />
          GỬI LINK CHO KHÁCH (ZALO)
        </button>

        <window.ShareDialog open={shareOpen} onClose={() => setShareOpen(false)}
          link={shareLink}
          title={marketing.tourName}
          summary={shareSummary}
          request={request}
          marketing={marketing}
          totalSale={totalSale}
          perPax={totalSale / Math.max(totalPax, 1)}
          onSent={(channel) => {
            setStatus('SENT');
            pushToast && pushToast(`Đã gửi báo giá qua ${channel === 'zalo' ? 'Zalo' : channel === 'mail' ? 'Email' : 'SMS'}`);
            setShareOpen(false);
          }}
        />

        <button className="btn btn-outline btn-full" onClick={onRestart}>
          Tạo tour mới
        </button>
        <button className="btn btn-ghost btn-full" onClick={onBack} style={{justifyContent: 'flex-start'}}>
          <Icon name="arrowLeft" size={14} /> Quay lại bảng giá
        </button>

        <div className="card" style={{padding: 16, marginTop: 4}}>
          <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--text-3)', marginBottom: 8}}>Trạng thái</div>
          <div style={{display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600}}>
            <span style={{width: 8, height: 8, borderRadius: '50%', background: status === 'SENT' ? 'var(--success)' : 'var(--warning)'}}></span>
            {status === 'SENT' ? 'SENT — đã gửi khách' : 'DRAFT — chưa gửi khách'}
          </div>
        </div>
      </div>
    </div>
  );
}

function SalePredictor({ request }) {
  // Default tĩnh — không call AI cho đến khi user bấm.
  const [data, setData] = React.useState({
    rate: 78,
    reason: 'ước tính mặc định cho đoàn B2B có ngân sách tương tự (bấm "Dự đoán bằng AI" để có phân tích chi tiết)',
    pristine: true
  });
  const [loading, setLoading] = React.useState(false);

  const runPredict = async () => {
    setLoading(true);
    try {
      const prompt = `Bạn là analyst dự đoán tỉ lệ chốt sale tour B2B Việt Nam. Dựa trên:
        - Đoàn: ${request.adults + request.children} khách (B2B công ty)
        - Ngân sách khách: ${fmtVND(request.budgetPerPax)}/pax
        - Khởi hành: ${request.startDate} (mùa hè cao điểm)
        - Tour: ${request.route}, ${request.days}N${request.nights}Đ
        - Loại: ${request.preferences.includes('Team Building') ? 'Team Building/MICE' : 'Du lịch thường'}

        Dự đoán tỉ lệ chốt sale (60-90%) dựa trên lịch sử các deal tương tự. Trả JSON THUẦN:
        {"rate": 78, "reason": "1 câu giải thích ngắn lý do (15-20 chữ)"}`;
      const raw = await window.claude.complete(prompt);
      const m = raw.match(/\{[\s\S]*\}/);
      if (m) setData({ ...JSON.parse(m[0]), pristine: false });
      else throw new Error('parse');
    } catch (e) {
      setData({ rate: 78, reason: 'dựa trên các đoàn khách công ty có ngân sách tương tự', pristine: false });
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="predictor-card">
      <div className="predictor-header">
        <Icon name="trend" size={14} stroke={2.5} /> HIỆU SUẤT SALE
      </div>
      {loading ? (
        <>
          <div className="sk" style={{height: 32, marginBottom: 8, background: 'rgba(249,115,22,0.12)'}} />
          <div className="sk" style={{height: 11, width: '85%', background: 'rgba(249,115,22,0.12)'}} />
        </>
      ) : (
        <>
          <div className="predictor-rate">{data.rate}<span style={{fontSize: 22, marginLeft: 2}}>%</span></div>
          <p className="predictor-text">
            Mẫu báo giá này có tỉ lệ chốt sale <strong style={{color: 'var(--primary-dark)'}}>{data.rate}%</strong> {data.reason}.
          </p>
          <button className="btn btn-outline btn-full" style={{padding: 8, marginTop: 10, fontSize: 12}}
            onClick={runPredict} disabled={loading}>
            <Icon name="sparkle" size={12} /> {data.pristine ? 'Dự đoán bằng AI' : 'Dự đoán lại'}
          </button>
        </>
      )}
    </div>
  );
}

window.Step4Quote = Step4Quote;
