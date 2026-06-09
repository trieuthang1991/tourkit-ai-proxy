// pages/deals.jsx — Ưu tiên Deal AI. Product UI nội bộ, bám design system warm-orange.
// Phân tích pipeline cơ hội bán hàng → AI chấm khả năng thắng → bảng xếp hạng "nên xử lý deal nào trước".
// Có BỘ LỌC kết quả (tìm/mức win/đang nguội) + layout CARD cho mobile (bảng tràn ở màn nhỏ).

const { useState: _dS, useEffect: _dE, useRef: _dR } = React;

const DEAL_LEVELS = {
  cao:        { label: 'Cao',        color: '#16a34a', bg: '#dcfce7' },
  trung_binh: { label: 'Trung bình', color: '#d97706', bg: '#fef3c7' },
  thap:       { label: 'Thấp',       color: '#dc2626', bg: '#fee2e2' },
};
const dealLevel = (lv) => DEAL_LEVELS[lv] || DEAL_LEVELS.trung_binh;
const vnd = (n) => (n || 0).toLocaleString('vi-VN') + ' đ';
const vndShort = (n) => {
  n = n || 0;
  if (n >= 1e9) return (n / 1e9).toFixed(n % 1e9 === 0 ? 0 : 1) + ' tỷ';
  if (n >= 1e6) return Math.round(n / 1e6) + ' tr';
  return n.toLocaleString('vi-VN');
};

function useIsMobile(bp = 640) {
  const [m, setM] = _dS(() => window.innerWidth <= bp);
  _dE(() => {
    const check = () => setM(window.innerWidth <= bp);
    window.addEventListener('resize', check);
    check();
    return () => window.removeEventListener('resize', check);
  }, []);
  return m;
}

// Đọc SSE stream → onEvent(obj) mỗi event.
async function readDealStream(url, onEvent) {
  const resp = await fetch(url, { headers: { Accept: 'text/event-stream' } });
  if (!resp.ok || !resp.body) throw new Error('Stream không khả dụng (' + resp.status + ')');
  const reader = resp.body.getReader();
  const dec = new TextDecoder('utf-8');
  let buf = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const line = buf.slice(0, idx).split('\n').find(l => l.startsWith('data:'));
      buf = buf.slice(idx + 2);
      if (!line) continue;
      try { onEvent(JSON.parse(line.slice(5).trim())); } catch { /* ignore */ }
    }
  }
}

// ─── Drawer chi tiết 1 deal ───────────────────────────────────────────────────
function DealDrawer({ item, onClose }) {
  if (!item) return null;
  const lv = dealLevel(item.level);
  const a = item.analysis || {};
  const Block = ({ title, items, icon, tone }) => items && items.length > 0 ? (
    <div className={'deal-block ' + tone}>
      <div className="deal-block-head"><Icon name={icon} size={15} /> {title}</div>
      <ul>{items.map((x, i) => <li key={i}>{x}</li>)}</ul>
    </div>
  ) : null;
  return (<>
    <div className="deal-drawer-backdrop" onClick={onClose} />
    <aside className="deal-drawer">
      <div className="deal-drawer-head">
        <div>
          <div className="deal-drawer-title">{item.customerName}</div>
          <div className="deal-drawer-sub">{item.title || item.code || ('#' + item.id)}</div>
        </div>
        <button className="deal-x" onClick={onClose}><Icon name="close" size={16} /></button>
      </div>

      <div className="deal-drawer-stat">
        <div className="deal-win" style={{ color: lv.color }}>{item.winRate}<span>%</span><em>khả năng thắng</em></div>
        <div className="deal-kv">
          <div><span>Giá trị</span><b>{vnd(item.totalPrice)}</b></div>
          <div><span>Doanh thu kỳ vọng</span><b>{vnd(item.expectedValue)}</b></div>
          <div><span>Trạng thái</span><b>{item.statusName || '—'}</b></div>
          <div><span>Tuổi cơ hội</span><b>{item.ageDays} ngày</b></div>
          <div><span>Phụ trách</span><b>{item.assignees || '—'}</b></div>
          <div><span>Nguồn</span><b>{item.sourceName || '—'}</b></div>
        </div>
      </div>

      {a.nextAction && (
        <div className="deal-next"><Icon name="zap" size={16} /><div><b>Hành động nên làm</b><p>{a.nextAction}</p></div></div>
      )}
      <Block title="Dấu hiệu tích cực" items={a.signals} icon="checkCircle" tone="good" />
      <Block title="Rủi ro" items={a.risks} icon="warning" tone="warn" />
      {a.reason && <div className="deal-reason">{a.reason}</div>}
    </aside>
  </>);
}

