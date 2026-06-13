// pages/ai-usage.jsx — Giám sát chi phí AI. Bám format /assistant (hero cam-đen + eyebrow + pane sections).

const { useState: _uS, useEffect: _uE, useCallback: _uCb } = React;

const fmtN = (n) => Number(n || 0).toLocaleString('vi-VN');
const fmtVnd = (n) => Number(n || 0).toLocaleString('vi-VN');
const FEATURE_LABEL = {
  visa: 'Thẩm định Visa', deals: 'Ưu tiên Deal', chat: 'Trợ lý số liệu',
  mail: 'Hộp thư AI', reviews: 'Customer Review', 'tour-builder': 'Soạn Tour GIT',
  completions: 'Completions (raw)', other: 'Khác',
};
const FEATURE_COLOR = {
  visa: '#2563eb', deals: '#d97706', chat: '#16a34a', mail: '#8b5cf6',
  reviews: '#0ea5e9', 'tour-builder': '#f97316', completions: '#64748b', other: '#94a3b8',
};

// Eyebrow nhỏ (dot cam + uppercase) — DÙNG TỐI ĐA 3 LẦN/page (taste: max 1 eyebrow / 3 section).
// Trong dashboard này: KPI hero, Phân tích, Workflow log. Các section khác chỉ dùng <h3> đơn.
const Eyebrow = ({ children, sub }) => (
  <div className="aiu-eyebrow">{children}{sub && <em> · {sub}</em>}</div>
);
// Header section gọn, KHÔNG dot cam — cho các section không cần "đánh dấu" mạnh
const SectionTitle = ({ children, hint }) => (
  <div className="aiu-section-title">
    <h3>{children}</h3>
    {hint && <span className="aiu-section-hint">{hint}</span>}
  </div>
);

