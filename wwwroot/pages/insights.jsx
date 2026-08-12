// pages/insights.jsx — Trang "Bảng tin" (Insight Feed)
//
// Chỗ đọc lại mọi bản tin + cảnh báo do tác vụ tự động sinh ra. Đây là kênh CHẮC CHẮN nhất
// (không phụ thuộc email/Telegram/Zalo) nên cũng là nơi tra khi các kênh ngoài hỏng.
//
// Dùng lại design system chung (wga-* + Icon) + helper chung (authedFetch, fmtAgo) — không tự chế.
'use strict';

const { useState: iS, useEffect: iE, useCallback: iCB } = React;

// ─── Loại bản tin → nhãn tiếng Việt + icon ──────────────────────────────────────
// Một nguồn cho cả ô lọc và nhãn trên từng dòng, để hai chỗ không bao giờ lệch tên.
const INSIGHT_KINDS = [
  { kind: '',                label: 'Tất cả',                icon: 'list' },
  { kind: 'sale-brief',      label: 'Bản tin sáng',          icon: 'phone' },
  { kind: 'ceo-brief',       label: 'Bản tin điều hành',     icon: 'trend' },
  { kind: 'payment-alert',   label: 'Cảnh báo thanh toán',   icon: 'dollar' },
];
const kindMeta = (k) => INSIGHT_KINDS.find(x => x.kind === k)
  // Loại mới do backend thêm mà frontend chưa biết: hiện NGUYÊN mã thay vì bỏ dòng đó đi —
  // thà nhãn lạ còn hơn người dùng không thấy thông báo nào.
  || { kind: k, label: k || 'Khác', icon: 'info' };

// Mức độ: 2 = gấp (đỏ), 1 = cần để ý (cam), 0 = thông tin (xám).
const SEVERITY = {
  2: { cls: 'is-urgent', label: 'Gấp' },
  1: { cls: 'is-warn',   label: 'Cần để ý' },
  0: { cls: 'is-info',   label: '' },
};

// Markdown-lite: bản tin chỉ dùng **in đậm** + xuống dòng. Escape TRƯỚC rồi mới đổi **x** thành
// thẻ b — làm ngược thì nội dung email/tên khách chứa dấu ngoặc góc sẽ chèn được HTML vào trang.
function mdLite(text) {
  const esc = String(text || '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  return esc.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/\n/g, '<br>');
}

async function iApi(path, opts = {}) {
  const r = await window.tourkitAuth.authedFetch(path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
  });
  if (!r.ok) {
    let msg = `HTTP ${r.status}`;
    try { const j = await r.json(); msg = j.error || msg; } catch {}
    throw new Error(msg);
  }
  return r.json();
}