// ─── Bộ lọc kết quả ─── (mượn pattern mobile qua window.SearchControls) ───
function DealFilters({ q, setQ, level, setLevel, riskOnly, setRiskOnly, shown, total,
                       advCount, advOpen, onAdvToggle }) {
  const SC = window.SearchControls;
  const chips = [['all', 'Tất cả'], ['cao', 'Win cao'], ['trung_binh', 'Win TB'], ['thap', 'Win thấp']];
  return (
    <div className="cust-filter">
      <div className="cust-filter-search">
        <SC.SearchInput value={q} onChange={setQ} placeholder="Tìm khách / cơ hội / phụ trách…" />
      </div>
      <SC.FilterChipRow>
        {chips.map(([v, lbl]) => (
          <SC.FilterChip key={v} on={level === v} onClick={() => setLevel(v)}>{lbl}</SC.FilterChip>
        ))}
        <SC.FilterChip on={riskOnly} onClick={() => setRiskOnly(r => !r)} tone="warn">
          <Icon name="warning" size={12} /> Đang nguội
        </SC.FilterChip>
      </SC.FilterChipRow>
      <SC.FilterButton count={advCount} open={advOpen} onClick={onAdvToggle} />
      <window.DataControls.StatRow shown={shown} total={total} suffix="cơ hội" />
    </div>
  );
}

