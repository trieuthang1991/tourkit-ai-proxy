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
  optVisible, optionDefaults, dynamicDefaults, OptionControl, OptHelp,
} = window.tourkitWorkflowOptions;

// Workflow chạy chậm (review/cảnh báo) — chỉ dùng để hiện hint "quét ≠ review lại mỗi lần".
const SLOW_WORKFLOWS = ['deal-auto-review', 'customer-auto-review'];

// Workflow có option kiểu "chọn trạng thái cơ hội" → cần tải danh sách trạng thái TỪ CRM của chính
// công ty để người dùng tick. Đây là cách tránh đoán: mỗi CRM tự đặt tên trạng thái, hardcode từ
// khoá tiếng Việt thì kiểu gì cũng có công ty sai.
const DEAL_STATUS_WORKFLOWS = ['deal-auto-review', 'sale-brief'];
const TASK_STATUS_WORKFLOWS = ['sale-brief', 'ceo-brief'];
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

// briefPart: thẻ bản tin bị TÁCH LÀM ĐÔI theo đúng ranh giới ai-quyết-cái-gì —
//   'personal' → chỉ "Bản tin của tôi" (tôi có nhận không, mấy giờ, ở đâu). Nằm mục Theo người dùng.
//   'company'  → luật chung: lịch chạy + đưa mục nào vào bản tin + ngưỡng + trạng thái. Một người
//                khai một lần cho cả công ty, nằm mục Theo tổ chức.
// Trước đây nhồi cả hai vào một thẻ nên người đọc phải tự tách "cái này của tôi hay của công ty",
// và nhân viên thường xuyên nhìn thấy cả đống ngưỡng mà họ không có quyền lẫn nhu cầu đụng vào.
function WorkflowCard({ wf, onUpdate, pushToast, locked, canConfig = true, digestSub, onDigestSaved,
                        briefPart = 'all' }) {
  const isBrief = BRIEF_WORKFLOWS.includes(wf.type);
  const showPersonal = !isBrief || briefPart !== 'company';
  const showRules = !isBrief || briefPart !== 'personal';
  // "Công ty đã khai luật chung chưa" = đã có ai bấm Lưu cấu hình cho tác vụ này chưa.
  // Chưa khai thì server từ chối bật nhận (409) — giao diện phải nói trước, đừng để bấm rồi mới báo.
  // Tác vụ KHÔNG có luật nào để khai (vd bản tin điều hành) thì luôn coi là sẵn sàng: bắt khai một
  // thứ không tồn tại thì người dùng không bao giờ bật nhận được. Điều kiện này phải khớp
  // IScheduledWorkflow.HasCompanyRules bên C# — lệch nhau là giao diện chặn còn server cho, hoặc ngược lại.
  // Đọc thẳng WORKFLOW_OPTIONS chứ KHÔNG dùng biến optionSchema: biến đó khai bằng const ở dưới,
  // dùng trước là "Cannot access before initialization" — trắng nguyên trang (đã dính một lần).
  const companyReady = ((WORKFLOW_OPTIONS[wf.type] || []).length === 0)
    || !!(wf.options && Object.keys(wf.options).length > 0);
  // Chi an phan LICH CHAY khi thieu quyen. Voi the ban tin thi van con khoi "Ban tin cua toi".
  const showSchedule = canConfig && showRules;
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
  // Gợi ý "trạng thái nào còn phải làm" do MÁY CHỦ tính (AI đọc tên trạng thái của công ty này).
  const [dynSuggested, setDynSuggested] = uS({});

  const optionSchema = WORKFLOW_OPTIONS[wf.type] || [];

  // Tải options động cho card: danh sách trạng thái cơ hội LẤY TỪ CRM của chính công ty, để người
  // dùng TICK CHỌN thay vì mình đoán hộ bằng từ khoá tiếng Việt (mỗi CRM tự đặt tên trạng thái).
  // Mỗi danh sách là một lời gọi riêng: trạng thái cơ hội và trạng thái công việc nằm ở hai
  // nơi khác nhau trong CRM, và không phải tác vụ nào cũng cần cả hai.
  function dynSources() {
    const out = [];
    if (DEAL_STATUS_WORKFLOWS.includes(wf.type)) out.push(['dealStatuses', '/api/v1/workflows/deal-statuses']);
    if (TASK_STATUS_WORKFLOWS.includes(wf.type)) out.push(['taskStatuses', '/api/v1/workflows/task-statuses']);
    return out;
  }

  // refresh=true → bỏ bản đã lưu, nhờ AI đọc lại tên trạng thái VÀ áp kết quả mới vào ô chọn
  // (người dùng bấm "Phân loại lại" là đang muốn lấy kết quả mới, không phải chỉ xem).
  function loadDynStatuses(refresh) {
    dynSources().forEach(([key, url]) => {
      setDynLoading(l => ({ ...l, [key]: true }));
      apiFetch(url + (refresh ? '?refresh=1' : ''))
        .then(d => {
          setDynOptions(o => ({ ...o, [key]: d.items || [] }));
          // openSuggested có thể vắng (AI lỗi / chưa khai khoá model) → client tự đoán theo tên.
          if (Array.isArray(d.openSuggested) && d.openSuggested.length) {
            setDynSuggested(s => ({ ...s, [key]: d.openSuggested, [key + ':src']: d.hintSource }));
            if (refresh) {
              const keys = (WORKFLOW_OPTIONS[wf.type] || [])
                .filter(o => o.dynamic === key && o.dynamicDefault).map(o => o.key);
              setOptions(o => {
                const next = { ...o };
                keys.forEach(k => { next[k] = d.openSuggested; });
                return next;
              });
              pushToast('Đã phân loại lại — nhớ bấm "Lưu cấu hình" nếu thấy đúng');
            }
          }
        })
        .catch(() => {})
        .finally(() => setDynLoading(l => ({ ...l, [key]: false })));
    });
  }

  uE(() => { loadDynStatuses(false); }, [wf.type]);

  // Sync state khi prop thay đổi (sau reload)
  uE(() => {
    setEnabled(wf.enabled);
    setInterval(initialInterval(wf));
    setOptions({ ...optionDefaults(wf.type), ...(wf.options || {}) });
  }, [wf.enabled, wf.intervalMinutes, wf.options]);

  // Default ĐỘNG: option nào chọn từ danh sách của CRM (vd trạng thái cơ hội) thì chỉ điền mặc
  // định được SAU khi danh sách về. Chạy lại cả khi wf.options đổi vì effect đồng bộ ở trên vừa
  // đặt lại options về bản đã lưu — thiếu thì mặc định bị xoá ngay sau khi Lưu.
  uE(() => {
    setOptions(o => {
      const patch = dynamicDefaults(wf.type, o, dynOptions, dynSuggested);
      return Object.keys(patch).length ? { ...o, ...patch } : o;
    });
  }, [dynOptions, dynSuggested, wf.options]);

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
    statusPill = <span className="wga-pill crm">{isBrief ? 'Đang gửi' : 'Đang chạy'}</span>;
  } else {
    statusPill = <span className="wga-pill faq">{isBrief ? 'Chưa gửi' : 'Tắt'}</span>;
  }

  // Bật lịch gửi ngay từ dòng phán quyết — KHÔNG bắt cuộn xuống tìm công tắc khác rồi bấm một
  // nút Lưu khác. Chỗ báo thiếu và chỗ sửa được thiếu đó phải là một.
  async function handleEnableSchedule() {
    setSaving(true);
    try {
      await apiFetch(`/api/v1/workflows/${wf.type}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled: true, intervalMinutes: interval, options }),
      });
      setEnabled(true);
      pushToast('Đã bật lịch gửi cho cả công ty');
      onUpdate();
    } catch (e) {
      pushToast('Bật lịch gửi thất bại: ' + e.message, 'error');
    } finally {
      setSaving(false);
    }
  }

  // ── Dòng phán quyết của thẻ bản tin ────────────────────────────────────────────
  //
  // Bản tin cần ĐỒNG THỜI hai công tắc: đăng ký của riêng bạn VÀ lịch gửi của công ty. Trước đây
  // hai thứ đó hiện thành hai dòng rời nhau — huy hiệu "TẮT" (nói về công ty) nằm ngay trên
  // "Bạn nhận lúc 21:00" (nói về bạn) — nên không dòng nào trả lời được câu hỏi duy nhất người
  // đọc có: SÁNG MAI TÔI CÓ NHẬN ĐƯỢC KHÔNG. Nay gộp thành một câu trả lời thẳng, và nếu thiếu
  // vế nào thì đặt ngay nút sửa vế đó vào cạnh câu.
  function briefVerdict() {
    const me = !!(digestSub && digestSub.enabled);
    const co = enabled && !isPaused;
    const hh = String((digestSub && digestSub.sendHourLocal) ?? 7).padStart(2, '0');
    const chans = [];
    if (digestSub) {
      chans.push('trong app');
      if (digestSub.channelEmail) chans.push('email');
      if (digestSub.channelTelegram) chans.push('telegram');
      if (digestSub.channelZalo) chans.push('zalo');
    }
    const stop = e => e.stopPropagation();

    // Chưa ai khai luật chung → nói thẳng, và chỉ đúng chỗ khai. Đây là điều kiện đứng TRƯỚC mọi
    // thứ khác: chưa có luật thì bật nhận cũng vô nghĩa.
    if (!companyReady) return (
      <div className="wf-verdict is-idle">
        <Icon name="info" size={13} />
        <span>Công ty <b>chưa cấu hình</b> bản tin này nên chưa đăng ký nhận được.
          {canConfig
            ? <> Khai ở mục <b>Theo tổ chức (cả công ty)</b> phía dưới rồi bấm Lưu cấu hình.</>
            : <> Nhờ người phụ trách khai giúp ở mục “Theo tổ chức”.</>}
        </span>
      </div>
    );

    // KHÔNG viết "sáng mai": giờ nhận do người dùng chọn trong cả 24 giờ, và 21:00 là lựa chọn
    // hợp lý (đọc tối trước cho sáng hôm sau) — "Sáng mai 21:00" đọc lên là vô nghĩa.
    if (me && co) return (
      <div className="wf-verdict is-ok">
        <Icon name="check" size={13} />
        <span>Mỗi ngày lúc <b>{hh}:00</b> bạn sẽ nhận — qua {chans.join(', ')}.</span>
      </div>
    );
    if (me && !co) return (
      <div className="wf-verdict is-bad" onClick={stop}>
        <Icon name="warning" size={13} />
        <span>Bạn đã đăng ký, nhưng <b>công ty chưa bật lịch gửi</b> nên sẽ không có gì tới.</span>
        {canConfig
          ? <button className="wga-btn primary wf-verdict-btn" onClick={handleEnableSchedule} disabled={saving}>
              {saving ? 'Đang bật…' : 'Bật lịch gửi'}
            </button>
          : <span className="wf-verdict-note">Nhờ người quản trị bật giúp.</span>}
      </div>
    );
    if (!me && co) return (
      <div className="wf-verdict is-idle">
        <Icon name="info" size={13} />
        <span>Công ty đang gửi bản tin này, nhưng <b>bạn chưa đăng ký nhận</b>. Mở thẻ để chọn giờ và nơi nhận.</span>
      </div>
    );
    return (
      <div className="wf-verdict is-idle">
        <Icon name="info" size={13} />
        <span>Chưa ai nhận bản tin này. Mở thẻ để đăng ký cho riêng bạn.</span>
      </div>
    );
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
  // Ô nào BUỘC phải chiếm trọn một dòng riêng: mọi ô chọn-NHIỀU và cụm nhiều ô số.
  // Chúng đựng danh sách đã chọn nên cao dần theo số mục — nhét cạnh nhãn thì vừa chật
  // vừa đẩy nhãn trôi lơ lửng giữa một khối mấy dòng.
  const needsOwnLine = opt => opt.type === 'numbers' || opt.type === 'multi';

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
          {isBrief && showPersonal && briefVerdict()}
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
            {/* Khoi rieng cua nguoi dung — dat TRUOC lich chay vi day la thu ho vao de lam.
                Thiếu component thì NÓI RA, đừng lặng lẽ bỏ qua: `&& window.DigestSubBlock` từng
                nuốt trọn khối này suốt nửa ngày khi digest.jsx quên xuất ra window — giao diện
                trông vẫn bình thường, chỉ là không còn chỗ đặt giờ nhận. */}
            {isBrief && showPersonal && (window.DigestSubBlock
              ? <window.DigestSubBlock briefType={wf.type} sub={digestSub}
                  onSaved={onDigestSaved} pushToast={pushToast}
                  companyReady={companyReady} />
              : <div className="workflows-locked-banner">
                  <Icon name="warning" size={14} /> Không nạp được khối <b>Bản tin của tôi</b> —
                  tải lại trang, còn nếu vẫn vậy thì báo kỹ thuật (thiếu pages/digest.jsx).
                </div>)}
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
                  {/* Với bản tin, con số này KHÔNG phải "mấy giờ gửi" — giờ gửi là của từng người.
                      Nó là khoảng cách giữa 2 lần hệ thống ngó xem "ai tới giờ chưa", tức là mức
                      chênh giờ tối đa. Gọi thẳng tên đó thay vì "tần suất kiểm tra" chung chung. */}
                  <label className="workflows-opt-label">
                    {isBrief ? 'Kiểm tra ai đến giờ, mỗi' : 'Tần suất kiểm tra'}
                  </label>
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
                    Bản tin KHÔNG gửi theo giờ đặt ở đây — mỗi người tự chọn giờ nhận ở khối “Bản tin
                    của tôi” phía trên. Cứ sau chừng này phút, hệ thống ngó một lượt xem <b>ai sắp tới
                    giờ</b>, dựng sẵn bản tin trước giờ đó <b>10 phút</b> rồi hẹn đúng giờ mới gửi.
                    Nên bản tin vẫn đến đúng giờ, miễn là có một lượt ngó rơi vào 10 phút chờ đó — đặt
                    <b> 10 phút trở xuống</b> thì luôn đúng giờ; đặt thưa hơn thì trễ nhiều nhất bằng
                    phần dôi ra (ví dụ 15 phút → trễ tối đa 5 phút, 1 giờ → trễ tối đa 50 phút).
                    Ô này của cả công ty, đặt một lần là xong.
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
                  <div className={'workflows-opt' + (opt.type === 'bool' ? ' is-toggle' : '') + (wideTypes.includes(opt.type) ? ' is-wide' : '')
                    + (needsOwnLine(opt) ? ' is-multi' : '')} key={opt.key}>
                    <div className="workflows-opt-row">
                      <label className="workflows-opt-label">
                        {opt.label}{opt.required && <span className="req-star">*</span>}
                        {/* CHỈ cảnh báo mới thu vào icon. Lời giải thích thường vẫn in thẳng ra
                            (xem hint bên dưới): người dùng lần đầu cần đọc lướt một lượt là hiểu,
                            bắt họ rê chuột từng ô mới biết ô đó làm gì thì khó theo dõi hơn nhiều.
                            Cảnh báo thì khác — nó chỉ xuất hiện tạm, và biến mất sau lần Lưu đầu. */}
                        {opt.dynamicDefault && (wf.options || {})[opt.key] === undefined
                          && (dynOptions[opt.dynamic] || []).length > 0 && (
                          <OptHelp tone="warn" text={'Danh sách chọn sẵn này là phỏng đoán theo tên trạng thái. Xem lại cho đúng cách công ty bạn đặt tên, rồi bấm "Lưu cấu hình" để chốt.'} />
                        )}
                      </label>
                      <div className="workflows-opt-control">
                    <OptionControl opt={opt} options={options} setOptions={setOptions}
                      dynOptions={dynOptions} dynLoading={dynLoading} />
                  </div>
                    </div>
                    {opt.hint && <div className="workflows-opt-hint">{opt.hint}</div>}
                    {opt.dynamicDefault && (
                      <StatusMapPanel
                        list={dynOptions[opt.dynamic] || []}
                        chosen={options[opt.key]}
                        loading={!!dynLoading[opt.dynamic]}
                        source={dynSuggested[opt.dynamic + ':src']}
                        onRefresh={() => loadDynStatuses(true)} />
                    )}
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

// ─── StatusMapPanel ──────────────────────────────────────────────────────────────
//
// Cho người dùng NHÌN THẤY máy đang hiểu từng trạng thái của họ là "còn phải làm" hay "đã xong",
// thay vì chỉ đưa một danh sách đã tick sẵn rồi mong họ tin. Một phán đoán của AI đang quyết định
// nội dung bản tin — thứ đó phải mở ra xem được, và phải chạy lại được.
//
// Bảng này đọc từ ô chọn ở trên (chosen), KHÔNG đọc riêng gợi ý của AI: sau khi người dùng tự sửa
// thì bảng phải nói đúng cái ĐANG có hiệu lực, chứ không phải cái AI từng đề xuất.
function StatusMapPanel({ list, chosen, loading, source, onRefresh }) {
  const Icon = window.Icon;
  const [open, setOpen] = uS(false);

  if (loading) return (
    <div className="wf-statusmap is-loading">
      <span className="wf-statusmap-spin" />
      Đang nhờ AI đọc tên trạng thái của công ty bạn để chọn sẵn — chờ vài giây…
    </div>
  );
  if (!list.length) return null;

  const sel = Array.isArray(chosen) ? chosen : [];
  const openCount = list.filter(o => sel.includes(o.value)).length;

  return (
    <div className="wf-statusmap">
      <div className="wf-statusmap-bar">
        <button type="button" className="wf-statusmap-toggle" onClick={() => setOpen(v => !v)}>
          <Icon name={open ? 'chevronUp' : 'chevronDown'} size={12} />
          Đang hiểu {openCount}/{list.length} trạng thái là “còn phải làm”
        </button>
        {source === 'ai' || source === 'cache'
          ? <span className="wf-statusmap-src">AI phân loại theo tên</span>
          : <span className="wf-statusmap-src is-weak">đoán theo từ khoá</span>}
        <button type="button" className="wga-btn ghost wf-statusmap-redo" onClick={onRefresh}>
          <Icon name="refresh" size={12} /> Phân loại lại
        </button>
      </div>
      {open && (
        <ul className="wf-statusmap-list">
          {list.map(o => {
            const on = sel.includes(o.value);
            return (
              <li key={o.value} className={on ? 'is-open' : 'is-closed'}>
                <span className="wf-statusmap-dot" />
                <span className="wf-statusmap-name">{o.label}</span>
                <span className="wf-statusmap-verdict">{on ? 'còn phải làm' : 'đã xong'}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

// ─── StatusMappingCard ───────────────────────────────────────────────────────────
//
// "Máy đang hiểu trạng thái của công ty tôi thế nào" — đặt ở CẤP TRANG, không chôn trong từng ô
// cấu hình. Bảng nhỏ dưới mỗi ô vẫn còn (nó nói cái ĐANG được chọn), nhưng muốn xem cách hiểu tổng
// thể hoặc bắt phân loại lại thì phải mở đúng thẻ, cuộn tới đúng nhóm ① hoặc ④ mới thấy — gần như
// không ai tìm ra. Một phán đoán của AI đang quyết định nội dung bản tin thì phải có chỗ xem và
// chạy lại mà không cần biết nó nằm ở ô nào.
function StatusMappingCard({ pushToast }) {
  const Icon = window.Icon;
  const [open, setOpen] = uS(false);
  const [data, setData] = uS(null);       // { deal: {...}, task: {...} }
  const [loading, setLoading] = uS(false);

  const KINDS = [
    { key: 'deal', label: 'Trạng thái cơ hội bán hàng', url: '/api/v1/workflows/deal-statuses' },
    { key: 'task', label: 'Trạng thái công việc', url: '/api/v1/workflows/task-statuses' },
  ];

  async function load(refresh) {
    setLoading(true);
    try {
      const out = {};
      for (const k of KINDS) {
        const d = await apiFetch(k.url + (refresh ? '?refresh=1' : ''));
        out[k.key] = {
          items: d.items || [],
          open: Array.isArray(d.openSuggested) ? d.openSuggested : null,
          src: d.hintSource,
          error: d.error,
        };
      }
      setData(out);
      if (refresh) pushToast('Đã phân loại lại. Mở lại thẻ cấu hình và bấm Lưu nếu muốn áp dụng.');
    } catch (e) {
      pushToast('Không đọc được danh sách trạng thái: ' + e.message, 'error');
    } finally { setLoading(false); }
  }

  function toggle() {
    const next = !open;
    setOpen(next);
    if (next && !data) load(false);
  }

  return (
    <div className="wga-card workflows-statusmap-card">
      <div className="workflows-statusmap-head">
        <div>
          <h4>Cách hiểu trạng thái của công ty bạn</h4>
          <p>
            Mỗi công ty đặt tên trạng thái một kiểu, mà hệ thống cần biết cái nào là “còn phải làm”
            để nhắc đúng việc. AI đọc tên trạng thái trong CRM của bạn rồi phân loại sẵn — xem lại ở
            đây, thấy sai thì sửa trong từng mục cấu hình bên dưới.
          </p>
        </div>
        <div className="workflows-statusmap-actions">
          <button className="wga-btn ghost" onClick={toggle}>
            <Icon name={open ? 'chevronUp' : 'chevronDown'} size={13} />
            {open ? 'Ẩn' : 'Xem cách hiểu'}
          </button>
          <button className="wga-btn" onClick={() => load(true)} disabled={loading}>
            <Icon name="refresh" size={13} /> {loading ? 'Đang đọc…' : 'Phân loại lại'}
          </button>
        </div>
      </div>

      {open && (
        loading && !data
          ? <div className="wf-statusmap is-loading">
              <span className="wf-statusmap-spin" />
              Đang nhờ AI đọc tên trạng thái trong CRM của bạn — chờ vài giây…
            </div>
          : <div className="workflows-statusmap-cols">
              {KINDS.map(k => {
                const d = (data || {})[k.key];
                if (!d) return null;
                const sel = d.open;
                return (
                  <div className="workflows-statusmap-col" key={k.key}>
                    <div className="workflows-statusmap-coltitle">
                      {k.label}
                      {sel
                        ? <span className="wf-statusmap-src">AI phân loại theo tên</span>
                        : <span className="wf-statusmap-src is-weak">chưa có gợi ý — đoán theo từ khoá</span>}
                    </div>
                    {d.error && <div className="workflows-opt-hint">Lỗi đọc từ CRM: {d.error}</div>}
                    {d.items.length === 0
                      ? <div className="workflows-opt-hint">CRM chưa trả về trạng thái nào.</div>
                      : <ul className="wf-statusmap-list">
                          {d.items.map(o => {
                            const on = sel ? sel.includes(o.value) : true;
                            return (
                              <li key={o.value} className={on ? 'is-open' : 'is-closed'}>
                                <span className="wf-statusmap-dot" />
                                <span className="wf-statusmap-name">{o.label}</span>
                                <span className="wf-statusmap-verdict">{on ? 'còn phải làm' : 'đã xong'}</span>
                              </li>
                            );
                          })}
                        </ul>}
                  </div>
                );
              })}
            </div>
      )}
    </div>
  );
}

// ─── BriefPicker ─────────────────────────────────────────────────────────────────

// Hai loại bản tin GỘP vào MỘT thẻ, chọn loại bằng ô chọn ở trên.
//
// Lý do: mỗi người chỉ nhận MỘT loại theo vai trò (bật loại này tự tắt loại kia). Bày cả hai
// thẻ cạnh nhau khiến người dùng tưởng phải khai cả hai, và phải đọc hết hai khối cấu hình gần
// giống nhau mới biết cái nào là của mình. Ô chọn chỉ đổi loại ĐANG XEM — không đụng đăng ký,
// muốn đổi loại nhận thì vẫn phải tick "Nhận bản tin này" rồi Lưu như cũ.
function BriefPicker({ items, subOf, onUpdate, pushToast, canConfig, onDigestSaved, briefPart }) {
  const Icon = window.Icon;
  // Mở ra là thấy đúng loại mình đang nhận, khỏi phải đi tìm.
  const enabledType = items.map(w => w.type).find(t => { const s = subOf(t); return s && s.enabled; });
  const [type, setType] = uS(enabledType || items[0].type);
  // Vừa bật loại kia rồi Lưu → danh sách tải lại → chuyển theo cho khớp thực tế.
  uE(() => { if (enabledType && enabledType !== type) setType(enabledType); }, [enabledType]);
  const wf = items.find(w => w.type === type) || items[0];
  const sub = subOf(wf.type);

  return (
    <div className="workflows-listview">
      <div className="workflows-briefpick">
        <label className="workflows-briefpick-label" htmlFor="wf-brief-pick">
          <Icon name="mail" size={14} /> Loại bản tin
        </label>
        <select id="wf-brief-pick" className="workflows-select workflows-briefpick-select"
          value={type} onChange={e => setType(e.target.value)}>
          {items.map(w => <option key={w.type} value={w.type}>{w.label}</option>)}
        </select>
        <span className="workflows-briefpick-note">
          Mỗi người chỉ nhận một loại theo vai trò. Đổi ở đây chỉ để xem cấu hình loại khác.
        </span>
      </div>
      <WorkflowCard key={wf.type} wf={wf} onUpdate={onUpdate} pushToast={pushToast}
        locked={false} canConfig={canConfig} briefPart={briefPart}
        digestSub={sub} onDigestSaved={onDigestSaved} />
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
        // Thẻ bản tin xuất hiện ở CẢ HAI mục, nhưng mỗi nơi một nửa:
        //   Theo người dùng  → chỉ đăng ký nhận của chính mình (briefPart='personal')
        //   Theo tổ chức     → luật chung: lịch chạy + mục nào vào bản tin + ngưỡng (briefPart='company')
        // Chia theo đúng ranh giới ai-quyết-cái-gì, thay vì nhồi cả hai vào một thẻ rồi để người
        // đọc tự đoán phần nào là của họ.
        const isBriefWf = w => BRIEF_WORKFLOWS.includes(w.type);
        const perUser = workflows.filter(w => w.scope === 'PerUser' || isBriefWf(w));
        const perTenant = canConfig
          ? workflows.filter(w => w.scope === 'PerTenant' && !isBriefWf(w))
          : [];
        const briefRules = canConfig ? workflows.filter(isBriefWf) : [];
        const subOf = t => digestSubs.find(x => x.briefType === t) || null;
        const renderCards = (list, locked, briefPart) => (
          <div className="workflows-listview">
            {list.map(wf => (
              <WorkflowCard key={wf.type} wf={wf} onUpdate={loadWorkflows} pushToast={pushToast}
                locked={locked} canConfig={canConfig} briefPart={briefPart}
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
                {renderCards(perUser.filter(w => !isBriefWf(w)), false)}
                {/* 2 loại bản tin gộp thành 1 thẻ + ô chọn loại — xem BriefPicker. */}
                {perUser.some(isBriefWf) && (
                  <BriefPicker items={perUser.filter(isBriefWf)} subOf={subOf}
                    onUpdate={loadWorkflows} pushToast={pushToast}
                    canConfig={canConfig} onDigestSaved={loadDigest} briefPart="personal" />
                )}
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
                {/* Luật chung của bản tin: khai một lần cho cả công ty, ai muốn nhận thì tự bật
                    ở mục Theo người dùng phía trên. Đặt TRƯỚC các tác vụ khác vì chưa khai xong
                    ở đây thì không ai đăng ký nhận được. */}
                {briefRules.length > 0 && (
                  <div className="workflows-subsection">
                    <div className="workflows-subsection-head">
                      <h3>Luật chung của bản tin</h3>
                      <p>Đưa mục nào vào bản tin, ngưỡng bao nhiêu ngày, trạng thái nào còn phải chăm —
                        khai một lần, áp cho mọi người nhận. Chưa khai thì chưa ai đăng ký nhận được.</p>
                    </div>
                    <StatusMappingCard pushToast={pushToast} />
                    {renderCards(briefRules, false, 'company')}
                  </div>
                )}
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
