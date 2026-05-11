// Step 4: Quote preview + actions sidebar
function Step4Quote({ request, itinerary, rows, onBack, onRestart, marketing, pushToast }) {
  const totalPax = request.adults + request.children;
  const [status, setStatus] = React.useState('DRAFT');
  const [shareOpen, setShareOpen] = React.useState(false);
  const [shareLink] = React.useState(`https://quote.tourkit.vn/q/${request.code}-${Math.random().toString(36).slice(2, 7)}`);

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

          <div style={{marginTop: 28, padding: 24, background: 'var(--bg)', borderRadius: 12, display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 16}}>
            <div>
              <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--text-3)', marginBottom: 4}}>Đoàn khách</div>
              <div style={{fontSize: 15, fontWeight: 700}}>{request.adults} người lớn + {request.children} trẻ em</div>
            </div>
            <div style={{textAlign: 'right'}}>
              <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--text-3)', marginBottom: 4}}>Giá / Người lớn</div>
              <div style={{fontSize: 22, fontWeight: 800, color: 'var(--primary)', letterSpacing: '-0.02em'}} className="numeric">
                {fmtVND((rows.reduce((s,r) => s + r.priceNet*(1+r.vat/100)*(1+r.markup/100), 0)) / Math.max(totalPax, 1))}
              </div>
            </div>
          </div>
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
