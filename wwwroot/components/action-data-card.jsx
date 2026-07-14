// components/action-data-card.jsx — Render dữ liệu action-result (Task 12: action tools SSE).
// Dùng chung /assistant (DataPanel bên phải) + /travai (Jarvis, hiện dưới bubble AI).
// data = ChatData (camelCase từ backend), 3 kind action-result quan tâm ở đây:
//   "customer-review" → data.raw = CustomerReview (Models/ReviewModels.cs)
//   "deal-score"       → data.raw = DealScore (Models/DealModels.cs)
//   "mail-list"        → data.raw = MailItem[] (Models/MailModels.cs)
// Kind khác (kpi/tours/cashflow/...) KHÔNG đi qua file này — vẫn render ở DataPanel như cũ.

const ADC_RANK_VI = { A: 'Hạng A', B: 'Hạng B', C: 'Hạng C', D: 'Hạng D' };
const ADC_ALERT_VI = { high: 'Cảnh báo cao', medium: 'Cảnh báo trung bình' };
const ADC_LEVEL_VI = { cao: 'Khả năng chốt cao', trung_binh: 'Khả năng chốt trung bình', thap: 'Khả năng chốt thấp' };
// mirror wwwroot/pages/mail.jsx _CAT_VI — Babel-standalone không share module, đành lặp lại.
const ADC_CAT_VI = {
  hoi_dat_tour: 'Hỏi đặt tour', xin_bao_gia: 'Xin báo giá', khieu_nai: 'Khiếu nại',
  xac_nhan: 'Xác nhận', spam: 'Spam', khac: 'Khác'
};

function AdcReviewCard({ r }) {
  if (!r) return null;
  // SummaryLine đôi khi mở đầu bằng chính hạng ("C — …" hoặc "Hạng C — …") → badge đã hiện, bỏ tiền tố.
  const _rank = String(r.rank || '').trim();
  let _summary = String(r.summaryLine || '').trim();
  if (_rank) {
    let s = _summary.replace(/^h[aạ]ng\s+/i, '');   // bỏ "Hạng "/"hạng " nếu có
    if (s.toUpperCase().startsWith(_rank.toUpperCase())) {
      const rest = s.slice(_rank.length).trimStart();
      if (/^[—\-:]/.test(rest)) _summary = rest.slice(1).trimStart();
    }
  }
  return (
    <div className="jv-data-card jv-review-card">
      <div className="jv-data-row">
        <span className={'jv-rank-badge jv-rank-' + _rank.toLowerCase()}>{ADC_RANK_VI[r.rank] || r.rank}</span>
        {r.alert && r.alert.level && r.alert.level !== 'none' && (
          <span className={'jv-alert-badge jv-alert-' + r.alert.level}>
            {ADC_ALERT_VI[r.alert.level] || r.alert.level}{r.alert.message ? ` — ${r.alert.message}` : ''}
          </span>
        )}
      </div>
      {_summary && <p className="jv-data-summary">{_summary}</p>}
      {(r.rankReason || r.portrait) && (
        <div className="jv-data-meta">
          {r.rankReason && <p className="jv-data-line"><strong>Lý do xếp hạng:</strong> {r.rankReason}</p>}
          {r.portrait && <p className="jv-data-line"><strong>Chân dung:</strong> {r.portrait}</p>}
        </div>
      )}
      {r.strengths && r.strengths.length > 0 && (
        <div className="jv-data-block jv-block-good">
          <div className="jv-data-block-label">Điểm mạnh</div>
          <ul>{r.strengths.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
      {r.concerns && r.concerns.length > 0 && (
        <div className="jv-data-block jv-block-warn">
          <div className="jv-data-block-label">Lưu ý</div>
          <ul>{r.concerns.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
      {r.actionNow && r.actionNow.task && (
        <div className="jv-data-block jv-block-action">
          <div className="jv-data-block-label">Hành động ngay</div>
          <p className="jv-data-line">{r.actionNow.task}{r.actionNow.reason ? ` — ${r.actionNow.reason}` : ''}</p>
        </div>
      )}
      {r.action30Days && r.action30Days.length > 0 && (
        <div className="jv-data-block jv-block-plan">
          <div className="jv-data-block-label">Trong 30 ngày</div>
          <ul>{r.action30Days.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
      {r.productSuggestions && r.productSuggestions.length > 0 && (
        <div className="jv-data-block jv-block-suggest">
          <div className="jv-data-block-label">Gợi ý sản phẩm</div>
          <ul>{r.productSuggestions.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
    </div>
  );
}

function AdcDealCard({ d }) {
  if (!d) return null;
  return (
    <div className="jv-data-card">
      <div className="jv-data-row">
        <span className={'jv-rank-badge jv-level-' + String(d.level || '')}>{ADC_LEVEL_VI[d.level] || d.level}</span>
        <span className="jv-data-winrate">{d.winRate}%</span>
      </div>
      {d.reason && <p className="jv-data-summary">{d.reason}</p>}
      {d.signals && d.signals.length > 0 && (
        <div className="jv-data-block">
          <div className="jv-data-block-label">Tín hiệu tích cực</div>
          <ul>{d.signals.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
      {d.risks && d.risks.length > 0 && (
        <div className="jv-data-block">
          <div className="jv-data-block-label">Rủi ro</div>
          <ul>{d.risks.map((s, i) => <li key={i}>{s}</li>)}</ul>
        </div>
      )}
      {d.nextAction && <p className="jv-data-line"><strong>Hành động tiếp theo:</strong> {d.nextAction}</p>}
    </div>
  );
}

function AdcMailList({ items }) {
  if (!items || !items.length) {
    return <div className="jv-data-card"><p className="jv-data-summary">Không có mail mới.</p></div>;
  }
  return (
    <div className="jv-data-card">
      <ul className="jv-data-maillist">
        {items.map((m) => (
          <li key={m.id}>
            <div className="jv-mail-row1">
              <strong>{m.from ? (m.from.name || m.from.email) : '(?)'}</strong>
              {m.category && <span className="jv-mail-cat">{ADC_CAT_VI[m.category] || m.category}</span>}
              {!m.isRead && <span className="jv-mail-dot" title="Chưa đọc" />}
            </div>
            <div className="jv-mail-subject">{m.subject}</div>
            {m.aiSummary && <div className="jv-mail-summary">{m.aiSummary}</div>}
          </li>
        ))}
      </ul>
    </div>
  );
}

// Dispatch theo data.kind. Kind lạ/không nhận diện → không render gì (caller tự fallback nếu cần).
function ActionDataCard({ data }) {
  if (!data || data.raw == null) return null;
  if (data.kind === 'customer-review') return <AdcReviewCard r={data.raw} />;
  if (data.kind === 'deal-score') return <AdcDealCard d={data.raw} />;
  if (data.kind === 'mail-list') return <AdcMailList items={Array.isArray(data.raw) ? data.raw : []} />;
  return null;
}
window.ActionDataCard = ActionDataCard;
