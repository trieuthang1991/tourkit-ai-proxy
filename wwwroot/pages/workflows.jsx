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
  // customer-auto-review summary: { reviewed, rereviewed, skippedFresh, skippedUnchanged, skippedOld, timedOut }
  if (s.skippedFresh != null || s.skippedUnchanged != null || s.skippedOld != null) {
    return (
      <span className="workflows-summary">
        <span>{s.reviewed ?? 0} review mới</span>
        <span> · {s.rereviewed ?? 0} review lại</span>
        {s.skippedUnchanged ? <span> · {s.skippedUnchanged} không đổi</span> : null}
        {s.skippedFresh ? <span> · {s.skippedFresh} chưa tới hạn</span> : null}
        {s.timedOut ? <span className="workflows-summary-warn"> · hết giờ, chạy tiếp lần sau</span> : null}
      </span>
    );
  }
  // deal-auto-review summary: { reviewed, rereviewed, cooling, queued, ... }
  if (s.reviewed != null || s.queued != null || s.cooling != null) {
    return (
      <span className="workflows-summary">
        {s.reviewed != null && <span>{s.reviewed} chấm</span>}
        {s.rereviewed ? <span> · {s.rereviewed} chấm lại</span> : null}
        {s.queued != null && <span> · {s.queued} cảnh báo</span>}
        {s.cooling != null && <span> · {s.cooling} nguội</span>}
        {s.autoFinalized ? <span> · {s.autoFinalized} đã xong</span> : null}
        {s.timedOut ? <span className="workflows-summary-warn"> · hết giờ, chạy tiếp lần sau</span> : null}
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

// ─── Schema + ô nhập: lấy từ NGUỒN CHUNG ────────────────────────────────────────
// Trước đây định nghĩa ngay trong file này; đã tách sang components/workflow-options.jsx
// để trang sơ đồ (/flow-preview) sửa ĐÚNG bộ cấu hình này, không dựng bộ form thứ hai.
// ⚠️ File đó PHẢI được nạp TRƯỚC file này (index.html + bundle-entry.js).
const {
  INTERVAL_OPTIONS, WORKFLOW_OPTIONS, OPTION_GROUPS,
  optVisible, optionDefaults, OptionControl,
} = window.tourkitWorkflowOptions;

// Workflow chạy chậm (review/cảnh báo) — chỉ dùng để hiện hint "quét ≠ review lại mỗi lần".
const SLOW_WORKFLOWS = ['deal-auto-review', 'customer-auto-review'];
// Interval khởi tạo: đã cấu hình → giá trị lưu; chưa → mặc định 15 phút.
function initialInterval(wf) {
  return wf.intervalMinutes || 15;
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

// ─── CRM Queue Monitor ("Hàng đợi CRM") ────────────────────────────────────────────
// Theo dõi hành động CRM (giao việc / lịch hẹn) mà trợ lý đã enqueue vào dbo.CrmActionQueue
// (xem Services/Crm/CrmActionQueueRepository.cs). Worker app-side (toutkit-app) drain Pending
// → tạo trong CRM thật → cập nhật Status. Pattern fetch/table giống RunHistoryTable ở trên.

const CRM_QUEUE_KIND_LABEL = {
  'assign-task': 'Giao việc',
  'create-appointment': 'Lịch hẹn',
};

const CRM_QUEUE_STATUS = {
  0: { label: 'Chờ ⏳',        cls: 'workflows-badge-pending' },
  1: { label: 'Đang xử lý',   cls: 'workflows-badge-processing' },
  2: { label: 'Xong ✅',       cls: 'workflows-badge-ok' },
  3: { label: 'Lỗi ❌',        cls: 'workflows-badge-fail' },
};

// Tóm tắt nội dung từ payloadJson (an toàn, không throw khi JSON hỏng).
function crmQueuePayloadLabel(item) {
  let p;
  try { p = JSON.parse(item.payloadJson || '{}'); } catch { p = {}; }
  if (item.kind === 'assign-task') return p.name || '—';
  if (item.kind === 'create-appointment') return p.careTitle || '—';
  return p.name || p.careTitle || '—';
}

function CrmQueueTable({ items, loading }) {
  if (loading) return <div className="workflows-history-loading">Đang tải...</div>;
  if (!items || items.length === 0) return <div className="workflows-history-empty">Chưa có hành động CRM nào.</div>;
  return (
    <div className="workflows-history-wrap">
      <table className="workflows-history-table">
        <thead>
          <tr>
            <th>Loại</th>
            <th>Nội dung</th>
            <th>Trạng thái</th>
            <th>Thời gian</th>
            <th>Lỗi</th>
          </tr>
        </thead>
        <tbody>
          {items.map(it => {
            const st = CRM_QUEUE_STATUS[it.status] || CRM_QUEUE_STATUS[0];
            return (
              <tr key={it.id} className={it.status === 3 ? 'workflows-run-failed' : ''}>
                <td>{CRM_QUEUE_KIND_LABEL[it.kind] || it.kind}</td>
                <td>{crmQueuePayloadLabel(it)}</td>
                <td><span className={'workflows-badge ' + st.cls}>{st.label}</span></td>
                <td className="workflows-run-ts" title={it.createdUtc}>{relativeTime(it.createdUtc)}</td>
                <td className="workflows-run-error-text">{it.status === 3 && it.errorMessage ? it.errorMessage : '—'}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function CrmQueueCard() {
  const [items, setItems] = uS([]);
  const [loading, setLoading] = uS(true);
  const [error, setError] = uS(null);
  const [statusFilter, setStatusFilter] = uS('');   // '' = tất cả

  const load = uCB(async () => {
    setLoading(true);
    try {
      const qs = statusFilter !== '' ? `&status=${statusFilter}` : '';
      const data = await apiFetch(`/api/v1/workflows/crm-queue?limit=50${qs}`);
      setItems(data.items || []);
      setError(null);
    } catch (e) {
      setError('Không tải được hàng đợi CRM: ' + e.message);
    } finally {
      setLoading(false);
    }
  }, [statusFilter]);

  uE(() => { load(); }, [load]);

  return (
    <section className="workflows-group" style={{ marginTop: 22 }}>
      <div className="workflows-group-head">
        <h2 className="workflows-group-title" style={_wfRow}><Icon name="list" size={17} /> Hàng đợi CRM</h2>
        <p className="workflows-group-desc">
          Hành động (giao việc, tạo lịch hẹn) mà trợ lý đã ghi nhận — hệ thống sẽ tự đồng bộ sang CRM.
        </p>
      </div>
      <div className="wga-card" style={{ padding: '14px 18px' }}>
        <div className="workflows-actions" style={{ marginBottom: 10 }}>
          <select className="workflows-select" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
            <option value="">Tất cả trạng thái</option>
            <option value="0">Chờ</option>
            <option value="1">Đang xử lý</option>
            <option value="2">Xong</option>
            <option value="3">Lỗi</option>
          </select>
          <button className="wga-btn ghost" onClick={load} disabled={loading}>
            <Icon name="refresh" size={14} /> {loading ? 'Đang tải...' : 'Làm mới'}
          </button>
        </div>
        {error && <div className="workflows-error">{error}</div>}
        <CrmQueueTable items={items} loading={loading && items.length === 0} />
      </div>
    </section>
  );
}

// ─── ServiceAccountConfig (deal-auto-review) ───────────────────────────────────────
// Workflow PerTenant tự đăng nhập TourKit bằng 1 tài khoản dịch vụ (không cần user online).
// POST validate login + đếm deal trước khi lưu; GET trạng thái (không trả password).

function ServiceAccountConfig({ pushToast, onChange }) {
  const [status, setStatus] = uS(null);
  const [editing, setEditing] = uS(false);   // đã cấu hình → panel tóm tắt; bấm Sửa → hiện form nhập lại
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
        setEditing(false);
        setStatus({ configured: true, username: username.trim() });
        if (onChange) onChange();
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
      if (onChange) onChange();
    } catch (e) {
      pushToast('Xóa thất bại: ' + e.message, 'error');
    } finally { setSaving(false); }
  }

  function startEdit() { setUsername(status?.username || ''); setPassword(''); setEditing(true); }
  function cancelEdit() { setEditing(false); setUsername(''); setPassword(''); }

  const configured = !!(status && status.configured);

  return (
    <div className="workflows-field-row" style={{ alignItems: 'flex-start', flexDirection: 'column', gap: 8 }}>
      <label className="workflows-field-label">Tài khoản tự động {!configured && <span className="req-star">*</span>}</label>

      {configured && !editing ? (
        /* Đã đăng nhập → panel tóm tắt + nút Sửa / Xóa */
        <div className="workflows-sa-panel">
          <span className="workflows-sa-cur" style={_wfRow}>
            <Icon name="check" size={14} /> Đang dùng tài khoản <b>{status.username}</b>
          </span>
          <div style={{ display: 'flex', gap: 8 }}>
            <button className="wga-btn" onClick={startEdit} disabled={saving}><Icon name="edit" size={13} /> Sửa</button>
            <button className="wga-btn ghost" onClick={remove} disabled={saving}><Icon name="close" size={13} /> Xóa</button>
          </div>
        </div>
      ) : (
        /* Chưa cấu hình / đang sửa → form nhập */
        <React.Fragment>
          <div className="workflows-card-substats" style={{ marginBottom: 2 }}>
            Hệ thống dùng tài khoản này để tự đăng nhập và lấy dữ liệu. Nên là tài khoản xem được toàn bộ dữ liệu công ty.
            {status && !configured && <span style={{ color: 'var(--danger, #c0392b)' }}> Chưa cấu hình — workflow chưa chạy được.</span>}
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, width: '100%' }}>
            <input className="workflows-select" placeholder="Tên đăng nhập *" style={{ flex: 1, minWidth: 140 }}
              value={username} onChange={e => setUsername(e.target.value)} />
            <input className="workflows-select" type="password" placeholder="Mật khẩu *" style={{ flex: 1, minWidth: 140 }}
              value={password} onChange={e => setPassword(e.target.value)} />
            <button className="wga-btn primary" onClick={save} disabled={saving}>
              {saving ? 'Đang kiểm tra...' : (configured ? 'Cập nhật' : 'Lưu & kiểm tra')}
            </button>
            {editing && (
              <button className="wga-btn ghost" onClick={cancelEdit} disabled={saving}>Hủy</button>
            )}
          </div>
        </React.Fragment>
      )}
    </div>
  );
}


// ─── WorkflowCard ────────────────────────────────────────────────────────────────

// Tac vu ban tin: phan nguoi dung quan tam (nhan gi, may gio, o dau) la CUA RIENG HO, giong het
// hop thu (mail-auto-sync). Con lich chay + tan suat la cap cong ty nen can quyen xem cau hinh.
const BRIEF_WORKFLOWS = ['sale-brief', 'ceo-brief'];

function WorkflowCard({ wf, onUpdate, pushToast, locked, canConfig = true, digestSub, onDigestSaved }) {
  const isBrief = BRIEF_WORKFLOWS.includes(wf.type);
  // Chi an phan LICH CHAY khi thieu quyen. Voi the ban tin thi van con khoi "Ban tin cua toi".
  const showSchedule = canConfig;
  const [enabled, setEnabled] = uS(wf.enabled);
  const [interval, setInterval] = uS(initialInterval(wf));
  const [saving, setSaving] = uS(false);
  const [running, setRunning] = uS(false);
  const [historyOpen, setHistoryOpen] = uS(false);
  const [runs, setRuns] = uS(null);
  const [runsLoading, setRunsLoading] = uS(false);
  const [expanded, setExpanded] = uS(false);   // list/accordion: mở cấu hình khi bấm dòng
  // Merge default schema + options đã lưu → tránh gửi mảng/giá trị rỗng khi user mới bật.
  const [options, setOptions] = uS({ ...optionDefaults(wf.type), ...(wf.options || {}) });
  // Options ĐỘNG (vd trạng thái deal lấy từ CRM cho user tick chọn).
  const [dynOptions, setDynOptions] = uS({});
  const [dynLoading, setDynLoading] = uS({});

  const optionSchema = WORKFLOW_OPTIONS[wf.type] || [];

  // Tải options động cho card (hiện: trạng thái deal cho deal-auto-review).
  uE(() => {
    if (wf.type !== 'deal-auto-review') return;
    setDynLoading(l => ({ ...l, dealStatuses: true }));
    apiFetch('/api/v1/workflows/deal-statuses')
      .then(d => setDynOptions(o => ({ ...o, dealStatuses: d.items || [] })))
      .catch(() => {})
      .finally(() => setDynLoading(l => ({ ...l, dealStatuses: false })));
  }, [wf.type]);

  // Sync state khi prop thay đổi (sau reload)
  uE(() => {
    setEnabled(wf.enabled);
    setInterval(initialInterval(wf));
    setOptions({ ...optionDefaults(wf.type), ...(wf.options || {}) });
  }, [wf.enabled, wf.intervalMinutes, wf.options]);

  const isSlow = SLOW_WORKFLOWS.includes(wf.type);
  const intervalOptions = INTERVAL_OPTIONS;

  const isPaused = !!wf.pausedReason;

  // Field bắt buộc đang trống? (chỉ tính field đang hiện + có dữ liệu để chọn)
  function reqEmpty(o) {
    const v = options[o.key];
    if (o.type === 'multi') {
      const opts = o.dynamic ? (dynOptions[o.dynamic] || []) : (o.options || []);
      return opts.length > 0 && (!Array.isArray(v) || v.length === 0);   // chỉ bắt buộc khi đã có list để chọn
    }
    if (o.type === 'numbers') return !Array.isArray(v) || v.length === 0;
    return v == null || v === '';
  }

  async function handleSave() {
    // Validate field bắt buộc trước khi lưu.
    const missing = optionSchema.filter(o => o.required && optVisible(o, options) && reqEmpty(o));
    if (missing.length) {
      pushToast('Vui lòng chọn/điền: ' + missing.map(o => o.label).join(', '), 'error');
      return;
    }
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
    try {
      const res = await apiFetch(`/api/v1/workflows/${wf.type}/run-now`, { method: 'POST' });
      if (res.started) {
        pushToast('Đã bắt đầu chạy nền. Bạn có thể rời trang — kết quả sẽ hiện ở "20 lần gần nhất" khi xong (có thể vài phút).', 'info');
        setHistoryOpen(true);
        loadRuns();
        // Tự cập nhật lịch sử + trạng thái card vài lần (workflow chậm có thể 1–3 phút).
        let n = 0;
        const iv = setInterval(() => {
          n++;
          loadRuns();
          onUpdate();
          if (n >= 10) clearInterval(iv);
        }, 20000);
      } else {
        pushToast('Không bắt đầu được: ' + (res.error || 'Lỗi không xác định'), 'error');
      }
    } catch (e) {
      pushToast('Không bắt đầu được: ' + e.message, 'error');
    } finally {
      setRunning(false);
    }
  }

  // Đồng bộ lại TOÀN BỘ (chỉ workflow đồng bộ giá NCC): xóa cứng dữ liệu cũ rồi kéo mới hoàn toàn.
  async function handleFullResync() {
    if (!(await window.appConfirm(
      'Xóa TOÀN BỘ bảng giá NCC đã lưu của công ty rồi kéo lại mới hoàn toàn từ TourKit? Dùng khi nghi ngờ dữ liệu bị lệch.',
      { title: 'Đồng bộ lại toàn bộ', confirmLabel: 'Xóa & kéo lại', danger: true }))) return;
    setRunning(true);
    try {
      const res = await apiFetch('/api/v1/workflows/tour-price-catalog-sync/full-resync', { method: 'POST' });
      if (res.started) {
        pushToast(`Đã xóa ${res.deleted || 0} dòng cũ, đang kéo lại toàn bộ. Kết quả hiện ở "20 lần gần nhất" khi xong (có thể vài phút).`, 'info');
        setHistoryOpen(true);
        loadRuns();
        let n = 0;
        const iv = setInterval(() => { n++; loadRuns(); onUpdate(); if (n >= 10) clearInterval(iv); }, 20000);
      } else {
        pushToast('Không bắt đầu được: ' + (res.error || 'Lỗi không xác định'), 'error');
      }
    } catch (e) {
      pushToast('Không bắt đầu được: ' + e.message, 'error');
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

  // Gom option theo nhóm (opt.group), giữ thứ tự. Option không group → nhóm '' (không tiêu đề).
  function groupedOptions() {
    const visible = optionSchema.filter(opt => optVisible(opt, options));
    const gmap = OPTION_GROUPS[wf.type] || {};
    const groups = [];
    visible.forEach(opt => {
      const gname = gmap[opt.key] || opt.group || '';
      let g = groups.find(x => x.name === gname);
      if (!g) { g = { name: gname, items: [] }; groups.push(g); }
      g.items.push(opt);
    });
    return groups;
  }
  const wideTypes = ['select', 'multi', 'numbers'];

  return (
    <div className={'workflows-rowitem' + (isPaused ? ' is-paused' : '') + (expanded ? ' is-open' : '')}>
      {/* Dòng list (bấm để mở/đóng cấu hình) */}
      <div className="workflows-rowhead" onClick={() => setExpanded(v => !v)}
        role="button" tabIndex={0}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(v => !v); } }}>
        <div className="workflows-rowhead-avatar">
          <Icon name={wf.type === 'sale-brief' ? 'phone' : (wf.type === 'ceo-brief' ? 'trend'
            : (wf.type === 'deal-auto-review' ? 'zap' : (wf.type === 'customer-auto-review' ? 'user' : 'mail')))} size={18} />
        </div>
        <div className="workflows-rowhead-main">
          <div className="workflows-rowhead-name">
            <span>{wf.label}</span>
            {statusPill}
          </div>
          <div className="workflows-rowhead-desc">{wf.description}</div>
          {/* Thông tin lần chạy cuối nằm BÊN TRÁI, dưới mô tả. Trước để ở cột phải thì
              tóm tắt dài (vd "0 review mới · 0 review lại · 104 không đổi") bị bóp lại,
              xuống dòng lộn xộn. Bên trái có nguyên chiều rộng nên thoáng. */}
          {/* The ban tin: dong dau tien nguoi dung can biet la "TOI co nhan khong, may gio" —
              khong phai lan chay cuoi cua he thong. */}
          {isBrief && (
            <div className="workflows-rowhead-meta">
              <span className={'workflows-digest-sum' + (digestSub && digestSub.enabled ? ' is-on' : '')}>
                {window.digestSummary ? window.digestSummary(digestSub) : ''}
              </span>
              {digestSub && digestSub.enabled && !enabled && (
                <span className="workflows-digest-warn">· công ty chưa bật lịch gửi</span>
              )}
            </div>
          )}
          <div className="workflows-rowhead-meta">
            {wf.lastRunUtc
              ? <span style={_wfRow}>
                  {wf.lastRunStatus === 'failed' && <Icon name="close" size={12} />}
                  {relativeTime(wf.lastRunUtc)}
                  {wf.lastRunSummary && <SummaryText summaryJson={wf.lastRunSummary} />}
                </span>
              : <span className="workflows-rowhead-muted">Chưa chạy</span>}
          </div>
        </div>
        {/* stopPropagation: bấm nút không kéo theo mở/đóng khối cấu hình bên dưới. */}
        <a className="workflows-rowhead-flow" href={'#/flow-preview/' + wf.type}
           onClick={e => e.stopPropagation()}
           title={'Xem sơ đồ hoạt động của "' + wf.label + '"'}>
          <Icon name="grip" size={14} />
          <span>Xem sơ đồ</span>
        </a>
        <span className="workflows-rowhead-chev"><Icon name={expanded ? 'chevronUp' : 'chevronDown'} size={16} /></span>
      </div>

      {/* Paused banner — luôn hiện khi tạm dừng (không cần mở) */}
      {isPaused && (
        <div className="workflows-paused-banner">
          <span style={_wfRow}><Icon name="warning" size={14} /> Đã tạm dừng: {wf.pausedReason}</span>
          <button className="wga-btn" onClick={handleReEnable} disabled={saving}>
            {saving ? 'Đang bật...' : 'Bật lại'}
          </button>
        </div>
      )}

      {/* Body cấu hình — chỉ render khi mở */}
      {expanded && (
        <div className="workflows-rowbody">
          {locked && (
            <div className="workflows-locked-banner">
              <Icon name="warning" size={14} /> Cần cấu hình <b>Tài khoản dịch vụ</b> (khối phía trên) trước khi bật workflow này.
            </div>
          )}
          <div className="workflows-rowbody-config">
            {/* Khoi rieng cua nguoi dung — dat TRUOC lich chay vi day la thu ho vao de lam. */}
            {isBrief && window.DigestSubBlock && (
              <window.DigestSubBlock briefType={wf.type} sub={digestSub}
                onSaved={onDigestSaved} pushToast={pushToast}
                scheduleOn={enabled && !isPaused} />
            )}
            {/* Nhóm "Lịch chạy" — bật/tắt + tần suất. Can quyen xem cau hinh (cap cong ty). */}
            {showSchedule && (
            <div className="workflows-optgroup">
              <div className="workflows-optgroup-title">Lịch chạy</div>
              <div className="workflows-opt is-toggle">
                <div className="workflows-opt-row">
                  <label className="workflows-opt-label">Bật workflow</label>
                  <div className="workflows-opt-control">
                    <div className="workflows-toggle-wrap">
                      <label className="workflows-toggle">
                        <input type="checkbox" checked={enabled} disabled={locked}
                          onChange={e => setEnabled(e.target.checked)} />
                        <span className="workflows-toggle-track" />
                      </label>
                      <span className="workflows-toggle-label">{enabled ? 'Bật' : 'Tắt'}</span>
                    </div>
                  </div>
                </div>
              </div>
              <div className="workflows-opt is-wide">
                <div className="workflows-opt-row">
                  <label className="workflows-opt-label">Tần suất kiểm tra</label>
                  <div className="workflows-opt-control">
                    <select className="workflows-select workflows-opt-input" value={interval}
                      onChange={e => setInterval(Number(e.target.value))}>
                      {intervalOptions.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                  </div>
                </div>
                {isSlow && (
                  <div className="workflows-opt-hint">
                    Bao lâu hệ thống tự chạy một lần. Mỗi lần chạy chỉ xử lý phần mới hoặc vừa thay đổi, nên đặt chạy thường xuyên cũng không tốn thêm.
                  </div>
                )}
                {isBrief && (
                  <div className="workflows-opt-hint">
                    Lịch này của CẢ CÔNG TY: hệ thống chạy mỗi N phút rồi tự gửi cho ai đến giờ họ chọn.
                    Giờ nhận riêng của bạn đặt ở khối “Bản tin của tôi” phía trên.
                  </div>
                )}
              </div>
            </div>
            )}

            {/* Option ĐỘNG theo nhóm */}
            {showSchedule && groupedOptions().map((g, gi) => (
              <div className="workflows-optgroup" key={g.name || ('g' + gi)}>
                {g.name && <div className="workflows-optgroup-title">{g.name}</div>}
                {g.items.map(opt => (
                  <div className={'workflows-opt' + (opt.type === 'bool' ? ' is-toggle' : '') + (wideTypes.includes(opt.type) ? ' is-wide' : '')} key={opt.key}>
                    <div className="workflows-opt-row">
                      <label className="workflows-opt-label">{opt.label}{opt.required && <span className="req-star">*</span>}</label>
                      <div className="workflows-opt-control">
                    <OptionControl opt={opt} options={options} setOptions={setOptions}
                      dynOptions={dynOptions} dynLoading={dynLoading} />
                  </div>
                    </div>
                    {opt.hint && <div className="workflows-opt-hint">{opt.hint}</div>}
                  </div>
                ))}
              </div>
            ))}
          </div>

          {/* Thống kê lần gần nhất */}
          {showSchedule && (wf.lastRunUtc || wf.nextRunUtc) && (
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

          {/* Actions — thao tac cap cong ty */}
          {showSchedule && (
          <div className="workflows-actions">
            <button className="wga-btn primary" onClick={handleSave} disabled={saving || running || locked}>
              <Icon name="save" size={14} /> {saving ? 'Đang lưu...' : 'Lưu cấu hình'}
            </button>
            <button className="wga-btn" onClick={handleRunNow} disabled={running || saving || locked}>
              <Icon name="refresh" size={14} /> {running ? 'Đang chạy...' : 'Chạy ngay'}
            </button>
            {wf.type === 'tour-price-catalog-sync' && (
              <button className="wga-btn" onClick={handleFullResync} disabled={running || saving || locked}
                title="Xóa toàn bộ bảng giá đã lưu rồi kéo lại mới hoàn toàn từ TourKit">
                <Icon name="trash" size={14} /> Đồng bộ lại toàn bộ
              </button>
            )}
            <button className="wga-btn ghost" onClick={toggleHistory}>
              <Icon name="list" size={14} /> {historyOpen ? 'Ẩn lịch sử' : '20 lần gần nhất'}
              <Icon name={historyOpen ? 'chevronUp' : 'chevronDown'} size={13} />
            </button>
          </div>
          )}

          {/* Run history */}
          {showSchedule && historyOpen && (
            <div className="workflows-history">
              <RunHistoryTable runs={runs} loading={runsLoading} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ─── WorkflowsPage ────────────────────────────────────────────────────────────────

function WorkflowsPage({ pushToast, initialTab }) {
  // 2 tab thay vi 2 trang rieng (chot 12/08): dang ky nhan ban tin CHINH LA cau hinh cua tac vu
  // sale-brief/ceo-brief, con bang tin la ket qua cua chinh cac tac vu do. Tach ra trang rieng thi
  // nguoi dung phai nho 2 noi cho cung mot viec.
  // Cum ban tin nam sau co Features:Digest. Tat thi trang chi con 1 tab "Tac vu", va 3 the ban
  // tin/canh bao tu bien mat vi backend khong dang ky workflow nua (khong loc gi o day).
  const digestOn = window.tourkitFeatures.useFeature('digest');
  const [tabState, setTab] = uS(initialTab === 'insights' ? 'insights' : 'tasks');
  // Duong /insights cu van tro vao day. Tat co thi ep ve tab "Tac vu" thay vi hien tab rong —
  // dan xuat chu khong setState, de khi co bat len lai thi lua chon cua nguoi dung con nguyen.
  const tab = digestOn ? tabState : 'tasks';
  const [unread, setUnread] = uS(0);
  const [digestSubs, setDigestSubs] = uS([]);
  const [workflows, setWorkflows] = uS([]);
  const [loading, setLoading] = uS(true);
  const [error, setError] = uS(null);
  const [saConfigured, setSaConfigured] = uS(null);   // null = chưa biết; false = chưa cấu hình tài khoản dịch vụ
  const canConfig = window.tourkitAuth.hasPermission('CH_HT_XEM');

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

  async function loadSa() {
    try { const d = await apiFetch('/api/v1/workflows/service-account'); setSaConfigured(!!d.configured); }
    catch { setSaConfigured(false); }
  }

  // Dang ky ban tin cua CHINH minh — khong can quyen gi (giong hop thu ca nhan).
  async function loadDigest() {
    try {
      const d = await apiFetch('/api/v1/digest/subscriptions');
      setDigestSubs(d.items || []);
    } catch { /* thieu thi the ban tin coi nhu chua dang ky, khong lam vo trang */ }
  }
  async function loadUnread() {
    try {
      const d = await apiFetch('/api/v1/insights/unread-count');
      setUnread(d.count || 0);
    } catch {}
  }

  uE(() => { loadWorkflows(); if (canConfig) loadSa(); }, []);
  // Tach rieng khoi lan tai dau: 2 endpoint nay 404 khi tat co, goi la console day loi vo ich.
  uE(() => { if (digestOn) { loadDigest(); loadUnread(); } }, [digestOn]);

  // So chua doc doi ngay khi doc tin hoac bam Gui thu (khoi doi chu ky cua chuong).
  uE(() => {
    if (!digestOn) return;
    const onPing = () => loadUnread();
    window.addEventListener('tourkit:insights', onPing);
    return () => window.removeEventListener('tourkit:insights', onPing);
  }, [digestOn]);

  // KPI — tính từ danh sách hiện tại
  const running = workflows.filter(w => w.enabled && !w.pausedReason).length;
  const paused = workflows.filter(w => !!w.pausedReason).length;
  const lastRunMs = workflows.reduce((acc, w) => {
    if (!w.lastRunUtc) return acc;
    const t = new Date(w.lastRunUtc).getTime();
    return (acc == null || t > acc) ? t : acc;
  }, null);

  return (
    <main className="page wga workflows-page">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">{digestOn ? 'Tự động · Bản tin' : 'Tự động'}</div>
          <h1>Tự động hóa</h1>
          <p className="wga-sub">Các tác vụ AI chạy nền theo lịch. Bật một lần, hệ thống tự làm đều đặn, bạn chỉ vào xem kết quả.</p>
        </div>
      </div>

      {/* 2 tab: cấu hình ở "Tác vụ", kết quả ở "Bảng tin" — cùng một trang để không phải nhớ 2 nơi.
          Tắt cờ bản tin thì còn đúng 1 tab → ẩn luôn thanh tab, chứ 1 tab đứng trơ trọi nhìn như lỗi. */}
      {digestOn && (
        <div className="workflows-tabs" role="tablist">
          <button role="tab" aria-selected={tab === 'tasks'}
            className={'workflows-tab' + (tab === 'tasks' ? ' is-on' : '')}
            onClick={() => setTab('tasks')}>
            <Icon name="zap" size={14} /> Tác vụ
          </button>
          <button role="tab" aria-selected={tab === 'insights'}
            className={'workflows-tab' + (tab === 'insights' ? ' is-on' : '')}
            onClick={() => setTab('insights')}>
            <Icon name="bell" size={14} /> Bảng tin
            {unread > 0 && <span className="workflows-tab-badge">{unread > 99 ? '99+' : unread}</span>}
          </button>
        </div>
      )}

      {tab === 'insights' && (window.InsightsFeed
        ? <window.InsightsFeed pushToast={pushToast} />
        : <div className="wga-empty"><p>Chưa nạp được Bảng tin.</p></div>)}

      {tab === 'tasks' && !loading && !error && workflows.length > 0 && (
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

      {tab === 'tasks' && loading && <div className="wga-loading">Đang tải…</div>}

      {tab === 'tasks' && error && (
        <div className="wga-empty">
          <p>{error}</p>
          <button className="wga-btn" onClick={loadWorkflows} style={{ marginTop: 14 }}>
            <Icon name="refresh" size={14} /> Thử lại
          </button>
        </div>
      )}

      {tab === 'tasks' && !loading && !error && workflows.length === 0 && (
        <div className="wga-empty">
          <div className="wga-empty-icon"><Icon name="zap" size={48} /></div>
          <h3>Chưa có tác vụ tự động</h3>
          <p>Khi có tác vụ khả dụng, bạn có thể bật lịch chạy nền tại đây.</p>
        </div>
      )}

      {tab === 'tasks' && !loading && !error && workflows.length > 0 && (() => {
        // The ban tin xep vao nhom "Theo nguoi dung" du scope o backend la PerTenant: cai nguoi
        // dung dat o day la NOI NHAN CUA RIENG HO. Lich chay cap cong ty van o trong the, chi hien
        // cho nguoi co quyen xem cau hinh.
        const isBriefWf = w => BRIEF_WORKFLOWS.includes(w.type);
        const perUser = workflows.filter(w => w.scope === 'PerUser' || isBriefWf(w));
        const perTenant = canConfig
          ? workflows.filter(w => w.scope === 'PerTenant' && !isBriefWf(w))
          : [];
        const subOf = t => digestSubs.find(x => x.briefType === t) || null;
        const renderCards = (list, locked) => (
          <div className="workflows-listview">
            {list.map(wf => (
              <WorkflowCard key={wf.type} wf={wf} onUpdate={loadWorkflows} pushToast={pushToast}
                locked={locked} canConfig={canConfig}
                digestSub={subOf(wf.type)} onDigestSaved={loadDigest} />
            ))}
          </div>
        );
        return (
          <React.Fragment>
            {perUser.length > 0 && (
              <section className="workflows-group">
                <div className="workflows-group-head">
                  <h2 className="workflows-group-title" style={_wfRow}><Icon name="user" size={17} /> Theo người dùng</h2>
                  <p className="workflows-group-desc">Mỗi nhân viên tự bật cho riêng mình, dùng hộp thư và dữ liệu của chính mình.</p>
                </div>
                {renderCards(perUser, false)}
              </section>
            )}
            {perTenant.length > 0 && (
              <section className="workflows-group" style={{ marginTop: 22 }}>
                <div className="workflows-group-head">
                  <h2 className="workflows-group-title" style={_wfRow}><Icon name="users" size={17} /> Theo tổ chức (cả công ty)</h2>
                  <p className="workflows-group-desc">Cấu hình một lần cho cả công ty. Hệ thống tự chạy bằng <b>tài khoản dịch vụ</b> bên dưới, không cần ai đăng nhập sẵn.</p>
                </div>
                <div className={'wga-card' + (saConfigured === false ? ' workflows-sa-needed' : '')} style={{ padding: '14px 18px', marginBottom: 14 }}>
                  <ServiceAccountConfig pushToast={pushToast} onChange={loadSa} />
                  {/* Khoi khai OA Zalo da GO (14/08): Zalo gui bang ZNS qua OA cua ben cung cap
                      dich vu, khai o config he thong -> tung cong ty khong con phai khai gi.
                      Truoc day moi cong ty phai tu khai OA vi tin Zalo tinh tien theo tung OA;
                      nay ben minh chiu chi phi nen gop ve mot moi. */}
                </div>
                {renderCards(perTenant, saConfigured === false)}
              </section>
            )}
          </React.Fragment>
        );
      })()}

      {tab === 'tasks' && <CrmQueueCard />}
    </main>
  );
}

window.WorkflowsPage = WorkflowsPage;
