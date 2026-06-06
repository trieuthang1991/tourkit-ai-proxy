// pages/ai-usage.jsx — Giám sát chi phí AI (admin view).
// 4 KPI cards (gọi/tokens in/out/cost VND) + budget bar + 4 bảng (feature/model/user/log).

const { useState: _uS, useEffect: _uE } = React;

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
      <header className="aiu-head">
        <div>
          <h1 className="aiu-title">Giám sát chi phí AI</h1>
          <p className="aiu-sub">Theo dõi token, chi phí và quota theo feature / model / user. Cập nhật từ {data.generatedAt ? new Date(data.generatedAt).toLocaleString('vi-VN') : '—'}.</p>
        </div>
        <div className="aiu-actions">
          <div className="aiu-range">
            {[1, 7, 30].map(d => (
              <button key={d} className={'aiu-range-btn' + (days === d ? ' on' : '')} onClick={() => setDays(d)}>
                {d === 1 ? 'Hôm nay' : d + ' ngày'}
              </button>
            ))}
          </div>
          <button className="aiu-btn" onClick={load}><Icon name="refresh" size={14} /> Làm mới</button>
        </div>
      </header>

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

      {/* 4 KPI */}
      <section className="aiu-kpis">
        <div className="aiu-kpi">
          <div className="aiu-kpi-lbl">Tổng số lượt gọi</div>
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
          <div className="aiu-kpi-sub">{days === 1 ? 'Hôm nay' : `${days} ngày gần nhất`}</div>
        </div>
      </section>

      {/* Budget bar theo tenant */}
      {tenants.length > 0 && (
        <section className="aiu-block">
          <h3>Ngân sách AI hôm nay <em>(ngưỡng {fmtVnd(budget.dailyVnd)} đ/tenant)</em></h3>
          <div className="aiu-budgets">
            {tenants.map(t => (
              <div key={t.tenant} className={'aiu-budget-row' + (t.overBudget ? ' over' : '')}>
                <span className="aiu-budget-name">{t.tenant}</span>
                <div className="aiu-budget-bar"><i style={{ width: Math.min(100, t.pct) + '%' }} /></div>
                <span className="aiu-budget-val">{fmtVnd(t.costVnd)} đ <em>· {t.pct}%</em></span>
              </div>
            ))}
          </div>
        </section>
      )}

      <div className="aiu-grid2">
        {/* Theo feature */}
        <section className="aiu-block">
          <h3>Theo tính năng</h3>
          <table className="aiu-table">
            <thead><tr><th>Tính năng</th><th>Lượt</th><th>Token (in/out)</th><th>Chi phí</th></tr></thead>
            <tbody>
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

        {/* Theo model */}
        <section className="aiu-block">
          <h3>Theo model</h3>
          <table className="aiu-table">
            <thead><tr><th>Model</th><th>Lượt</th><th>Token (in/out)</th><th>Chi phí</th></tr></thead>
            <tbody>
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

      {/* Top user */}
      {(data.byUser || []).length > 0 && (
        <section className="aiu-block">
          <h3>Top user tiêu nhiều nhất</h3>
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

      {/* Log gần đây */}
      <section className="aiu-block">
        <h3>Log {logs.length} lượt gọi gần nhất</h3>
        <div className="aiu-log-wrap">
          <table className="aiu-table aiu-log">
            <thead><tr><th>Thời gian</th><th>Tính năng</th><th>Model</th><th>In/Out</th><th>Latency</th><th>Chi phí</th><th>Status</th></tr></thead>
            <tbody>
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
    </main>
  );
}

window.AiUsagePage = AiUsagePage;
