// components/quota-upgrade-modal.jsx
// Modal "Nạp thêm lượt AI" — 2 bước cùng overlay:
//   B1 TierPick : show 3 gói, click → POST /api/v1/quota/order → switch B2
//   B2 QrPay    : show VietQR + countdown 15p + poll status mỗi 3s → khi paid show success + onPaid()
// Mount qua window.QuotaUpgradeModal — app.jsx render khi state showUpgrade=true.

(function(){
  'use strict';
  const { useState, useEffect, useRef } = React;

  const fmtVND = window.fmtVND || (n => (n || 0).toLocaleString('vi-VN') + 'đ');   // bản chuẩn ở lib/data.js
  const fmtPerUnit = (amt, units) => Math.round(amt / units).toLocaleString('vi-VN') + 'đ/lượt';

  function QuotaUpgradeModal({ open, onClose, onPaid }) {
    // Tránh mount sub-component khi chưa mở — đỡ tốn render.
    if (!open) return null;

    const [step, setStep]   = useState('pick');  // pick | qr | paid
    const [tiers, setTiers] = useState([]);
    const [bank, setBank]   = useState(null);    // {bankBin, accountNumber, accountName, mock}
    const [order, setOrder] = useState(null);    // server response sau POST /order
    const [loading, setLoading] = useState(true);
    const [err, setErr] = useState(null);

    // Load catalog 1 lần khi mở.
    useEffect(() => {
      let alive = true;
      setLoading(true);
      window.tourkitAuth.authedFetch('/api/v1/quota/tiers').then(async r => {
        const d = await r.json();
        if (!alive) return;
        setTiers(d.tiers || []);
        setBank({ ...d.account, mock: d.mock });
        setLoading(false);
      }).catch(e => { if (alive) { setErr('Không tải được danh sách gói'); setLoading(false); } });
      return () => { alive = false; };
    }, []);

    async function pickTier(tier){
      setErr(null);
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/quota/order', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tierId: tier.id })
        });
        const d = await r.json();
        if (!r.ok) throw new Error(d.error || 'Tạo đơn thất bại');
        setOrder(d);
        setStep('qr');
      } catch(e){ setErr(e.message); }
    }

    function backToPick(){ setOrder(null); setStep('pick'); setErr(null); }

    return (
      <div className="qu-overlay" role="dialog" aria-modal="true"
           onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
        <div className="qu-modal">
          <button className="qu-close" aria-label="Đóng" onClick={onClose}>×</button>

          {step === 'pick' && (
            <TierPickStep tiers={tiers} loading={loading} err={err}
                          onPick={pickTier} bank={bank} />
          )}
          {step === 'qr' && order && (
            <QrPayStep order={order} onBack={backToPick}
                       onPaid={(snap) => { setStep('paid'); onPaid && onPaid(snap); }} />
          )}
          {step === 'paid' && order && (
            <PaidStep order={order} onClose={onClose} />
          )}
        </div>
      </div>
    );
  }

  // ─── Step 1: chọn gói ────────────────────────────────────────────────────────
  function TierPickStep({ tiers, loading, err, onPick, bank }){
    return (
      <div className="qu-pick">
        <div className="qu-head">
          <h2 className="qu-title">Nạp thêm lượt AI</h2>
          <p className="qu-sub">Chọn gói phù hợp với nhu cầu của doanh nghiệp. Mua 1 lần, dùng đến hết — không thời hạn.</p>
          {bank?.mock && (
            <div className="qu-banner-dev">
              ⚙️ Chế độ DEV — chưa kết nối Tingee thật. Có nút "Simulate paid" ở bước QR để test luồng.
            </div>
          )}
        </div>
        {err && <div className="qu-err">{err}</div>}
        {loading ? (
          <div className="qu-loading">Đang tải danh sách gói…</div>
        ) : (
          <div className="qu-tier-grid">
            {tiers.map(t => (
              <article key={t.id} className={'qu-tier-card' + (t.popular ? ' is-popular' : '')}>
                {t.popular && <div className="qu-tier-ribbon">Phổ biến nhất</div>}
                <div className="qu-tier-name">{t.name}</div>
                <div className="qu-tier-tagline">{t.tagline}</div>
                <div className="qu-tier-price">
                  <span className="qu-price-amt">{fmtVND(t.amountVnd)}</span>
                  <span className="qu-price-unit">{fmtPerUnit(t.amountVnd, t.quotaUnits)}</span>
                </div>
                <div className="qu-tier-quota">
                  <b>{t.quotaUnits.toLocaleString('vi-VN')}</b> lượt AI
                </div>
                <ul className="qu-tier-benefits">
                  {t.benefits.map((b, i) => (
                    <li key={i}><span className="qu-bullet">✓</span> {b}</li>
                  ))}
                </ul>
                <button className={'qu-tier-cta' + (t.popular ? ' is-popular' : '')}
                        onClick={() => onPick(t)}>
                  Chọn gói này
                </button>
              </article>
            ))}
          </div>
        )}
        <p className="qu-footnote">
          Thanh toán qua VietQR (chuyển khoản ngân hàng). Lượt AI sẽ tự động cộng ngay khi nhận tiền.
        </p>
      </div>
    );
  }

  // ─── Step 2: QR + countdown + poll ──────────────────────────────────────────
  function QrPayStep({ order, onBack, onPaid }){
    const [remaining, setRemaining] = useState(order.expiresInSeconds || 900);
    const [polling, setPolling] = useState(true);
    const [err, setErr] = useState(null);
    const [copied, setCopied] = useState('');
    const timerRef = useRef(null);
    const pollRef  = useRef(null);

    // Countdown
    useEffect(() => {
      timerRef.current = setInterval(() => {
        setRemaining(r => Math.max(0, r - 1));
      }, 1000);
      return () => clearInterval(timerRef.current);
    }, []);

    // Poll status mỗi 3s
    useEffect(() => {
      let alive = true;
      const tick = async () => {
        if (!alive || !polling) return;
        try {
          const r = await window.tourkitAuth.authedFetch(`/api/v1/quota/order/${order.orderId}/status`);
          const d = await r.json();
          if (!alive) return;
          if (d.status === 'paid'){
            setPolling(false);
            // Phát event để chip quota refresh ngay (app.jsx listener).
            window.dispatchEvent(new CustomEvent('tourkit:quota', {
              detail: { used: d.quotaUsed, limit: d.quotaLimit, remaining: d.quotaRemaining,
                        usedPct: Math.round(100*d.quotaUsed/d.quotaLimit), warn: false, exhausted: false }
            }));
            onPaid({ used: d.quotaUsed, limit: d.quotaLimit, addedUnits: d.addedUnits });
          } else if (d.status === 'expired' || d.status === 'cancelled'){
            setPolling(false);
            setErr('Đơn đã hết hạn hoặc bị hủy. Tạo đơn mới để tiếp tục.');
          }
        } catch(e){ /* ignore tick fail, retry next */ }
      };
      pollRef.current = setInterval(tick, 3000);
      tick();   // kick first poll ngay
      return () => { alive = false; clearInterval(pollRef.current); };
    }, [order.orderId, polling, onPaid]);

    // Hết countdown → stop poll + show err.
    useEffect(() => {
      if (remaining <= 0 && polling){
        setPolling(false);
        setErr('Hết thời gian thanh toán. Đơn này sẽ tự hết hạn — tạo đơn mới để tiếp tục.');
      }
    }, [remaining, polling]);

    async function copy(text, label){
      try { await navigator.clipboard.writeText(text); setCopied(label); setTimeout(()=>setCopied(''), 1800); }
      catch { /* old browser */ }
    }

    // DEV: simulate paid endpoint (chỉ mock mode).
    async function simulatePaid(){
      try {
        await window.tourkitAuth.authedFetch(`/api/v1/quota/dev/simulate-paid/${order.orderId}`, { method: 'POST' });
        // Để poll detect tự nhiên cho realistic.
      } catch(e){ setErr(e.message); }
    }

    const mm = String(Math.floor(remaining/60)).padStart(2,'0');
    const ss = String(remaining%60).padStart(2,'0');

    return (
      <div className="qu-qr">
        <div className="qu-qr-head">
          <button className="qu-back" onClick={onBack}>← Đổi gói</button>
          <div className="qu-qr-title">
            <h2>Quét QR để chuyển khoản</h2>
            <p className="qu-qr-sub">{order.tierName} · <b>{fmtVND(order.amountVnd)}</b> — {order.quotaUnits.toLocaleString('vi-VN')} lượt AI</p>
          </div>
          <div className={'qu-countdown' + (remaining < 60 ? ' is-warn' : '')}>
            <span className="qu-cd-label">Còn lại</span>
            <span className="qu-cd-time">{mm}:{ss}</span>
          </div>
        </div>

        {err && <div className="qu-err">{err}</div>}

        <div className="qu-qr-body">
          <div className="qu-qr-img-wrap">
            <img className="qu-qr-img" src={order.qrPayload} alt="VietQR thanh toán" loading="eager" />
            {polling && <div className="qu-qr-status">Đang chờ chuyển khoản…</div>}
          </div>

          <div className="qu-qr-info">
            <h3>Hướng dẫn</h3>
            <ol className="qu-steps">
              <li>Mở app ngân hàng (MB, Vietcombank, Techcombank…)</li>
              <li>Quét mã QR bên cạnh</li>
              <li>Xác nhận chuyển — không sửa số tiền hay nội dung</li>
            </ol>

            <h3 className="qu-fallback-title">Hoặc chuyển khoản tay</h3>
            <div className="qu-bank">
              <div className="qu-row">
                <span>Ngân hàng</span>
                <b>{order.bankBin === '970422' ? 'MB Bank' : order.bankBin}</b>
              </div>
              <div className="qu-row">
                <span>Số tài khoản</span>
                <button className="qu-copy" onClick={()=>copy(order.accountNumber, 'STK')}>
                  <b>{order.accountNumber}</b>
                  <span className="qu-copy-ico">{copied==='STK' ? '✓' : '📋'}</span>
                </button>
              </div>
              <div className="qu-row">
                <span>Người nhận</span>
                <b>{order.accountName}</b>
              </div>
              <div className="qu-row">
                <span>Số tiền</span>
                <button className="qu-copy" onClick={()=>copy(String(order.amountVnd), 'AMT')}>
                  <b>{fmtVND(order.amountVnd)}</b>
                  <span className="qu-copy-ico">{copied==='AMT' ? '✓' : '📋'}</span>
                </button>
              </div>
              <div className="qu-row qu-row-memo">
                <span>Nội dung CK</span>
                <button className="qu-copy" onClick={()=>copy(order.memo, 'MEMO')}>
                  <b className="qu-memo">{order.memo}</b>
                  <span className="qu-copy-ico">{copied==='MEMO' ? '✓' : '📋'}</span>
                </button>
              </div>
              <p className="qu-warn-memo">⚠️ Giữ nguyên nội dung CK — hệ thống match tự động qua mã này.</p>
            </div>

            {window.tourkitDebug && (
              <button className="qu-sim-btn" onClick={simulatePaid}>
                🧪 [DEV] Simulate paid (mock Tingee)
              </button>
            )}
          </div>
        </div>
      </div>
    );
  }

  // ─── Step 3: success ─────────────────────────────────────────────────────────
  function PaidStep({ order, onClose }){
    useEffect(() => {
      // Confetti nhẹ qua emoji burst (không cần lib).
      const root = document.querySelector('.qu-paid');
      if (!root) return;
      const symbols = ['🎉','✨','🎊','🪙','💎'];
      for (let i = 0; i < 14; i++){
        const el = document.createElement('span');
        el.className = 'qu-confetti';
        el.textContent = symbols[i % symbols.length];
        el.style.left = (10 + Math.random() * 80) + '%';
        el.style.animationDelay = (Math.random() * 0.6) + 's';
        el.style.fontSize = (14 + Math.random() * 18) + 'px';
        root.appendChild(el);
      }
      return () => root.querySelectorAll('.qu-confetti').forEach(n => n.remove());
    }, []);
    return (
      <div className="qu-paid">
        <div className="qu-paid-ico">✅</div>
        <h2 className="qu-paid-title">Thanh toán thành công!</h2>
        <p className="qu-paid-sub">
          Đã cộng <b>{order.quotaUnits.toLocaleString('vi-VN')}</b> lượt AI vào tài khoản. Có thể dùng ngay.
        </p>
        <button className="qu-paid-cta" onClick={onClose}>Bắt đầu sử dụng</button>
      </div>
    );
  }

  window.QuotaUpgradeModal = QuotaUpgradeModal;
})();