// ─── 1 card (mobile) ──────────────────────────────────────────────────────────
function DealCard({ item, rank, onClick }) {
  const lv = dealLevel(item.level);
  const scored = item._hasScore;
  return (
    <div className="deal-card" onClick={onClick}>
      <div className="deal-card-top">
        <span className="deal-card-rank">{rank ? '#' + rank : '·'}</span>
        <div className="deal-card-id">
          <div className="deal-card-cust">{item.customerName}
            {item.riskFlag === 'nguoi' && <span className="deals-risk"><Icon name="warning" size={11} /> nguội</span>}
          </div>
          <div className="deal-card-deal">{item.title || item.code || ('#' + item.id)} · {item.statusName || '—'} · {item.ageDays}d</div>
        </div>
        {scored
          ? <span className="deals-win" style={{ color: lv.color, background: lv.bg }}>{item.winRate}%</span>
          : <span style={{fontSize: 11, color: 'var(--text-3)'}}>chưa chấm</span>}
      </div>
      <div className="deal-card-mid">
        {scored && <div className="deal-prio"><i style={{ width: `${Math.round(item.priorityScore)}%` }} /></div>}
        <span className="deal-card-vals">{vndShort(item.totalPrice)}{scored ? ' · EV ' + vndShort(item.expectedValue) : ''}</span>
      </div>
      {item.analysis?.nextAction && <div className="deal-card-action"><Icon name="zap" size={13} /> {item.analysis.nextAction}</div>}
    </div>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────
function DealsPage({ pushToast }) {
  // Board AI (đã chấm) — nguồn winRate/level/priorityScore. Merge vào list theo id.
  const [board, setBoard] = _dS(null);
  // List paginated từ /api/v1/deals — TẤT CẢ cơ hội (raw upstream), browse được kể cả chưa chấm.
  const [list, setList]         = _dS([]);
  const [total, setTotal]       = _dS(0);
  const [page, setPage]         = _dS(1);
  const [pageSize, setPageSize] = _dS(50);
  const [listLoading, setListLoading] = _dS(true);

  const [running, setRunning] = _dS(false);
  const [progress, setProgress] = _dS(null);
  const [sel, setSel] = _dS(null);

  // ─── Tự động phân tích ───────────────────────────────────────────────────
  // Toggle persist localStorage theo tenant. Khi BẬT + page mount + có cơ hội + chưa chấm gần đây
  // → tự run() phân tích. Chỉ trigger 1 lần per page-mount (autoTriedRef).
  const _autoKey = 'tourkit_auto_deal_' + (window.tourkitAuth?.getUser?.()?.tenantId || '');
  const [autoAnalyze, setAutoAnalyze] = _dS(() => localStorage.getItem(_autoKey) === 'on');
  const autoTriedRef = window.React.useRef(false);

  const toggleAutoAnalyze = () => {
    setAutoAnalyze(prev => {
      const next = !prev;
      try { localStorage.setItem(_autoKey, next ? 'on' : 'off'); } catch {}
      if (next) pushToast('Tự động phân tích BẬT — chạy khi vào page nếu chưa có bảng', 'info');
      else      pushToast('Tự động phân tích TẮT', 'warn');
      autoTriedRef.current = false;
      return next;
    });
  };
  // bộ lọc kết quả
  const [q, setQ] = _dS('');
  const [level, setLevel] = _dS('all');
  const [riskOnly, setRiskOnly] = _dS(false);

  // Bộ lọc nâng cao (client-side trên merged items — không gọi lại AI)
  const EMPTY_DEAL_FILTER = { status: '', source: '', staff: '', minValue: '', maxValue: '', maxAge: '', sortBy: 'priority' };
  const [adv, setAdv] = _dS(EMPTY_DEAL_FILTER);
  const [advOpen, setAdvOpen] = _dS(false);
  const advCount = ['status','source','staff','minValue','maxValue','maxAge'].filter(k => adv[k] && adv[k] !== '').length + (adv.sortBy && adv.sortBy !== 'priority' ? 1 : 0);
  const cancelUrl = _dR(null);
  const isMobile = useIsMobile();
  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const loadBoard = async () => {
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals/board');
      if (r.ok) { const b = await r.json(); if (b && b.items) setBoard(b); }
    } catch { /* ignore */ }
  };
  const loadList = async () => {
    setListLoading(true);
    try {
      const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals?' + q.toString());
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      setList(data.items || []);
      setTotal(data.total ?? (data.items?.length || 0));
    } catch (e) {
      pushToast('Không tải được danh sách cơ hội: ' + e.message, 'error');
    } finally { setListLoading(false); }
  };
  _dE(() => { loadBoard(); }, []);
  _dE(() => { loadList(); }, [page, pageSize]);
  _dE(() => { setPage(1); }, [pageSize]);   // đổi pageSize → về page 1

  // Auto-trigger: bật ON + đã load list + chưa chạy + board cũ (>30min hoặc rỗng) → chạy phân tích.
  _dE(() => {
    if (!autoAnalyze || running || listLoading || list.length === 0 || autoTriedRef.current) return;
    const recent = board?.generatedAt && (Date.now() - new Date(board.generatedAt).getTime() < 30 * 60 * 1000);
    if (recent) return;   // skip nếu vừa chấm < 30 phút
    autoTriedRef.current = true;
    pushToast(`Tự động phân tích ${list.length} cơ hội…`, 'info');
    setTimeout(() => run(), 300);
  }, [autoAnalyze, running, listLoading, list, board]);

  async function run() {
    setRunning(true); setProgress({ stage: 'scanning' });
    const live = [];
    try {
      const cfg = window.tourkit.ai.getConfig();
      const key = window.tourkit.ai.getKey(cfg.provider);
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals/analyze', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ provider: cfg.provider, model: cfg.model, apiKey: key || undefined }),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Không khởi động được phân tích', 'error'); setRunning(false); setProgress(null); return; }
      cancelUrl.current = data.cancelUrl;

      await readDealStream(data.streamUrl, (e) => {
        if (e.type === 'scanning') setProgress({ stage: 'scanning' });
        else if (e.type === 'ranked') setProgress({ stage: 'scoring', done: 0, total: e.payload.total, scanned: e.payload.scanned });
        else if (e.type === 'scored') { live.push(e.payload.item); setProgress(p => ({ ...p, stage: 'scoring', done: e.payload.done, total: e.payload.total })); }
        else if (e.type === 'done') {
          const b = e.payload.board || { items: [...live].sort((a, b) => b.priorityScore - a.priorityScore), scanned: 0, deepScored: live.length };
          setBoard(b);
          pushToast(`Đã phân tích ${b.deepScored || live.length} cơ hội ưu tiên`);
        }
        else if (e.type === 'error' && !e.payload) pushToast(e.error || 'Lỗi phân tích', 'error');
        else if (e.type === 'cancelled') pushToast('Đã hủy phân tích', 'error');
      });
    } catch (e) { pushToast('Lỗi: ' + e.message, 'error'); }
    finally { setRunning(false); setProgress(null); cancelUrl.current = null; }
  }

  async function cancel() {
    if (cancelUrl.current) { try { await window.tourkitAuth.authedFetch(cancelUrl.current, { method: 'POST' }); } catch {} }
  }

  // Merge list (real-time API) với board scores (cached AI). Row có score → có winRate/level/priorityScore;
  // chưa chấm → các field đó null/0 và `_hasScore=false` (UI hiển thị "—" thay vì số).
  const scoreMap = (() => {
    const m = new Map();
    (board?.items || []).forEach(b => m.set(b.id, b));
    return m;
  })();
  const items = list.map(d => {
    const sc = scoreMap.get(d.id);
    if (sc) return { ...d, ...sc, _hasScore: true };
    return { ...d, _hasScore: false, winRate: null, level: null, priorityScore: 0, expectedValue: 0 };
  });
  const ql = q.trim().toLowerCase();
  // Distinct values từ items để fill dropdown filter (không cần API lookups)
  const dealLookups = (() => {
    const s = new Set(), src = new Set(), staff = new Set();
    for (const it of items) {
      if (it.statusName) s.add(it.statusName);
      if (it.sourceName) src.add(it.sourceName);
      if (it.assignees) it.assignees.split(/[,;]/).forEach(a => a.trim() && staff.add(a.trim()));
    }
    return {
      statuses: [...s].sort(),
      sources:  [...src].sort(),
      staffs:   [...staff].sort(),
    };
  })();
  let filtered = items.filter(it => {
    if (level !== 'all' && it.level !== level) return false;
    if (riskOnly && !(it.isCooling || it.riskFlag === 'nguoi')) return false;
    if (ql) {
      const hay = (it.customerName + ' ' + (it.title || '') + ' ' + (it.code || '') + ' ' + (it.assignees || '')).toLowerCase();
      if (!hay.includes(ql)) return false;
    }
    if (adv.status && it.statusName !== adv.status) return false;
    if (adv.source && it.sourceName !== adv.source) return false;
    if (adv.staff && !(it.assignees || '').toLowerCase().includes(adv.staff.toLowerCase())) return false;
    if (adv.minValue && (it.totalPrice || 0) < Number(adv.minValue)) return false;
    if (adv.maxValue && (it.totalPrice || 0) > Number(adv.maxValue)) return false;
    if (adv.maxAge && (it.ageDays || 0) > Number(adv.maxAge)) return false;
    return true;
  });
  // Sắp xếp tùy chọn (mặc định = priority desc)
  filtered = [...filtered].sort((a, b) => {
    if (adv.sortBy === 'value') return (b.totalPrice || 0) - (a.totalPrice || 0);
    if (adv.sortBy === 'win')   return (b.winRate || 0) - (a.winRate || 0);
    if (adv.sortBy === 'age')   return (b.ageDays || 0) - (a.ageDays || 0);
    if (adv.sortBy === 'ev')    return (b.expectedValue || 0) - (a.expectedValue || 0);
    return (b.priorityScore || 0) - (a.priorityScore || 0);
  });

  // KPI strip counts — "Tổng" = total upstream (full DB); các count còn lại = trên BOARD đã chấm
  // (vì unscored row không có level/riskFlag). Label kèm "/đã chấm" cho rõ.
  const boardItems = board?.items || [];
  const c = {
    total: total,
    cao:   boardItems.filter(it => it.level === 'cao').length,
    tb:    boardItems.filter(it => it.level === 'trung_binh').length,
    thap:  boardItems.filter(it => it.level === 'thap').length,
    nguoi: boardItems.filter(it => (it.isCooling || it.riskFlag === 'nguoi')).length,
  };

  return (
    <main className="page deals">
      <window.PageShell.PageHero
        icon="trend"
        title="Ưu tiên Deal AI"
        badge="EV + độ gấp"
        sub="AI chấm khả năng thắng từng cơ hội bán hàng và xếp hạng nên xử lý deal nào trước."
        status={{ label: total > 0 ? `${total} CƠ HỘI · ${boardItems.length} ĐÃ CHẤM` : (running ? 'ĐANG PHÂN TÍCH' : (listLoading ? 'ĐANG TẢI' : 'CHƯA CÓ DỮ LIỆU')),
          detail: board?.generatedAt ? `Chấm lần cuối ${new Date(board.generatedAt).toLocaleTimeString('vi-VN')}` : 'Bấm "Phân tích AI" để chấm điểm',
          tone: total > 0 ? 'live' : 'idle' }}
        actions={<>
          <button className={'btn btn-sm autotoggle ' + (autoAnalyze ? 'on' : 'off')}
            onClick={toggleAutoAnalyze} title="Tự động phân tích cơ hội khi mở page (lưu theo tài khoản, skip nếu vừa chấm < 30 phút)">
            <Icon name={autoAnalyze ? 'check' : 'close'} size={14} /> Tự động {autoAnalyze ? 'ON' : 'OFF'}
          </button>
          {running
            ? <button className="deals-btn" onClick={cancel}><Icon name="close" size={15} /> Hủy</button>
            : <button className="deals-btn primary" onClick={run}><Icon name="sparkle" size={15} /> Phân tích AI</button>}
        </>}
      />

      {running && progress && (
        <div className="deals-progress">
          <span className="deals-spin" />
          {progress.stage === 'scanning'
            ? <span>Đang quét pipeline cơ hội đang mở…</span>
            : <span>Đang chấm sâu <b>{progress.done || 0}/{progress.total || 0}</b> cơ hội tiềm năng
                {progress.scanned ? <em> (quét {progress.scanned} cơ hội mở)</em> : null}</span>}
          {progress.total > 0 && <div className="deals-bar"><i style={{ width: `${Math.round(100 * (progress.done || 0) / progress.total)}%` }} /></div>}
        </div>
      )}

      {!running && listLoading && items.length === 0 && (
        <div className="deals-empty"><Icon name="trend" size={28} /><div>Đang tải danh sách cơ hội…</div></div>
      )}
      {!running && !listLoading && items.length === 0 && (
        <div className="deals-empty">
          <Icon name="trend" size={28} />
          <div><b>Chưa có cơ hội bán hàng</b><br />Khi có booking-ticket trên CRM, danh sách sẽ hiện ở đây.</div>
        </div>
      )}

      {items.length > 0 && (<>
        {board?.generatedAt && (
          <div className="deals-meta">
            Top {board.deepScored} cơ hội ưu tiên · quét {board.scanned} cơ hội mở · cập nhật {new Date(board.generatedAt).toLocaleString('vi-VN')}
          </div>
        )}

        <window.DataControls.KpiStrip items={[
          { icon: 'trend',   label: 'Tổng cơ hội',     value: c.total },
          { icon: 'star',    label: 'Win cao /đã chấm', value: c.cao, highlight: c.cao > 0 },
          { icon: 'sparkle', label: 'Win TB /đã chấm', value: c.tb },
          { icon: 'warning', label: 'Win thấp /đã chấm', value: c.thap },
          { icon: 'warning', label: 'Nguội /đã chấm',  value: c.nguoi },
        ]} />

        <DealFilters q={q} setQ={setQ} level={level} setLevel={setLevel}
          riskOnly={riskOnly} setRiskOnly={setRiskOnly} shown={filtered.length} total={items.length}
          advCount={advCount} advOpen={advOpen} onAdvToggle={() => setAdvOpen(o => !o)} />

        <window.SearchControls.AdvancedFilterPanel
          open={advOpen} onClose={() => setAdvOpen(false)}
          value={adv} defaultValue={EMPTY_DEAL_FILTER} onApply={setAdv}>
          {({ draft, set }) => (
            <div className="cust-sheet-grid">
              <div className="cust-sheet-row">
                <label>Trạng thái</label>
                <window.SearchControls.SearchSelect
                  items={['', ...dealLookups.statuses].map(s => s || 'Tất cả trạng thái')}
                  value={draft.status || 'Tất cả trạng thái'}
                  onChange={v => set('status', v === 'Tất cả trạng thái' ? '' : v)}
                  placeholder="Tất cả trạng thái" />
              </div>
              <div className="cust-sheet-row">
                <label>Nguồn</label>
                <window.SearchControls.SearchSelect
                  items={['', ...dealLookups.sources].map(s => s || 'Tất cả nguồn')}
                  value={draft.source || 'Tất cả nguồn'}
                  onChange={v => set('source', v === 'Tất cả nguồn' ? '' : v)}
                  placeholder="Tất cả nguồn" />
              </div>
              <div className="cust-sheet-row">
                <label>NV phụ trách</label>
                <window.SearchControls.SearchSelect
                  items={['', ...dealLookups.staffs].map(s => s || 'Tất cả nhân viên')}
                  value={draft.staff || 'Tất cả nhân viên'}
                  onChange={v => set('staff', v === 'Tất cả nhân viên' ? '' : v)}
                  placeholder="Tất cả nhân viên" />
              </div>
              <div className="cust-sheet-row">
                <label>Tuổi cơ hội tối đa (ngày)</label>
                <input type="number" className="tb-field" placeholder="vd 30" value={draft.maxAge}
                  onChange={e => set('maxAge', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row">
                <label>Giá trị tối thiểu (đ)</label>
                <input type="number" className="tb-field" placeholder="vd 10000000" value={draft.minValue}
                  onChange={e => set('minValue', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row">
                <label>Giá trị tối đa (đ)</label>
                <input type="number" className="tb-field" placeholder="bỏ trống = không giới hạn" value={draft.maxValue}
                  onChange={e => set('maxValue', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row full">
                <label>Sắp xếp</label>
                <window.SearchControls.FilterChipRow>
                  {[['priority', 'Ưu tiên (mặc định)'], ['ev', 'Doanh thu kỳ vọng'], ['value', 'Giá trị deal'], ['win', 'Khả năng thắng'], ['age', 'Tuổi cơ hội']].map(([v, l]) => (
                    <window.SearchControls.FilterChip key={v} on={draft.sortBy === v}
                      onClick={() => set('sortBy', v)}>{l}</window.SearchControls.FilterChip>
                  ))}
                </window.SearchControls.FilterChipRow>
              </div>
            </div>
          )}
        </window.SearchControls.AdvancedFilterPanel>

        {filtered.length === 0 ? (
          <div className="deals-empty sm"><Icon name="search" size={22} /><div>Không có cơ hội nào khớp bộ lọc.</div></div>
        ) : isMobile ? (
          <div className="deals-cards">
            {filtered.map((it) => {
              const rank = it._hasScore ? (boardItems.findIndex(b => b.id === it.id) + 1) : null;
              return <DealCard key={it.id} item={it} rank={rank} onClick={() => setSel(it)} />;
            })}
          </div>
        ) : (
          <div className="deals-tablewrap">
            <table className="deals-table">
              <colgroup>
                <col className="col-rank" />
                <col className="col-cust" />
                <col className="col-val" />
                <col className="col-win" />
                <col className="col-prio" />
                <col className="col-action" />
                <col className="col-go" />
              </colgroup>
              <thead><tr>
                <th>#</th><th>Khách hàng / Cơ hội</th><th>Giá trị</th><th>Win</th>
                <th>Ưu tiên</th><th>Hành động nên làm</th><th></th>
              </tr></thead>
              <tbody>
                {filtered.map((it) => {
                  const lv = dealLevel(it.level);
                  // Rank chỉ có nghĩa cho deal đã chấm — unscored hiện "—" thay vì index list.
                  const rank = it._hasScore ? (boardItems.findIndex(b => b.id === it.id) + 1) : null;
                  return (
                    <tr key={it.id} onClick={() => setSel(it)} className="deals-row">
                      <td className="deals-rank">{rank || <span style={{color:'var(--text-3)'}}>—</span>}</td>
                      <td>
                        <div className="deals-cust">{it.customerName}
                          {(it.isCooling || it.riskFlag === 'nguoi') && <span className="deals-risk" title="Đang nguội"><Icon name="warning" size={11} /> nguội</span>}
                        </div>
                        <div className="deals-deal">{it.title || it.code || ('#' + it.id)} · {it.statusName || '—'} · {it.ageDays}d</div>
                      </td>
                      <td className="deals-val">{vndShort(it.totalPrice)}</td>
                      <td>
                        {it._hasScore
                          ? <span className="deals-win" style={{ color: lv.color, background: lv.bg }}>{it.winRate}%</span>
                          : <span style={{color:'var(--text-3)', fontSize:12}}>chưa chấm</span>}
                      </td>
                      <td>
                        {it._hasScore ? (<>
                          <div className="deals-prio"><i style={{ width: `${Math.round(it.priorityScore)}%` }} /></div>
                          <span className="deals-ev">EV {vndShort(it.expectedValue)}</span>
                        </>) : <span style={{color:'var(--text-3)'}}>—</span>}
                      </td>
                      <td className="deals-action">{it.analysis?.nextAction || <span style={{color:'var(--text-3)'}}>—</span>}</td>
                      <td className="deals-go"><Icon name="chevronRight" size={16} /></td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {/* Pagination — chỉ hiện khi có >1 trang. Dùng total thật từ /api/v1/deals. */}
        {!listLoading && total > pageSize && (
          <window.TKPagination page={page} totalPages={totalPages} pageSize={pageSize}
            total={total} shown={filtered.length}
            onPage={setPage} onPageSize={setPageSize} />
        )}
      </>)}

      {sel && <DealDrawer item={sel} onClose={() => setSel(null)} />}
    </main>
  );
}

window.DealsPage = DealsPage;
