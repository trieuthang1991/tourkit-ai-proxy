// pages/workflows.jsx — Trang "Tự động hóa" (User Workflows)
// Cho user cấu hình tác vụ AI chạy tự động theo lịch (interval).
// V1 built-in: mail-auto-sync (đồng bộ Gmail mỗi N phút).
// Pattern: X-Session-Id header + authedFetch giống mail.jsx / assistant.jsx.
// UI tái dùng design system chung của app (wga-* + Icon) — không tạo class/namespace mới.
'use strict';

const { useState: uS, useEffect: uE, useCallback: uCB } = React;

// span inline-flex dùng cho ô có icon + text (trigger, badge nội bộ)
const _wfRow = { display: 'inline-flex', alignItems: 'center', gap: 5 };

// ─── Helpers ────────────────────────────────────────────────────────────────────

// Dùng chung window.tourkitAuth.authedFetch (gắn X-Session-Id + 401→logout + 429→quota event).
// apiFetch chỉ bọc thêm envelope JSON + throw khi !ok (KHÔNG tự chế lại session/header).
async function apiFetch(path, opts = {}) {
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

// "time ago" → dùng chung window.tourkitUtil.fmtAgo (seconds precision + empty '—').
const relativeTime = (utcStr) => window.tourkitUtil.fmtAgo(utcStr, { seconds: true, empty: '—' });

function futureTime(utcStr) {
  if (!utcStr) return '—';
  const diff = (new Date(utcStr).getTime() - Date.now()) / 1000;
  if (diff <= 0) return 'ngay bây giờ';
  if (diff < 60) return `sau ${Math.round(diff)} giây`;
  if (diff < 3600) return `sau ${Math.round(diff / 60)} phút`;
  return `sau ${Math.round(diff / 3600)} giờ`;
}

function parseSummary(summaryJson) {
  if (!summaryJson) return null;
  try { return JSON.parse(summaryJson); } catch { return null; }
}

function SummaryText({ summaryJson }) {
  const s = parseSummary(summaryJson);
  if (!s) return <span className="workflows-summary-empty">—</span>;
  // deal-auto-review / customer-auto-review summary: { reviewed, rereviewed, cooling, queued, ... }
  if (s.reviewed != null || s.queued != null || s.cooling != null) {
    return (
      <span className="workflows-summary">
        {s.reviewed != null && <span>{s.reviewed} chấm</span>}
        {s.rereviewed ? <span> · {s.rereviewed} chấm lại</span> : null}
        {s.queued != null && <span> · {s.queued} cảnh báo</span>}
        {s.cooling != null && <span> · {s.cooling} nguội</span>}
        {s.autoFinalized ? <span> · {s.autoFinalized} đã xong</span> : null}
      </span>
    );
  }
  // mail-auto-sync summary
  return (
    <span className="workflows-summary">
      {s.fetched != null && <span>{s.fetched} mail kéo</span>}
      {s.classified != null && <span> · {s.classified} phân loại</span>}
      {s.skipped != null && s.skipped > 0 && <span> · {s.skipped} bỏ qua</span>}
    </span>
  );
}

// ─── Interval options ────────────────────────────────────────────────────────────

const INTERVAL_OPTIONS = [
  { value: 5,  label: 'Mỗi 5 phút' },
  { value: 10, label: 'Mỗi 10 phút' },
  { value: 15, label: 'Mỗi 15 phút' },
  { value: 30, label: 'Mỗi 30 phút' },
  { value: 60, label: 'Mỗi 1 giờ' },
  { value: 120, label: 'Mỗi 2 giờ' },
];

// Schema option ĐỘNG per-workflow (client). Thêm workflow/option mới = thêm 1 entry — UI tự render.
// type: 'bool'→toggle | 'select'→dropdown | 'multi'→chip nhiều lựa chọn. showIf: chỉ hiện khi option kia bật.
// Khớp shape OptionsJson backend (mail-auto-sync: {autoReply, replyMode, replyCategories[], replyTone}).
const MAIL_CATEGORIES = [
  { value: 'hoi_dat_tour', label: 'Hỏi/đặt tour' },
  { value: 'xin_bao_gia',  label: 'Xin báo giá' },
  { value: 'xac_nhan',     label: 'Xác nhận' },
  { value: 'khieu_nai',    label: 'Khiếu nại' },
];
const MAIL_TONES = [
  { value: 'lich_su',    label: 'Lịch sự' },
  { value: 'than_thien', label: 'Thân thiện' },
  { value: 'dam_phan',   label: 'Đàm phán' },
  { value: 'xin_loi',    label: 'Xin lỗi' },
];
const WORKFLOW_OPTIONS = {
  'mail-auto-sync': [
    { key: 'autoReply', type: 'bool', label: 'Tự động trả lời',
      hint: 'AI tự soạn/gửi trả lời cho email mới theo cấu hình dưới đây.' },
    { key: 'replyMode', type: 'select', label: 'Chế độ', showIf: 'autoReply', default: 'draft',
      options: [
        { value: 'draft', label: 'Soạn sẵn (người duyệt rồi gửi)' },
        { value: 'send',  label: 'Gửi thẳng tự động' },
      ],
      hint: 'Soạn sẵn = an toàn (nháp chờ NV duyệt). Gửi thẳng = AI gửi luôn cho khách.' },
    { key: 'replyCategories', type: 'multi', label: 'Áp dụng nhóm', showIf: 'autoReply',
      default: ['hoi_dat_tour', 'xin_bao_gia', 'xac_nhan'],
      options: MAIL_CATEGORIES,
      hint: 'Chỉ auto-reply email thuộc nhóm đã chọn. Khiếu nại nên để người xử lý.' },
    { key: 'replyTone', type: 'select', label: 'Giọng văn', showIf: 'autoReply', default: 'lich_su',
      options: MAIL_TONES },
  ],
  'deal-auto-review': [
    { key: 'statuses', type: 'numbers', label: 'Trạng thái xử lý', default: [],
      hint: 'Để trống = mọi trạng thái. Nhập ID trạng thái deal (vd chỉ "Mới") cách nhau dấu phẩy. Deal ngoài danh sách coi như đã xử lý → bỏ qua.' },
    { key: 'createdWithinDays', type: 'number', label: 'Deal tạo trong (ngày)', default: 30, min: 1, max: 365,
      hint: 'Chỉ xử lý deal tạo trong N ngày gần đây. Deal cũ hơn → bỏ qua (đỡ review + tránh phát sinh nhiều).' },
    { key: 'autoReview', type: 'bool', label: 'Tự động chấm điểm AI', default: true,
      hint: 'AI tự chấm khả năng chốt cho deal mới + chấm lại khi nội dung đổi. Tắt = chỉ cảnh báo nguội.' },
    { key: 'reviewMax', type: 'number', label: 'Số deal chấm / lần', showIf: 'autoReview', default: 20, min: 1, max: 100 },
    { key: 'maxAutoReviews', type: 'number', label: 'Số lần chấm tối đa / deal', showIf: 'autoReview', default: 5, min: 1, max: 50,
      hint: 'Chống chấm lại vô tận. Đạt số lần này → ngừng tự chấm deal đó.' },
    { key: 'coolingDays', type: 'number', label: 'Ngưỡng nguội (ngày)', default: 7, min: 1, max: 90,
      hint: 'Deal không được chăm sóc quá N ngày → coi là "nguội" → cảnh báo.' },
    { key: 'minWinRateToNotify', type: 'number', label: 'Chỉ cảnh báo winRate ≥ (%)', default: 0, min: 0, max: 100,
      hint: '0 = mọi deal nguội. >0 = chỉ cảnh báo deal đã chấm điểm cao đang nguội (đỡ mail rác).' },
    { key: 'maxNotifications', type: 'number', label: 'Số lần cảnh báo tối đa / deal', default: 3, min: 1, max: 20 },
    { key: 'notifyMinGapHours', type: 'number', label: 'Giãn cách cảnh báo (giờ)', default: 24, min: 1, max: 720 },
  ],
  'customer-auto-review': [
    { key: 'createdWithinDays', type: 'number', label: 'KH tạo trong (ngày)', default: 30, min: 1, max: 365,
      hint: 'CHỈ review KH MỚI tạo trong N ngày (tránh phát sinh quá nhiều). KH cũ hơn → bỏ qua ở lượt review đầu.' },
    { key: 'reReviewDays', type: 'number', label: 'Review lại sau (ngày)', default: 30, min: 1, max: 365,
      hint: 'KH đã review: chỉ chấm lại khi đã quá N ngày kể từ lần review cuối (đọc từ bảng review).' },
    { key: 'reviewMax', type: 'number', label: 'Số KH chấm / lần', default: 20, min: 1, max: 100,
      hint: 'Giới hạn số lượt AI mỗi chu kỳ (kiểm soát quota).' },
  ],
};

// Workflow cần tài khoản dịch vụ (login TourKit nền) → hiện form ServiceAccountConfig.
const WORKFLOWS_NEED_SERVICE_ACCOUNT = ['deal-auto-review', 'customer-auto-review'];

// Gom default từ schema → {key: default} để pre-fill options khi user mới bật (tránh gửi mảng rỗng).
function optionDefaults(type) {
  const out = {};
  (WORKFLOW_OPTIONS[type] || []).forEach(o => { if (o.default !== undefined) out[o.key] = o.default; });
  return out;
}

// ─── Run History Table ───────────────────────────────────────────────────────────

function RunHistoryTable({ runs, loading }) {
  const [expandedError, setExpandedError] = uS(null);
  if (loading) return <div className="workflows-history-loading">Đang tải lịch sử...</div>;
  if (!runs || runs.length === 0) return <div className="workflows-history-empty">Chưa có lịch sử chạy.</div>;
  return (
    <div className="workflows-history-wrap">
      <table className="workflows-history-table">
        <thead>
          <tr>
            <th>Thời gian</th>
            <th>Trigger</th>
            <th>Trạng thái</th>
            <th>Tóm tắt</th>
            <th>Thời lượng</th>
          </tr>
        </thead>
        <tbody>
          {runs.map(r => (
            <React.Fragment key={r.id}>
              <tr
                className={r.status === 'failed' ? 'workflows-run-failed' : ''}
                style={{ cursor: r.status === 'failed' && r.error ? 'pointer' : 'default' }}
                onClick={() => r.status === 'failed' && r.error && setExpandedError(expandedError === r.id ? null : r.id)}>
                <td className="workflows-run-ts" title={r.startedUtc}>{relativeTime(r.startedUtc)}</td>
                <td>
                  <span style={_wfRow}>
                    <Icon name={r.triggerKind === 'manual' ? 'user' : 'clock'} size={12} />
                    {r.triggerKind === 'manual' ? 'Thủ công' : 'Lịch'}
                  </span>
                </td>
                <td>
                  {r.status === 'ok'
                    ? <span className="workflows-badge workflows-badge-ok"><Icon name="check" size={11} /> Thành công</span>
                    : <span className="workflows-badge workflows-badge-fail"><Icon name="close" size={11} /> Lỗi</span>}
                </td>
                <td><SummaryText summaryJson={r.summary} /></td>
                <td>{r.durationMs != null ? `${(r.durationMs / 1000).toFixed(1)}s` : '—'}</td>
              </tr>
              {expandedError === r.id && r.error && (
                <tr>
                  <td colSpan={5} className="workflows-run-error-row">
                    <span className="workflows-run-error-text" style={_wfRow}><Icon name="warning" size={12} /> {r.error}</span>
                  </td>
                </tr>
              )}
            </React.Fragment>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ─── ServiceAccountConfig (deal-auto-review) ───────────────────────────────────────
// Workflow PerTenant tự đăng nhập TourKit bằng 1 tài khoản dịch vụ (không cần user online).
// POST validate login + đếm deal trước khi lưu; GET trạng thái (không trả password).

function ServiceAccountConfig({ pushToast }) {
  const [status, setStatus] = uS(null);
  const [username, setUsername] = uS('');
  const [password, setPassword] = uS('');
  const [saving, setSaving] = uS(false);

  uE(() => {
    apiFetch('/api/v1/workflows/service-account')
      .then(d => setStatus(d))
      .catch(() => setStatus({ configured: false }));
  }, []);

  async function save() {
    if (!username.trim() || !password) { pushToast('Nhập tên đăng nhập + mật khẩu', 'error'); return; }
    setSaving(true);
    try {
      const res = await apiFetch('/api/v1/workflows/service-account', {
        method: 'POST', body: JSON.stringify({ username: username.trim(), password }),
      });
      if (res.ok) {
        pushToast(`Đã lưu tài khoản — thấy ${res.dealsVisible} deal.` + (res.warning ? ' ⚠ ' + res.warning : ''),
          res.warning ? 'info' : 'success');
        setPassword('');
        setStatus({ configured: true, username: username.trim() });
      } else {
        pushToast(res.error || 'Lưu thất bại', 'error');
      }
    } catch (e) {
      pushToast('Lưu thất bại: ' + e.message, 'error');
    } finally { setSaving(false); }
  }

  async function remove() {
    if (!window.confirm('Xóa tài khoản tự động? Workflow sẽ ngừng tự đăng nhập.')) return;
    setSaving(true);
    try {
      await apiFetch('/api/v1/workflows/service-account', { method: 'DELETE' });
      pushToast('Đã xóa tài khoản tự động', 'success');
      setUsername(''); setPassword('');
      setStatus({ configured: false });
    } catch (e) {
      pushToast('Xóa thất bại: ' + e.message, 'error');
    } finally { setSaving(false); }
  }

  return (
    <div className="workflows-field-row" style={{ alignItems: 'flex-start', flexDirection: 'column', gap: 8 }}>
      <label className="workflows-field-label">Tài khoản tự động</label>
      <div className="workflows-card-substats" style={{ marginBottom: 2 }}>
        Workflow đăng nhập TourKit bằng tài khoản này để quét deal (nên cấp quyền xem TẤT CẢ cơ hội).
        {status && (status.configured
          ? <span style={{ color: 'var(--primary-dark)' }}> Đang dùng: <b>{status.username}</b>.</span>
          : <span style={{ color: 'var(--danger, #c0392b)' }}> Chưa cấu hình — workflow chưa chạy được.</span>)}
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, width: '100%' }}>
        <input className="workflows-select" placeholder="Tên đăng nhập" style={{ flex: 1, minWidth: 140 }}
          value={username} onChange={e => setUsername(e.target.value)} />
        <input className="workflows-select" type="password" placeholder="Mật khẩu" style={{ flex: 1, minWidth: 140 }}
          value={password} onChange={e => setPassword(e.target.value)} />
        <button className="wga-btn" onClick={save} disabled={saving}>
          {saving ? 'Đang kiểm tra...' : (status && status.configured ? 'Cập nhật' : 'Lưu & kiểm tra')}
        </button>
        {status && status.configured && (
          <button className="wga-btn ghost" onClick={remove} disabled={saving} title="Xóa tài khoản tự động">
            <Icon name="close" size={13} /> Xóa
          </button>
        )}
      </div>
    </div>
  );
}

// ─── WorkflowCard ────────────────────────────────────────────────────────────────

function WorkflowCard({ wf, onUpdate, pushToast }) {
  const [enabled, setEnabled] = uS(wf.enabled);
  const [interval, setInterval] = uS(wf.intervalMinutes || 15);
  const [saving, setSaving] = uS(false);
  const [running, setRunning] = uS(false);
  const [historyOpen, setHistoryOpen] = uS(false);
  const [runs, setRuns] = uS(null);
  const [runsLoading, setRunsLoading] = uS(false);
  // Merge default schema + options đã lưu → tránh gửi mảng/giá trị rỗng khi user mới bật.
  const [options, setOptions] = uS({ ...optionDefaults(wf.type), ...(wf.options || {}) });

  const optionSchema = WORKFLOW_OPTIONS[wf.type] || [];

  // Sync state khi prop thay đổi (sau reload)
  uE(() => {
    setEnabled(wf.enabled);
    setInterval(wf.intervalMinutes || 15);
    setOptions({ ...optionDefaults(wf.type), ...(wf.options || {}) });
  }, [wf.enabled, wf.intervalMinutes, wf.options]);

  const isPaused = !!wf.pausedReason;

  async function handleSave() {
    setSaving(true);
    try {
      await apiFetch(`/api/v1/workflows/${wf.type}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, intervalMinutes: interval, options }),
      });
      pushToast(`Đã lưu cấu hình "${wf.label}"`);
      onUpdate();
    } catch (e) {
      pushToast('Lưu cấu hình thất bại: ' + e.message, 'error');
    } finally {
      setSaving(false);
    }
  }

  async function handleReEnable() {
    setSaving(true);
    try {
      await apiFetch(`/api/v1/workflows/${wf.type}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled: true, intervalMinutes: wf.intervalMinutes || interval }),
      });
      pushToast(`Đã bật lại "${wf.label}"`);
      onUpdate();
    } catch (e) {
      pushToast('Bật lại thất bại: ' + e.message, 'error');
    } finally {
      setSaving(false);
    }
  }

  async function handleRunNow() {
    setRunning(true);
    pushToast(`Đang chạy "${wf.label}"...`, 'info');
    try {
      const res = await apiFetch(`/api/v1/workflows/${wf.type}/run-now`, { method: 'POST' });
      if (res.ok) {
        const s = parseSummary(res.summary);
        let msg = 'Hoàn thành';
        if (s && wf.type === 'deal-auto-review') msg = `Hoàn thành: ${s.reviewed ?? 0} chấm · ${s.queued ?? 0} cảnh báo`;
        else if (s && wf.type === 'customer-auto-review') msg = `Hoàn thành: ${s.reviewed ?? 0} chấm · ${s.rereviewed ?? 0} chấm lại`;
        else if (s) msg = `Hoàn thành: ${s.classified ?? 0} mail mới phân loại`;
        pushToast(msg, 'success');
      } else {
        pushToast('Chạy thất bại: ' + (res.error || 'Lỗi không xác định'), 'error');
      }
      onUpdate();
      // Reload runs nếu đang mở
      if (historyOpen) loadRuns();
    } catch (e) {
      pushToast('Chạy thất bại: ' + e.message, 'error');
    } finally {
      setRunning(false);
    }
  }

  async function loadRuns() {
    setRunsLoading(true);
    try {
      const data = await apiFetch(`/api/v1/workflows/${wf.type}/runs?limit=20`);
      setRuns(data.items || []);
    } catch (e) {
      setRuns([]);
    } finally {
      setRunsLoading(false);
    }
  }

  function toggleHistory() {
    const next = !historyOpen;
    setHistoryOpen(next);
    if (next && runs === null) loadRuns();
  }

  // Trạng thái pill — tái dùng wga-pill (crm = xanh có chấm, faq = xám, off = đỏ)
  let statusPill;
  if (isPaused) {
    statusPill = <span className="wga-pill off">Tạm dừng</span>;
  } else if (enabled) {
    statusPill = <span className="wga-pill crm">Đang chạy</span>;
  } else {
    statusPill = <span className="wga-pill faq">Tắt</span>;
  }

  return (
    <div className={'wga-card' + (isPaused ? ' workflows-card-paused' : '')}>
      {/* Header: icon tròn + tiêu đề + pill + mô tả (tái dùng wga-card-top) */}
      <div className="wga-card-top">
        <div className="wga-card-avatar" style={{ borderColor: 'var(--primary)', color: 'var(--primary)' }}>
          <Icon name={wf.type === 'deal-auto-review' ? 'zap' : (wf.type === 'customer-auto-review' ? 'user' : 'mail')} size={20} />
        </div>
        <div className="wga-card-meta">
          <div className="wga-card-name-row">
            <span className="wga-card-name">{wf.label}</span>
            {statusPill}
          </div>
          <div className="wga-card-substats">
            <span>{wf.description}</span>
          </div>
        </div>
      </div>

      {/* Paused banner */}
      {isPaused && (
        <div className="workflows-paused-banner">
          <span style={_wfRow}><Icon name="warning" size={14} /> Đã tạm dừng: {wf.pausedReason}</span>
          <button className="wga-btn" onClick={handleReEnable} disabled={saving}>
            {saving ? 'Đang bật...' : 'Bật lại'}
          </button>
        </div>
      )}

      <div className="workflows-card-body">
        {/* Tài khoản dịch vụ (workflow nền cần login TourKit) */}
        {WORKFLOWS_NEED_SERVICE_ACCOUNT.includes(wf.type) && <ServiceAccountConfig pushToast={pushToast} />}

        {/* Toggle + Interval */}
        <div className="workflows-field-row">
          <label className="workflows-field-label">Trạng thái</label>
          <div className="workflows-toggle-wrap">
            <label className="workflows-toggle">
              <input type="checkbox" checked={enabled}
                onChange={e => setEnabled(e.target.checked)} />
              <span className="workflows-toggle-track" />
            </label>
            <span className="workflows-toggle-label">{enabled ? 'Bật' : 'Tắt'}</span>
          </div>
        </div>
        <div className="workflows-field-row">
          <label className="workflows-field-label">Tần suất</label>
          <select className="workflows-select"
            value={interval}
            onChange={e => setInterval(Number(e.target.value))}>
            {INTERVAL_OPTIONS.map(o => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
        </div>

        {/* Điều kiện/option ĐỘNG per-workflow (render từ WORKFLOW_OPTIONS; showIf → chỉ hiện khi option kia bật) */}
        {optionSchema.filter(opt => !opt.showIf || options[opt.showIf]).map(opt => (
          <div className="workflows-field-row" key={opt.key} style={{ alignItems: 'flex-start' }}>
            <label className="workflows-field-label">{opt.label}</label>
            <div style={{ flex: 1, minWidth: 0 }}>
              {opt.type === 'bool' && (
                <div className="workflows-toggle-wrap">
                  <label className="workflows-toggle">
                    <input type="checkbox" checked={!!options[opt.key]}
                      onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.checked }))} />
                    <span className="workflows-toggle-track" />
                  </label>
                  <span className="workflows-toggle-label">{options[opt.key] ? 'Bật' : 'Tắt'}</span>
                </div>
              )}
              {opt.type === 'select' && (
                <select className="workflows-select"
                  value={options[opt.key] || opt.options[0].value}
                  onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value }))}>
                  {opt.options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              )}
              {opt.type === 'multi' && (
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {opt.options.map(o => {
                    const arr = Array.isArray(options[opt.key]) ? options[opt.key] : [];
                    const on = arr.includes(o.value);
                    return (
                      <label key={o.value}
                        style={{ display: 'inline-flex', alignItems: 'center', gap: 6, padding: '4px 11px',
                          borderRadius: 999, cursor: 'pointer', fontSize: 13, userSelect: 'none',
                          border: '1px solid ' + (on ? 'var(--primary)' : 'var(--border)'),
                          background: on ? 'var(--primary-soft)' : 'transparent',
                          color: on ? 'var(--primary-dark)' : 'var(--text-2)' }}>
                        <input type="checkbox" checked={on} style={{ display: 'none' }}
                          onChange={() => setOptions(prev => {
                            const cur = Array.isArray(prev[opt.key]) ? prev[opt.key] : [];
                            return { ...prev, [opt.key]: on ? cur.filter(x => x !== o.value) : [...cur, o.value] };
                          })} />
                        {o.label}
                      </label>
                    );
                  })}
                </div>
              )}
              {opt.type === 'number' && (
                <input type="number" className="workflows-select" style={{ width: 130 }}
                  min={opt.min} max={opt.max}
                  value={options[opt.key] ?? opt.default ?? 0}
                  onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value === '' ? 0 : Number(e.target.value) }))} />
              )}
              {opt.type === 'numbers' && (
                <input type="text" className="workflows-select"
                  placeholder="vd: 1, 2 (để trống = tất cả)"
                  value={(Array.isArray(options[opt.key]) ? options[opt.key] : []).join(', ')}
                  onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value.split(',').map(x => parseInt(x.trim(), 10)).filter(n => !isNaN(n) && n > 0) }))} />
              )}
              {opt.hint && <div className="workflows-card-substats" style={{ marginTop: 4 }}>{opt.hint}</div>}
            </div>
          </div>
        ))}

        {/* Thống kê lần gần nhất */}
        {(wf.lastRunUtc || wf.nextRunUtc) && (
          <div className="workflows-meta">
            {wf.lastRunUtc && (
              <div className="workflows-meta-item">
                <span className="workflows-meta-label">Lần chạy cuối</span>
                <span className={'workflows-meta-val' + (wf.lastRunStatus === 'failed' ? ' workflows-meta-fail' : '')}>
                  {relativeTime(wf.lastRunUtc)}
                  {wf.lastRunStatus === 'ok' && <Icon name="check" size={13} />}
                  {wf.lastRunStatus === 'failed' && <Icon name="close" size={13} />}
                  {wf.lastRunSummary && <SummaryText summaryJson={wf.lastRunSummary} />}
                </span>
              </div>
            )}
            {wf.nextRunUtc && enabled && !isPaused && (
              <div className="workflows-meta-item">
                <span className="workflows-meta-label">Lần kế tiếp</span>
                <span className="workflows-meta-val">{futureTime(wf.nextRunUtc)}</span>
              </div>
            )}
          </div>
        )}

        {/* Actions — tái dùng wga-btn */}
        <div className="workflows-actions">
          <button className="wga-btn primary"
            onClick={handleSave} disabled={saving || running}>
            <Icon name="save" size={14} /> {saving ? 'Đang lưu...' : 'Lưu cấu hình'}
          </button>
          <button className="wga-btn"
            onClick={handleRunNow} disabled={running || saving}>
            <Icon name="refresh" size={14} /> {running ? 'Đang chạy...' : 'Chạy ngay'}
          </button>
          <button className="wga-btn ghost"
            onClick={toggleHistory}>
            <Icon name="list" size={14} /> {historyOpen ? 'Ẩn lịch sử' : '20 lần gần nhất'}
            <Icon name={historyOpen ? 'chevronUp' : 'chevronDown'} size={13} />
          </button>
        </div>
      </div>

      {/* Run history collapsible */}
      {historyOpen && (
        <div className="workflows-history">
          <RunHistoryTable runs={runs} loading={runsLoading} />
        </div>
      )}
    </div>
  );
}