function InsightsPage({ pushToast }) {
  const Icon = window.Icon;
  const [items, setItems] = iS([]);
  const [kind, setKind] = iS('');
  const [unreadOnly, setUnreadOnly] = iS(false);
  const [loading, setLoading] = iS(true);
  const [err, setErr] = iS('');
  const [busy, setBusy] = iS(false);

  const load = iCB(async () => {
    setLoading(true); setErr('');
    try {
      const q = new URLSearchParams({ limit: '50' });
      if (kind) q.set('kind', kind);
      if (unreadOnly) q.set('unread', 'true');
      const d = await iApi('/api/v1/insights?' + q.toString());
      setItems(d.items || []);
    } catch (e) { setErr(e.message); }
    finally { setLoading(false); }
  }, [kind, unreadOnly]);

  iE(() => { load(); }, [load]);

  // Đánh dấu đã đọc: cập nhật ngay trên màn hình rồi mới gọi server (thao tác này gần như không
  // bao giờ lỗi, mà chờ mạng mới đổi màu thì cảm giác rất chậm). Lỗi thì tải lại cho khớp thật.
  const markRead = async (it) => {
    if (it.isRead) return;
    setItems(prev => prev.map(x => x.id === it.id ? { ...x, isRead: true } : x));
    bumpBell();
    try { await iApi(`/api/v1/insights/${it.id}/read`, { method: 'POST' }); }
    catch (e) { pushToast && pushToast('Không đánh dấu được: ' + e.message); load(); }
  };

  const markAll = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await iApi('/api/v1/insights/read-all', { method: 'POST' });
      setItems(prev => prev.map(x => ({ ...x, isRead: true })));
      bumpBell();
      pushToast && pushToast('Đã đánh dấu đọc tất cả');
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setBusy(false); }
  };

  // Báo cho chuông ở thanh trên cập nhật ngay, khỏi đợi hết chu kỳ hỏi lại.
  const bumpBell = () => window.dispatchEvent(new CustomEvent('tourkit:insights'));

  const unreadCount = items.filter(x => !x.isRead).length;

  return (
    <main className="page wga insights-page">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">Bản tin · Thông báo</div>
          <h1>Bảng tin</h1>
          <p className="wga-sub">
            Bản tin và cảnh báo do tác vụ tự động gửi. Đây là bản lưu trong app —
            luôn còn ở đây dù email hay Telegram có hỏng.
          </p>
        </div>
        <div className="insights-head-actions">
          <button className="wga-btn ghost" onClick={load} disabled={loading}>
            <Icon name="refresh" size={14} /> Tải lại
          </button>
          <button className="wga-btn" onClick={markAll} disabled={busy || unreadCount === 0}>
            <Icon name="check" size={14} /> Đọc tất cả{unreadCount > 0 ? ` (${unreadCount})` : ''}
          </button>
        </div>
      </div>

      <div className="insights-filters">
        {INSIGHT_KINDS.map(k => (
          <button key={k.kind || 'all'}
            className={'insights-chip' + (kind === k.kind ? ' is-on' : '')}
            onClick={() => setKind(k.kind)}>
            <Icon name={k.icon} size={13} /> {k.label}
          </button>
        ))}
        <label className="insights-toggle">
          <input type="checkbox" checked={unreadOnly}
            onChange={e => setUnreadOnly(e.target.checked)} />
          <span>Chỉ chưa đọc</span>
        </label>
      </div>

      {err && <div className="insights-err"><Icon name="warning" size={14} /> {err}</div>}

      {loading && <div className="insights-empty">Đang tải…</div>}

      {!loading && items.length === 0 && (
        <div className="insights-empty">
          <Icon name="bell" size={22} />
          <p><b>Chưa có gì ở đây.</b></p>
          <p>
            Bản tin sáng chỉ tới sau khi bạn đăng ký ở trang <b>Bản tin AI</b> và chọn giờ nhận.
            Muốn thử ngay thì bấm <b>Gửi thử</b> ở trang đó.
          </p>
          <button className="wga-btn primary"
            onClick={() => window.tourkitRouter.navigate('/digest')}>
            <Icon name="sliders" size={14} /> Mở trang Bản tin AI
          </button>
        </div>
      )}

      <div className="insights-list">
        {items.map(it => {
          const meta = kindMeta(it.kind);
          const sev = SEVERITY[it.severity] || SEVERITY[0];
          return (
            <article key={it.id}
              className={'insights-card ' + sev.cls + (it.isRead ? ' is-read' : ' is-unread')}
              onClick={() => markRead(it)}>
              <div className="insights-card-top">
                <span className="insights-kind"><Icon name={meta.icon} size={12} /> {meta.label}</span>
                {sev.label && <span className="insights-sev">{sev.label}</span>}
                {!it.isRead && <span className="insights-dot" title="Chưa đọc" />}
                <span className="insights-time" title={it.createdUtc}>
                  {window.tourkitUtil.fmtAgo(it.createdUtc, { empty: '—' })}
                </span>
              </div>
              <h3 className="insights-card-title">{it.title}</h3>
              <div className="insights-card-body"
                dangerouslySetInnerHTML={{ __html: mdLite(it.body) }} />
              {/* Dòng tenant-wide (Username rỗng) là việc của CẢ công ty — nói rõ để người đọc
                  biết đồng nghiệp cũng thấy, tránh cảnh 5 người cùng gọi một khách. */}
              {!it.username && <div className="insights-scope">Cả công ty đều thấy tin này</div>}
            </article>
          );
        })}
      </div>
    </main>
  );
}

window.InsightsPage = InsightsPage;
