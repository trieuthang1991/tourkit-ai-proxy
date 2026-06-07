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

// Eyebrow giống asst (uppercase + dot cam + em phụ)
const Eyebrow = ({ children, sub }) => (
  <div className="aiu-eyebrow">{children}{sub && <em> · {sub}</em>}</div>
);

function AiUsagePage({ pushToast }) {
  const [data, setData] = _uS(null);
  const [logs, setLogs] = _uS([]);
  const [days, setDays] = _uS(1);
  const [loading, setLoading] = _uS(true);

  const load = async () => {
    setLoading(true);
    try {
      const [r1, r2] = await Promise.all([
        fetch(`/api/v1/ai/usage?days=${days}`),
        fetch('/api/v1/ai/usage/log?n=50'),
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

      {/* Cảnh báo vượt ngưỡng */}
      {overTenants.length > 0 && (
        <div className="aiu-warn">
          <Icon name="warning" size={16} />
          <div>
            <b>Cảnh báo:</b> {overTenants.length} tenant vượt ngân sách ngày
            ({fmtVnd(budget.dailyVnd)} đ) — {overTenants.map(x => `${x.tenant} (${fmtVnd(x.costVnd)} đ)`).join(', ')}.
          </div>
        </div>
      )}

      {/* Section: TỔNG QUAN — 4 KPI + budget pane */}
      <section className="aiu-pane">
        <Eyebrow sub={days === 1 ? 'HÔM NAY' : `${days} NGÀY GẦN NHẤT`}>TỔNG QUAN</Eyebrow>
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

      {/* Section: NGÂN SÁCH (chỉ hiện khi có tenant) */}
      {tenants.length > 0 && (
        <section className="aiu-pane">
          <Eyebrow sub={`NGƯỠNG ${fmtVnd(budget.dailyVnd)} đ/TENANT`}>NGÂN SÁCH HÔM NAY</Eyebrow>
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

      {/* Section: PHÂN TÍCH — 2 cột (feature/model) */}
      <div className="aiu-grid2">
        <section className="aiu-pane">
          <Eyebrow sub="THEO TÍNH NĂNG">PHÂN BỔ CHI PHÍ</Eyebrow>
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
        </section>

        <section className="aiu-pane">
          <Eyebrow sub="THEO MODEL">PHÂN BỔ CHI PHÍ</Eyebrow>
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
        </section>
      </div>

      {/* Section: TOP USER */}
      {(data.byUser || []).length > 0 && (
        <section className="aiu-pane">
          <Eyebrow sub="TOP 10">USER TIÊU NHIỀU NHẤT</Eyebrow>
          <table className="aiu-table">
            <thead><tr><th>Session</th><th>Tenant</th><th>Lượt</th><th>Chi phí</th></tr></thead>
            <tbody>
              {data.byUser.map(u => (
                <tr key={u.session}>
                  <td><code>{u.session}…</code></td>
                  <td>{u.tenant || '—'}</td>
                  <td className="num">{fmtN(u.calls)}</td>
                  <td className="num strong">{fmtVnd(u.costVnd)} đ</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {/* Section: LOG */}
      <section className="aiu-pane">
        <Eyebrow sub={`${logs.length} LƯỢT GẦN NHẤT`}>NHẬT KÝ CHI TIẾT</Eyebrow>
        <div className="aiu-log-wrap">
          <table className="aiu-table aiu-log">
            <thead><tr><th>Thời gian</th><th>Tính năng</th><th>Model</th><th>IN/OUT</th><th>Latency</th><th>Chi phí</th><th>Status</th></tr></thead>
            <tbody>
              {logs.length === 0 && (
                <tr><td colSpan={7} className="aiu-empty">Chưa có log nào</td></tr>
              )}
              {logs.map((e, i) => (
                <tr key={i} className={e.cached ? 'cached' : ''}>
                  <td>{new Date(e.ts).toLocaleTimeString('vi-VN')}</td>
                  <td><span className="aiu-dot" style={{ background: FEATURE_COLOR[e.feature] || '#94a3b8' }} /> {FEATURE_LABEL[e.feature] || e.feature}</td>
                  <td><code>{e.model}</code></td>
                  <td className="num">{fmtN(e.inTok)}/{fmtN(e.outTok)}</td>
                  <td className="num">{e.latencyMs}ms</td>
                  <td className="num strong">{e.cached ? <span className="aiu-cache">cache</span> : fmtVnd(e.costVnd) + ' đ'}</td>
                  <td><span className={'aiu-status ' + (e.status === 'ok' ? 'ok' : 'warn')}>{e.status}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {/* Section: CÂU KHÓ AI — unresolved questions log */}
      <UnresolvedTab />
    </main>
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
      <Eyebrow sub="CÂU HỎI AI KHÔNG SỬ LÝ ĐƯỢC">CÂU KHÓ AI</Eyebrow>

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