// ─── WorkflowsPage ────────────────────────────────────────────────────────────────

function WorkflowsPage({ pushToast }) {
  const [workflows, setWorkflows] = uS([]);
  const [loading, setLoading] = uS(true);
  const [error, setError] = uS(null);

  async function loadWorkflows() {
    try {
      const data = await apiFetch('/api/v1/workflows');
      setWorkflows(data.items || []);
      setError(null);
    } catch (e) {
      setError('Không tải được danh sách workflow: ' + e.message);
    } finally {
      setLoading(false);
    }
  }

  uE(() => { loadWorkflows(); }, []);

  // KPI — tính từ danh sách hiện tại
  const running = workflows.filter(w => w.enabled && !w.pausedReason).length;
  const paused = workflows.filter(w => !!w.pausedReason).length;
  const lastRunMs = workflows.reduce((acc, w) => {
    if (!w.lastRunUtc) return acc;
    const t = new Date(w.lastRunUtc).getTime();
    return (acc == null || t > acc) ? t : acc;
  }, null);

  return (
    <main className="page wga">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">Tích hợp · Tự động</div>
          <h1>Tự động hóa</h1>
          <p className="wga-sub">Tác vụ AI chạy nền theo lịch — kéo &amp; phân loại email, đồng bộ dữ liệu. Bật một lần, hệ thống tự chạy đều, bạn chỉ vào xem kết quả.</p>
        </div>
      </div>

      {!loading && !error && workflows.length > 0 && (
        <div className="wga-kpi-strip">
          <div className="wga-kpi">
            <div className="wga-kpi-l">Tác vụ</div>
            <div className="wga-kpi-v">{workflows.length}</div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Đang chạy</div>
            <div className="wga-kpi-v">{running}<span className="wga-kpi-s">/{workflows.length}</span></div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Tạm dừng</div>
            <div className="wga-kpi-v">{paused}</div>
          </div>
          <div className="wga-kpi">
            <div className="wga-kpi-l">Chạy gần nhất</div>
            <div className="wga-kpi-v" style={{ fontSize: 16, fontWeight: 700 }}>
              {lastRunMs ? relativeTime(new Date(lastRunMs).toISOString()) : '—'}
            </div>
          </div>
        </div>
      )}

      {loading && <div className="wga-loading">Đang tải…</div>}

      {error && (
        <div className="wga-empty">
          <p>{error}</p>
          <button className="wga-btn" onClick={loadWorkflows} style={{ marginTop: 14 }}>
            <Icon name="refresh" size={14} /> Thử lại
          </button>
        </div>
      )}

      {!loading && !error && workflows.length === 0 && (
        <div className="wga-empty">
          <div className="wga-empty-icon"><Icon name="zap" size={48} /></div>
          <h3>Chưa có tác vụ tự động</h3>
          <p>Khi có tác vụ khả dụng, bạn có thể bật lịch chạy nền tại đây.</p>
        </div>
      )}

      <div className="wga-list workflows-list">
        {workflows.map(wf => (
          <WorkflowCard
            key={wf.type}
            wf={wf}
            onUpdate={loadWorkflows}
            pushToast={pushToast}
          />
        ))}
      </div>
    </main>
  );
}

window.WorkflowsPage = WorkflowsPage;
