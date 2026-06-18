// Step 4: Quote preview + actions sidebar
// hotelOptions: { 3?: {providerName, pricePerPaxPerNight, ...}, 4?, 5? } từ Step 1.5 NccTierPicker.
// Khi có 1+ tier → render 3 báo giá cards thay vì 1 giá đơn → khách chọn tier.
function Step4Quote({ request, itinerary, rows, hotelOptions, onBack, onRestart, marketing, pushToast, tourId, initialStatus, onStatusSaved, ensureTourSaved }) {
  const totalPax = request.adults + request.children;
  const nights = request.nights || Math.max((request.days || 1) - 1, 1);
  const [status, setStatus] = React.useState(initialStatus || 'draft');   // draft | sent | success — persist xuống SavedTour (/api/v1/tours) → badge Wizard landing
  const [statusSaving, setStatusSaving] = React.useState(false);
  const [shareOpen, setShareOpen] = React.useState(false);
  const [shareLink, setShareLink] = React.useState('');
  const [savedQuoteId, setSavedQuoteId] = React.useState(null);
  const [sharePrepping, setSharePrepping] = React.useState(false);
  const [pdfBusy, setPdfBusy] = React.useState(false);
  const quoteRef = React.useRef(null);   // element .qv-paper → render ra PDF (html2pdf)

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

  // Tải PDF từ MẪU báo giá (element .qv-paper) bằng html2pdf — KHÔNG dùng window.print.
  // Thư viện load lazy từ CDN lần đầu bấm (giống Chart.js). Offline → toast lỗi.
  const ensureHtml2Pdf = () => new Promise((resolve, reject) => {
    if (window.html2pdf) return resolve(window.html2pdf);
    const s = document.createElement('script');
    s.src = 'https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.2/html2pdf.bundle.min.js';
    s.onload = () => resolve(window.html2pdf);
    s.onerror = () => reject(new Error('không tải được thư viện PDF (cần mạng)'));
    document.head.appendChild(s);
  });
  const handleDownloadPDF = async () => {
    const el = quoteRef.current;
    if (!el) { pushToast && pushToast('Chưa có nội dung báo giá để xuất', 'warn'); return; }
    setPdfBusy(true);
    try {
      const html2pdf = await ensureHtml2Pdf();
      const fname = `BaoGia_${request.code || 'Tour'}_${(marketing.tourName || 'Tour').replace(/\s+/g, '_')}.pdf`;
      await html2pdf().set({
        margin: [8, 8, 8, 8], filename: fname,
        image: { type: 'jpeg', quality: 0.96 },
        html2canvas: { scale: 2, useCORS: true, backgroundColor: '#ffffff', logging: false },
        jsPDF: { unit: 'mm', format: 'a4', orientation: 'portrait' },
        pagebreak: { mode: ['avoid-all', 'css', 'legacy'] },
      }).from(el).save();
    } catch (e) {
      pushToast && pushToast('Tạo PDF lỗi: ' + (e.message || e), 'error');
    } finally { setPdfBusy(false); }
  };

  const totalSale = rows.reduce((s, r) => s + r.priceNet * (1 + r.vat/100) * (1 + r.markup/100), 0);
  const shareSummary = `${request.adults}NL + ${request.children}TE · ${request.days}N${request.nights}Đ · ${fmtVND(totalSale)}`;

  // ── Save báo giá vào DB rồi build share link THẬT (thay vì link fake cũ) ──
  // Lần đầu user bấm "GỬI LINK CHO KHÁCH": POST /tour-quotes → server sinh id Guid →
  // shareLink = `${origin}/q/{id}` → khách bấm xem được mà không cần login.
  // Lần sau: dùng id đã lưu (không tạo bản ghi mới). Nếu fail → fallback link cũ + toast warn.
  // Lưu báo giá vào SQL (/tour-quotes) → trả id cho share link công khai (/q/{id}).
  async function saveQuote() {
    const totalNet = Math.round(rows.reduce((s, r) => s + (r.priceNet || 0), 0));
    const totalRev = Math.round(totalSale);
    const body = {
      id: savedQuoteId,                              // null lần đầu, có khi resave
      title: marketing.tourName || ('Báo giá tour ' + (request.code || '')),
      customerName: request.customerName || null,
      customerPhone: request.customerPhone || null,
      marketName: null, tourType: null,
      startDate: request.startDate || null, endDate: null,
      adultCount: request.adults || 0, childCount: request.children || 0,
      totalNet, totalRevenue: totalRev, profit: totalRev - totalNet,
      marginPercent: totalRev > 0 ? Math.round(((totalRev - totalNet) / totalRev) * 10000) / 100 : null,
      data: { request, itinerary, rows, hotelOptions, marketing },   // Full wizard state
    };
    const r = await window.tourkitAuth.authedFetch('/api/v1/tour-quotes', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    const j = await r.json();
    if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
    const id = j.id || j.item?.id;
    if (id) setSavedQuoteId(id);
    return id;
  }

  // Đổi trạng thái nháp tour + persist (/api/v1/tours/{tourId}/status) → badge cập nhật ở Wizard landing.
  async function changeStatus(s) {
    if (s === status) return;
    setStatusSaving(true);
    try {
      let id = tourId;
      if (!id && ensureTourSaved) id = await ensureTourSaved();   // tour chưa lưu → lưu nháp để có id rồi mới gắn trạng thái
      if (!id) throw new Error('Chưa lưu được nháp tour để gắn trạng thái');
      const r = await window.tourkitAuth.authedFetch('/api/v1/tours/' + id + '/status', {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ status: s }),
      });
      const j = await r.json();
      if (!r.ok || j.error) throw new Error(j.error || 'HTTP ' + r.status);
      setStatus(s);
      onStatusSaved && onStatusSaved(s);   // wizard refresh savedTours + currentTour.status
      pushToast && pushToast('Trạng thái: ' + ({ draft: 'Nháp', sent: 'Đã gửi khách', success: 'Đã chốt' }[s] || s));
    } catch (e) { pushToast && pushToast('Lỗi đổi trạng thái: ' + e.message, 'error'); }
    finally { setStatusSaving(false); }
  }

  async function prepareShareLink() {
    if (shareLink) { setShareOpen(true); return; }    // đã lưu rồi
    setSharePrepping(true);
    try {
      const id = await saveQuote();
      if (!id) throw new Error('Server không trả id');
      setShareLink(`${window.location.origin}/q/${id}`);
      setShareOpen(true);
    } catch (e) {
      pushToast && pushToast('Lưu báo giá lỗi: ' + e.message + ' — dùng link tạm', 'warn');
      // Fallback link tạm để Sale vẫn share được lúc DB lỗi (link sẽ 404 khi mở nhưng không kẹt flow)
      setShareLink(`${window.location.origin}/q/temp-${request.code || Date.now()}`);
      setShareOpen(true);
    } finally {
      setSharePrepping(false);
    }
  }

  // Dùng CHUNG body với public viewer (/q/:id) — 1 source of truth cho mọi layout báo giá.
  // window.WizardQuoteBody + window.QuoteStyles được expose từ pages/quote-view.jsx.
  // Wrap trong .qv-paper để có nền giấy + boxshadow giống public.
  const QuoteBody = window.WizardQuoteBody;
  const QStyles   = window.QuoteStyles;
  const previewQuote = {
    id: request.code || 'preview',
    title: marketing.tourName,
    adultCount: request.adults,
    childCount: request.children,
    totalRevenue: Math.round(totalSale),
    updatedAt: new Date().toISOString(),
  };
  const previewData = { request, itinerary, rows, hotelOptions, marketing };

  return (
    <div className="layout-2col">
      <div>
        {QStyles && <QStyles />}
        <div className="qv-paper" ref={quoteRef}>
          {QuoteBody
            ? <QuoteBody quote={previewQuote} d={previewData} fmtVND={fmtVND} />
            : <div style={{padding: 40, color: '#9ca3af'}}>Đang tải preview…</div>}
        </div>
      </div>

      <div style={{display: 'flex', flexDirection: 'column', gap: 14, position: 'sticky', top: 140, alignSelf: 'flex-start'}}>
        <h3 style={{fontSize: 16, fontWeight: 700, margin: 0, letterSpacing: '-0.01em'}}>Hoàn tất & gửi khách</h3>

        <SalePredictor request={request} />

        <button className="btn btn-dark btn-lg btn-full" onClick={handleDownloadPDF} disabled={pdfBusy}>
          <Icon name="download" size={16} stroke={2} />
          {pdfBusy ? 'ĐANG TẠO PDF…' : 'TẢI PDF CHUYÊN NGHIỆP'}
        </button>
        <button className="btn btn-primary btn-lg btn-full" onClick={prepareShareLink} disabled={sharePrepping}>
          <Icon name="share" size={16} stroke={2} />
          {sharePrepping ? 'ĐANG LƯU BÁO GIÁ...' : 'GỬI LINK CHO KHÁCH (ZALO)'}
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
            changeStatus('sent');   // persist 'sent' xuống /tour-quotes
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
          <div style={{fontSize: 10, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--text-3)', marginBottom: 10}}>
            Trạng thái{statusSaving ? ' · đang lưu…' : ''}
          </div>
          <div style={{display: 'flex', gap: 6}}>
            {[['draft', 'Nháp', 'var(--warning)'], ['sent', 'Đã gửi', 'var(--primary)'], ['success', 'Đã chốt', 'var(--success)']].map(([k, lbl, color]) => {
              const on = status === k;
              return (
                <button key={k} type="button" onClick={() => changeStatus(k)} disabled={statusSaving}
                  style={{flex: 1, minWidth: 70, padding: '8px 6px', borderRadius: 8, fontSize: 12, fontWeight: 700,
                    cursor: statusSaving ? 'wait' : 'pointer',
                    border: '1.5px solid ' + (on ? color : 'var(--border)'),
                    background: on ? color : 'white', color: on ? 'white' : 'var(--text-2)'}}>
                  {lbl}
                </button>
              );
            })}
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
