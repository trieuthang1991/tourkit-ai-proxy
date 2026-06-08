// wwwroot/components/trace-view.jsx
// Generic "Cách vận hành" collapsible — hiện workflow trace từ BẤT KỲ feature nào
// (Chat, Customer Review, Mail, Visa, Deal, TourBuilder).
//
// Backend trả field `_trace` hoặc `trace` trong response khi user bật ?debug=1 / X-Debug header.
// Frontend pass đối tượng đó vào <TraceView trace={result._trace || result.trace} /> để render.
//
// Dùng:
//   const r = await fetch('/api/v1/.../score?debug=1').then(x => x.json());
//   <TraceView trace={r._trace} />     // null/undefined → KHÔNG render

(function () {
  const { useState } = React;

  const STEP_ICON = { ok: '✓', skip: '○', fail: '✗', fallback: '↻' };
  const STEP_CLS  = {
    ok:       'asst-trc-ok',
    skip:     'asst-trc-skip',
    fail:     'asst-trc-fail',
    fallback: 'asst-trc-fallback',
  };

  function TraceView({ trace }) {
    const [open, setOpen] = useState(false);
    if (!trace || !trace.steps || !trace.steps.length) return null;

    // Nhãn workflow: ưu tiên trace.workflow (mới), fallback trace.agent (legacy chat).
    const wf = trace.workflow || trace.agent || 'Workflow';

    return (
      <div className={'asst-trace' + (open ? ' open' : '')}>
        <button className="asst-trace-toggle" onClick={() => setOpen(v => !v)}>
          {window.Icon ? <window.Icon name="info" size={12} /> : null}
          <span>{open ? 'Ẩn' : 'Cách vận hành'}</span>
          <span className="asst-trace-meta">
            {trace.steps.length} bước · {trace.totalMs}ms · {wf}
          </span>
          <span className="asst-trace-chev">{open ? '▾' : '▸'}</span>
        </button>
        {open && (
          <ol className="asst-trace-steps">
            {trace.steps.map((s, i) => (
              <li key={i} className={'asst-trc-step ' + (STEP_CLS[s.status] || '')}>
                <span className="asst-trc-icon">{STEP_ICON[s.status] || '·'}</span>
                <div className="asst-trc-body">
                  <div className="asst-trc-line">
                    <span className="asst-trc-name">{s.name}</span>
                    <span className="asst-trc-ms">{s.durationMs}ms</span>
                  </div>
                  <div className="asst-trc-summary">{s.summary}</div>
                  {s.meta && Object.keys(s.meta).length > 0 && (
                    <details className="asst-trc-meta-det">
                      <summary>chi tiết</summary>
                      <pre className="asst-trc-meta-json">{JSON.stringify(s.meta, null, 2)}</pre>
                    </details>
                  )}
                </div>
              </li>
            ))}
          </ol>
        )}
      </div>
    );
  }

  // Global debug toggle helper: bật/tắt X-Debug header global cho mọi fetch.
  // Persist trong localStorage để giữ qua reload.
  const DEBUG_KEY = 'tourkit_debug_trace';
  function isDebugOn() { return localStorage.getItem(DEBUG_KEY) === '1'; }
  function setDebugOn(on) {
    localStorage.setItem(DEBUG_KEY, on ? '1' : '0');
    window.dispatchEvent(new CustomEvent('tourkit:debug-toggle', { detail: { on } }));
  }

  // Patch global fetch để tự động đính X-Debug header khi flag bật.
  // Frontend các page không cần làm gì — chỉ cần check response._trace + render.
  const _origFetch = window.fetch.bind(window);
  window.fetch = function (input, init = {}) {
    if (!isDebugOn()) return _origFetch(input, init);
    const headers = new Headers(init.headers || {});
    if (!headers.has('X-Debug')) headers.set('X-Debug', '1');
    return _origFetch(input, { ...init, headers });
  };

  window.TraceView = TraceView;
  window.tourkitDebug = { isOn: isDebugOn, set: setDebugOn };
})();
