// pages/workflows.jsx — Trang "Tự động" (User Workflows)
// Cho user cấu hình tác vụ AI chạy tự động theo lịch (interval).
// V1 built-in: mail-auto-sync (đồng bộ Gmail mỗi N phút).
// Pattern: X-Session-Id header + authedFetch giống mail.jsx / assistant.jsx.
'use strict';

const { useState: uS, useEffect: uE, useCallback: uCB } = React;

// ─── Helpers ────────────────────────────────────────────────────────────────────

function sessionId() {
  try { return localStorage.getItem('tourkit_tk_session') || ''; } catch { return ''; }
}

function authHeaders() {
  const sid = sessionId();
  return sid ? { 'X-Session-Id': sid, 'Content-Type': 'application/json' } : { 'Content-Type': 'application/json' };
}

async function apiFetch(path, opts = {}) {
  const r = await fetch(path, {
    ...opts,
    headers: { ...authHeaders(), ...(opts.headers || {}) },
  });
  if (!r.ok) {
    let msg = `HTTP ${r.status}`;
    try { const j = await r.json(); msg = j.error || msg; } catch {}
    throw new Error(msg);
  }
  return r.json();
}

function relativeTime(utcStr) {
  if (!utcStr) return '—';
  const diff = (Date.now() - new Date(utcStr).getTime()) / 1000;
  if (diff < 60) return `${Math.round(diff)} giây trước`;
  if (diff < 3600) return `${Math.round(diff / 60)} phút trước`;
  if (diff < 86400) return `${Math.round(diff / 3600)} giờ trước`;
  return `${Math.round(diff / 86400)} ngày trước`;
}

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

// ─── Run History Table ───────────────────────────────────────────────────────────

function RunHistoryTable({ runs, loading }) {
  const [expandedError, setExpandedError] = uS(null);
  if (loading) return <div className="workflows-history-loading">Đang tải lịch sử...</div>;
  if (!runs || runs.length === 0) return <div className="workflows-history-empty">Chưa có lịch sử chạy.</div>;
  return (
    <div className="workflows-history-wrap">
      <table className="quota-table workflows-history-table">
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
                <td>{r.triggerKind === 'manual' ? '🖱 Thủ công' : '⏱ Lịch'}</td>
                <td>
                  {r.status === 'ok'
                    ? <span className="workflows-badge workflows-badge-ok">✓ Thành công</span>
                    : <span className="workflows-badge workflows-badge-fail">✗ Lỗi</span>}
                </td>
                <td><SummaryText summaryJson={r.summary} /></td>
                <td>{r.durationMs != null ? `${(r.durationMs / 1000).toFixed(1)}s` : '—'}</td>
              </tr>
              {expandedError === r.id && r.error && (
                <tr>
                  <td colSpan={5} className="workflows-run-error-row">
                    <span className="workflows-run-error-text">⚠ {r.error}</span>
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

  // Sync state khi prop thay đổi (sau reload)
  uE(() => {
    setEnabled(wf.enabled);
    setInterval(wf.intervalMinutes || 15);
  }, [wf.enabled, wf.intervalMinutes]);

  const isPaused = !!wf.pausedReason;

  async function handleSave() {
    setSaving(true);
    try {
      await apiFetch(`/api/v1/workflows/${wf.type}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, intervalMinutes: interval }),
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

  // Trạng thái pill
  let statusPill;
  if (isPaused) {
    statusPill = <span className="workflows-pill workflows-pill-paused">⏸ Tạm dừng</span>;
  } else if (enabled) {
    statusPill = <span className="workflows-pill workflows-pill-on">● Đang chạy</span>;
  } else {
    statusPill = <span className="workflows-pill workflows-pill-off">○ Tắt</span>;
  }

  return (
    <div className={'quota-card workflows-card' + (isPaused ? ' workflows-card-paused' : '')}>
      <div className="workflows-card-header">
        <div className="workflows-card-title-row">
          <span className="workflows-icon">📧</span>
          <span className="workflows-card-title">{wf.label}</span>
          {statusPill}
        </div>
        <p className="workflows-card-desc">{wf.description}</p>
      </div>

      {/* Paused banner */}
      {isPaused && (
        <div className="workflows-paused-banner">
          <span>⚠ Đã tạm dừng: {wf.pausedReason}</span>
          <button className="workflows-btn workflows-btn-sm workflows-btn-warn"
            onClick={handleReEnable} disabled={saving}>
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

        {/* Thống kê lần gần nhất */}
        {(wf.lastRunUtc || wf.nextRunUtc) && (
          <div className="workflows-meta">
            {wf.lastRunUtc && (
              <div className="workflows-meta-item">
                <span className="workflows-meta-label">Lần chạy cuối:</span>
                <span className={'workflows-meta-val' + (wf.lastRunStatus === 'failed' ? ' workflows-meta-fail' : '')}>
                  {relativeTime(wf.lastRunUtc)}
                  {wf.lastRunStatus === 'ok' ? ' · ✓' : wf.lastRunStatus === 'failed' ? ' · ✗' : ''}
                  {wf.lastRunSummary && <SummaryText summaryJson={wf.lastRunSummary} />}
                </span>
              </div>
            )}
            {wf.nextRunUtc && enabled && !isPaused && (
              <div className="workflows-meta-item">
                <span className="workflows-meta-label">Lần kế tiếp:</span>
                <span className="workflows-meta-val">{futureTime(wf.nextRunUtc)}</span>
              </div>
            )}
          </div>
        )}

        {/* Actions */}
        <div className="workflows-actions">
          <button className="workflows-btn workflows-btn-primary"
            onClick={handleSave} disabled={saving || running}>
            {saving ? 'Đang lưu...' : 'Lưu cấu hình'}
          </button>
          <button className="workflows-btn workflows-btn-secondary"
            onClick={handleRunNow} disabled={running || saving}>
            {running ? '⏳ Đang chạy...' : '▶ Chạy ngay'}
          </button>
          <button className="workflows-btn workflows-btn-ghost"
            onClick={toggleHistory}>
            📜 {historyOpen ? 'Ẩn lịch sử' : '20 lần gần nhất'} {historyOpen ? '▲' : '▼'}
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

  return (
    <main className="page">
      <div className="page-header">
        <h1 className="page-title">⚙️ Tự động hóa</h1>
        <p className="page-subtitle">Cấu hình tác vụ AI chạy tự động theo lịch</p>
      </div>

      {loading && (
        <div className="workflows-loading">Đang tải...</div>
      )}

      {error && (
        <div className="workflows-error">
          {error}
          <button className="workflows-btn workflows-btn-sm" onClick={loadWorkflows}
            style={{ marginLeft: 12 }}>Thử lại</button>
        </div>
      )}

      {!loading && !error && workflows.length === 0 && (
        <div className="workflows-empty">Chưa có workflow nào được cấu hình.</div>
      )}

      <div className="workflows-list">
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