function AiUsagePage({ pushToast }) {
  const [data, setData] = _uS(null);
  const [logs, setLogs] = _uS([]);
  const [days, setDays] = _uS(1);
  const [loading, setLoading] = _uS(true);
  // Filter UI cho bảng "Nhật ký chi tiết" — tìm model / feature trong 500 dòng gần nhất.
  const [logFilter, setLogFilter] = _uS('');

  const load = async () => {
    setLoading(true);
    try {
      // n=1000: max của endpoint (Clamp 10-1000), cover toàn bộ file rotate ngưỡng 10k dòng.
      // Filter UI lọc ở client (model/feature/provider).
      const [r1, r2] = await Promise.all([
        fetch(`/api/v1/ai/usage?days=${days}`),
        fetch('/api/v1/ai/usage/log?n=1000'),
      ]);
      if (r1.ok) setData(await r1.json());
      if (r2.ok) setLogs(await r2.json());
    } catch (e) { pushToast('Lỗi tải usage: ' + e.message, 'error'); }
    finally { setLoading(false); }
  };
  _uE(() => { load(); }, [days]);

  if (loading && !data) return <main className="page aiu"><div className="aiu-loading">Đang tải…</div></main>;
  if (!data) return <main className="page aiu"><div className="aiu-loading">Không có dữ liệu</div></main>;

  const t = data.totals || {};
  const budget = data.budget || {};
  const tenants = budget.perTenantToday || [];
  const overTenants = tenants.filter(x => x.overBudget);

  return (
    <main className="page aiu">
      <window.PageShell.PageHero
        icon="chart"
        title="Giám sát chi phí AI"
        badge="thời gian thực"
        sub="Theo dõi token, chi phí và quota theo tính năng · model · user — chống cháy túi."
        status={{ label: loading ? 'ĐANG CẬP NHẬT' : 'DỮ LIỆU MỚI',
          detail: data.generatedAt ? new Date(data.generatedAt).toLocaleTimeString('vi-VN') : '—',
          tone: loading ? 'idle' : 'live' }}
        actions={<button className="aiu-status-refresh" onClick={load} disabled={loading} title="Làm mới">
          <Icon name="refresh" size={15} stroke={2.4} />
        </button>}
      />

      {/* Range chips (filter Hôm nay/7/30 ngày) */}
      <div className="aiu-rangebar">
        {[{ d: 1, l: 'Hôm nay' }, { d: 7, l: '7 ngày' }, { d: 30, l: '30 ngày' }].map(o => (
          <button key={o.d} className={'aiu-range-chip' + (days === o.d ? ' on' : '')} onClick={() => setDays(o.d)}>{o.l}</button>
        ))}
        <span className="aiu-range-info">
          {t.calls > 0 && t.cacheHits > 0 && (
            <>Cache đã tiết kiệm <b>{fmtN(t.cacheHits)}</b> lượt gọi</>
          )}
        </span>
      </div>

      {/* Cảnh báo vượt ngưỡng (em-dash banned: dùng "·" thay) */}
      {overTenants.length > 0 && (
        <div className="aiu-warn">
          <Icon name="warning" size={16} />
          <div>
            <b>Cảnh báo:</b> {overTenants.length} tenant vượt ngân sách ngày
            ({fmtVnd(budget.dailyVnd)} đ): {overTenants.map(x => `${x.tenant} (${fmtVnd(x.costVnd)} đ)`).join(', ')}.
          </div>
        </div>
      )}

      {/* KPI hero strip — eyebrow #1/3 (chỉ section "đánh dấu" mạnh, kế đến là Phân tích + Workflow log) */}
      <section className="aiu-pane">
        <Eyebrow sub={days === 1 ? 'HÔM NAY' : `${days} NGÀY GẦN NHẤT`}>TỔNG QUAN CHI PHÍ</Eyebrow>
        <div className="aiu-kpis">
          <div className="aiu-kpi">
            <div className="aiu-kpi-lbl">Tổng lượt gọi</div>
            <div className="aiu-kpi-val">{fmtN(t.calls)}</div>
            <div className="aiu-kpi-sub">{fmtN(t.cacheHits)} cache-hit (tiết kiệm)</div>
          </div>
          <div className="aiu-kpi">
            <div className="aiu-kpi-lbl">Token IN</div>
            <div className="aiu-kpi-val">{fmtN(t.inTok)}</div>
            <div className="aiu-kpi-sub">prompts gửi đi</div>
          </div>
          <div className="aiu-kpi">
            <div className="aiu-kpi-lbl">Token OUT</div>
            <div className="aiu-kpi-val">{fmtN(t.outTok)}</div>
            <div className="aiu-kpi-sub">AI trả về</div>
          </div>
          <div className="aiu-kpi accent">
            <div className="aiu-kpi-lbl">Chi phí ước tính</div>
            <div className="aiu-kpi-val">{fmtVnd(t.costVnd)} <em>đ</em></div>
            <div className="aiu-kpi-sub">đã trừ cache</div>
          </div>
        </div>
      </section>

      {/* Ngân sách (compact list, không eyebrow — layout khác family với KPI strip để khỏi lặp) */}
      {tenants.length > 0 && (
        <section className="aiu-pane">
          <SectionTitle hint={`ngưỡng ${fmtVnd(budget.dailyVnd)} đ/tenant`}>Ngân sách hôm nay</SectionTitle>
          <div className="aiu-budgets">
            {tenants.map(t => (
              <div key={t.tenant} className={'aiu-budget-row' + (t.overBudget ? ' over' : '')}>
                <span className="aiu-budget-name">{t.tenant}</span>
                <div className="aiu-budget-bar"><i style={{ width: Math.min(100, t.pct) + '%' }} /></div>
                <span className="aiu-budget-val">{fmtVnd(t.costVnd)} <em>đ · {t.pct}%</em></span>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* Phân tích — 2 cột feature/model. Eyebrow #2/3 dùng chung cho cả grid. */}
      <section className="aiu-pane aiu-analysis">
        <Eyebrow sub="PHÂN BỔ THEO TÍNH NĂNG VÀ MODEL">PHÂN TÍCH CHI PHÍ</Eyebrow>
        <div className="aiu-grid2">
          <div className="aiu-subpane">
            <SectionTitle hint="theo tính năng">Tính năng</SectionTitle>
            <table className="aiu-table">
              <thead><tr><th>Tính năng</th><th>Lượt</th><th>Token IN/OUT</th><th>Chi phí</th></tr></thead>
              <tbody>
                {(data.byFeature || []).length === 0 && (
                  <tr><td colSpan={4} className="aiu-empty">Chưa có lượt gọi nào</td></tr>
                )}
                {(data.byFeature || []).map(f => (
                  <tr key={f.feature}>
                    <td><span className="aiu-dot" style={{ background: FEATURE_COLOR[f.feature] || '#94a3b8' }} /> {FEATURE_LABEL[f.feature] || f.feature}</td>
                    <td className="num">{fmtN(f.calls)}</td>
                    <td className="num">{fmtN(f.inTok)} / {fmtN(f.outTok)}</td>
                    <td className="num strong">{fmtVnd(f.costVnd)} đ</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="aiu-subpane">
            <SectionTitle hint="theo model">Model</SectionTitle>
            <table className="aiu-table">
              <thead><tr><th>Model</th><th>Lượt</th><th>Token IN/OUT</th><th>Chi phí</th></tr></thead>
              <tbody>
                {(data.byModel || []).length === 0 && (
                  <tr><td colSpan={4} className="aiu-empty">Chưa có lượt gọi nào</td></tr>
                )}
                {(data.byModel || []).map(m => (
                  <tr key={m.model}>
                    <td><code>{m.model}</code></td>
                    <td className="num">{fmtN(m.calls)}</td>
                    <td className="num">{fmtN(m.inTok)} / {fmtN(m.outTok)}</td>
                    <td className="num strong">{fmtVnd(m.costVnd)} đ</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </section>

      {/* Top user — rank list, KHÁC layout family với section trên (không table) */}
      {(data.byUser || []).length > 0 && (
        <section className="aiu-pane">
          <SectionTitle hint={`top ${data.byUser.length} user tiêu nhiều nhất`}>User chi tiêu cao</SectionTitle>
          <ol className="aiu-rank">
            {data.byUser.map((u, i) => (
              <li key={u.session}>
                <span className="aiu-rank-num">{String(i + 1).padStart(2, '0')}</span>
                <div className="aiu-rank-body">
                  <code className="aiu-rank-id">{u.session}</code>
                  <span className="aiu-rank-tenant">{u.tenant || '(không có tenant)'}</span>
                </div>
                <span className="aiu-rank-meta">{fmtN(u.calls)} lượt</span>
                <span className="aiu-rank-cost">{fmtVnd(u.costVnd)} <em>đ</em></span>
              </li>
            ))}
          </ol>
        </section>
      )}

      {/* Nhật ký chi tiết — full-width, không eyebrow. Filter theo model/feature
          để tìm nhanh model lạ (vd "sonnet", "opus") trong 500 dòng gần nhất. */}
      <section className="aiu-pane">
        <SectionTitle hint={`${logs.length} lượt · ${(() => {
          const q = logFilter.trim().toLowerCase();
          if (!q) return 'hiển thị tất cả';
          const n = logs.filter(e => (e.model||'').toLowerCase().includes(q) || (e.feature||'').toLowerCase().includes(q) || (e.provider||'').toLowerCase().includes(q)).length;
          return `${n} khớp "${logFilter}"`;
        })()}`}>Nhật ký chi tiết</SectionTitle>
        <div className="aiu-log-filter">
          <input type="text" placeholder='Tìm theo model / tính năng / provider (vd: "sonnet")'
            value={logFilter} onChange={e => setLogFilter(e.target.value)} />
          {logFilter && <button onClick={() => setLogFilter('')} className="aiu-log-filter-clear">×</button>}
        </div>
        <div className="aiu-log-wrap">
          <table className="aiu-table aiu-log">
            <thead><tr><th>Thời gian</th><th>Tính năng</th><th>Model</th><th>IN/OUT</th><th>Latency</th><th>Chi phí</th><th>Status</th></tr></thead>
            <tbody>
              {(() => {
                const q = logFilter.trim().toLowerCase();
                const shown = q ? logs.filter(e => (e.model||'').toLowerCase().includes(q) || (e.feature||'').toLowerCase().includes(q) || (e.provider||'').toLowerCase().includes(q)) : logs;
                if (shown.length === 0) return <tr><td colSpan={7} className="aiu-empty">{logs.length === 0 ? 'Chưa có log nào' : 'Không có dòng nào khớp filter'}</td></tr>;
                return shown.map((e, i) => (
                  <tr key={i} className={e.cached ? 'cached' : ''}>
                    <td>{new Date(e.ts).toLocaleString('vi-VN', { day:'2-digit', month:'2-digit', hour:'2-digit', minute:'2-digit', second:'2-digit' })}</td>
                    <td><span className="aiu-dot" style={{ background: FEATURE_COLOR[e.feature] || '#94a3b8' }} /> {FEATURE_LABEL[e.feature] || e.feature}</td>
                    <td><code>{e.model}</code></td>
                    <td className="num">{fmtN(e.inTok)}/{fmtN(e.outTok)}</td>
                    <td className="num">{e.latencyMs}ms</td>
                    <td className="num strong">{e.cached ? <span className="aiu-cache">cache</span> : fmtVnd(e.costVnd) + ' đ'}</td>
                    <td><span className={'aiu-status ' + (e.status === 'ok' ? 'ok' : 'warn')}>{e.status}</span></td>
                  </tr>
                ));
              })()}
            </tbody>
          </table>
        </div>
      </section>

      {/* Section: CÂU KHÓ AI — unresolved questions log */}
      <UnresolvedTab />

      {/* Section: WORKFLOW LOG — workflow trace persisted (data/workflow-traces.jsonl) */}
      <WorkflowLogTab />
    </main>
  );
}

// ── Tab "Workflow log" — đọc workflow-traces.jsonl qua GET /api/v1/workflow-traces ──
function WorkflowLogTab() {
  const [data, setData] = _uS(null);
  const [days, setDays] = _uS(7);
  const [workflow, setWf] = _uS('');
  const [loading, setLoading] = _uS(false);
  const [expanded, setExpanded] = _uS({});   // {runId: bool}

  const load = _uCb(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams({ days });
      if (workflow) params.set('workflow', workflow);
      params.set('limit', '50');
      const r = await fetch(`/api/v1/workflow-traces?${params}`);
      if (r.ok) setData(await r.json());
    } catch {} finally { setLoading(false); }
  }, [days, workflow]);
  _uE(() => { load(); }, [load]);

  return (
    <section className="aiu-pane">
      <Eyebrow sub="MỌI REQUEST DEBUG ĐƯỢC LƯU JSONL">WORKFLOW LOG</Eyebrow>
      <div className="aiu-wflog-head">
        <div className="aiu-wflog-filters">
          <select className="aiu-select" value={days} onChange={e => setDays(+e.target.value)}>
            <option value={1}>24h qua</option>
            <option value={7}>7 ngày</option>
            <option value={30}>30 ngày</option>
          </select>
          <select className="aiu-select" value={workflow} onChange={e => setWf(e.target.value)}>
            <option value="">Tất cả workflow</option>
            {(data?.summary || []).map(s => (
              <option key={s.workflow} value={s.workflow}>{s.workflow} ({s.count})</option>
            ))}
          </select>
          <button className="aiu-status-refresh" onClick={load} disabled={loading} title="Tải lại">
            <Icon name="refresh" size={14} stroke={2.4} />
          </button>
        </div>
      </div>

      {data && data.summary && data.summary.length > 0 && (
        <div className="aiu-wflog-summary">
          {data.summary.map(s => (
            <div key={s.workflow} className="aiu-wflog-summary-card">
              <div className="aiu-wflog-summary-val">{s.count}</div>
              <div className="aiu-wflog-summary-lbl">{s.workflow}</div>
              <div className="aiu-wflog-summary-meta">
                {(s.minMs/1000).toFixed(1)}s ~ {(s.maxMs/1000).toFixed(1)}s
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="aiu-wflog-list">
        {(data?.entries || []).map((e, i) => {
          const isOpen = expanded[e.runId];
          return (
            <div key={e.runId + ':' + i} className={'aiu-wflog-row' + (isOpen ? ' open' : '')}>
              <button className="aiu-wflog-rowhead" onClick={() => setExpanded(x => ({...x, [e.runId]: !isOpen}))}>
                <span className="aiu-wflog-ts">{new Date(e.ts).toLocaleString('vi-VN')}</span>
                <span className="aiu-wflog-wf">{e.workflow}</span>
                <span className="aiu-wflog-ms">{e.totalMs}ms</span>
                <span className="aiu-wflog-steps">{e.stepCount} bước</span>
                <span className="aiu-wflog-path"><code>{e.method} {e.path}</code></span>
                <span className="aiu-wflog-chev">{isOpen ? '▾' : '▸'}</span>
              </button>
              {isOpen && e.trace && window.TraceView && (
                <div className="aiu-wflog-detail">
                  <window.TraceView trace={e.trace} />
                </div>
              )}
            </div>
          );
        })}
        {(!data || data.count === 0) && (
          <div className="aiu-empty">
            Chưa có trace nào trong {days} ngày qua. Bật nút <Icon name="info" size={11}/> trên topbar
            rồi thao tác feature AI bất kỳ, trace tự lưu xuống đĩa.
          </div>
        )}
      </div>
    </section>
  );
}

// ── Tab "Câu khó AI" — đọc unresolved-log qua GET /api/v1/chat/unresolved ──
function UnresolvedTab() {
  const [data, setData] = _uS(null);
  const [days, setDays] = _uS(7);
  const [tag, setTag]   = _uS('');
  const [loading, setLoading] = _uS(false);

  const load = _uCb(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams({ days });
      if (tag) params.set('tag', tag);
      const r = await fetch(`/api/v1/chat/unresolved?${params}`);
      if (r.ok) setData(await r.json());
    } finally { setLoading(false); }
  }, [days, tag]);

  _uE(() => { load(); }, [load]);

  return (
    <section className="aiu-pane">
      <SectionTitle hint="trigger tag — câu AI không suy luận được">Câu khó AI</SectionTitle>

      <div className="unresolved-filters">
        <select value={days} onChange={e => setDays(+e.target.value)}>
          <option value={1}>1 ngày</option>
          <option value={7}>7 ngày</option>
          <option value={30}>30 ngày</option>
        </select>
        <input
          placeholder="Lọc theo tag (vd: planner_none_but_data_intent)"
          value={tag}
          onChange={e => setTag(e.target.value)}
        />
        <button className="aiu-range-chip on" onClick={load} disabled={loading}>
          {loading ? 'Đang tải…' : 'Làm mới'}
        </button>
        {data && <span className="unresolved-badge">{data.total} entry</span>}
      </div>

      {data && data.byTag && data.byTag.length > 0 && (
        <div className="unresolved-summary">
          <table className="aiu-table unresolved-table">
            <thead>
              <tr><th>Tag</th><th>Count</th><th>Câu hỏi mẫu</th></tr>
            </thead>
            <tbody>
              {data.byTag.map(t => (
                <tr key={t.tag}>
                  <td><code className="unresolved-code">{t.tag}</code></td>
                  <td className="num">{t.count}</td>
                  <td>
                    <ul className="unresolved-samples">
                      {(t.sampleQuestions || []).map((q, i) => <li key={i}>{q}</li>)}
                    </ul>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.byTag && data.byTag.length === 0 && (
        <div className="aiu-empty" style={{ padding: '24px 0' }}>Không có entry nào trong khoảng thời gian đã chọn.</div>
      )}

      {data && data.entries && data.entries.length > 0 && (
        <details className="unresolved-details">
          <summary>{Math.min(50, data.total)} entry chi tiết gần nhất</summary>
          <pre className="unresolved-pre">{JSON.stringify(data.entries, null, 2)}</pre>
        </details>
      )}
    </section>
  );
}

window.AiUsagePage = AiUsagePage;
