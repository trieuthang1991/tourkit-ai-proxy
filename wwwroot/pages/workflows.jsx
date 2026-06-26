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
// type 'bool' → toggle. Khớp shape OptionsJson backend (vd mail-auto-sync: { autoReply: bool }).
const WORKFLOW_OPTIONS = {
  'mail-auto-sync': [
    { key: 'autoReply', type: 'bool', label: 'Tự động trả lời',
      hint: 'AI soạn + gửi trả lời tự động cho email mới (đang phát triển — hiện chỉ lưu cấu hình)' },
  ],
};

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

// ─── WorkflowCard ────────────────────────────────────────────────────────────────

function WorkflowCard({ wf, onUpdate, pushToast }) {
  const [enabled, setEnabled] = uS(wf.enabled);
  const [interval, setInterval] = uS(wf.intervalMinutes || 15);
  const [saving, setSaving] = uS(false);
  const [running, setRunning] = uS(false);
  const [historyOpen, setHistoryOpen] = uS(false);
  const [runs, setRuns] = uS(null);
  const [runsLoading, setRunsLoading] = uS(false);
  const [options, setOptions] = uS(wf.options || {});   // điều kiện ĐỘNG per-workflow

  const optionSchema = WORKFLOW_OPTIONS[wf.type] || [];

  // Sync state khi prop thay đổi (sau reload)
  uE(() => {
    setEnabled(wf.enabled);
    setInterval(wf.intervalMinutes || 15);
    setOptions(wf.options || {});
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
        const msg = s ? `Hoàn thành: ${s.classified ?? 0} mail mới phân loại` : 'Hoàn thành';
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
          <Icon name="mail" size={20} />
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

        {/* Điều kiện/option ĐỘNG per-workflow (render từ WORKFLOW_OPTIONS) */}
        {optionSchema.map(opt => opt.type === 'bool' ? (
          <div className="workflows-field-row" key={opt.key} style={{ alignItems: 'flex-start' }}>
            <label className="workflows-field-label">{opt.label}</label>
            <div>
              <div className="workflows-toggle-wrap">
                <label className="workflows-toggle">
                  <input type="checkbox" checked={!!options[opt.key]}
                    onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.checked }))} />
                  <span className="workflows-toggle-track" />
                </label>
                <span className="workflows-toggle-label">{options[opt.key] ? 'Bật' : 'Tắt'}</span>
              </div>
              {opt.hint && <div className="workflows-card-substats" style={{ marginTop: 4 }}>{opt.hint}</div>}
            </div>
          </div>
        ) : null)}

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
