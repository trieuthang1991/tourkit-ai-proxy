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

  // ─── Multi-select để chấm AI những deal cụ thể (giống Khách hàng) ────────
  // Khi `selectedIds.size > 0` → bấm "Chấm AI N deal" gọi POST với dealIds: [...]
  // Khi size = 0 → bấm "Phân tích AI" chấm top 20 ưu tiên auto.
  const [selectedIds, setSelectedIds] = _dS(new Set());
  const toggleDeal = (id) => setSelectedIds(s => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const clearSelected = () => setSelectedIds(new Set());

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

  // Reset autoTriedRef CHỈ khi user navigate (page/filter/search) — giống Khách hàng.
  // KHÔNG reset khi list refresh sau batch → tránh chain auto-batch + spam toast.
  // 1 page = 1 batch auto duy nhất. Muốn batch tiếp → user paginate hoặc bấm "Phân tích AI" tay.
  _dE(() => {
    console.log('[auto-deal] reset autoTriedRef (navigation đổi)');
    autoTriedRef.current = false;
  }, [page, pageSize, adv, q, level, riskOnly]);

  // Auto-trigger: pick deal có scoreStatus !== 'fresh' từ list (giống Khách hàng).
  // Server đã trả per-item scoreStatus dựa trên cache → frontend KHÔNG cần merge board.
  // Sau mỗi batch xong, loadList() refresh list → scoreStatus per item cập nhật → effect re-run.
  _dE(() => {
    const dbg = (m) => console.log('[auto-deal]', m);
    if (!autoAnalyze)            { dbg('skip: autoAnalyze OFF'); return; }
    if (running)                 { dbg('skip: đang running'); return; }
    if (listLoading)             { dbg('skip: list đang loading'); return; }
    if (list.length === 0)       { dbg('skip: list rỗng'); return; }
    if (autoTriedRef.current)    { dbg('skip: đã trigger cho state hiện tại'); return; }

    const unscored = list.filter(d => !d.scoreStatus || d.scoreStatus === 'none').slice(0, 50);
    if (unscored.length === 0) {
      dbg('skip: trang này không có deal scoreStatus=none');
      pushToast('Tự động: trang này không có deal nào chưa chấm', 'info');
      autoTriedRef.current = true;
      return;
    }
    autoTriedRef.current = true;
    dbg(`trigger chấm ${unscored.length} deal scoreStatus=none (trên ${list.length} đang xem)`);
    const ids = new Set(unscored.map(d => String(d.id)));
    pushToast(`Tự động chấm ${unscored.length} deal chưa chấm…`, 'info');
    setTimeout(() => run(ids), 300);
  }, [autoAnalyze, running, listLoading, list]);

  async function run(overrideIds = null) {
    setRunning(true); setProgress({ stage: 'scanning' });
    const live = [];
    try {
      const cfg = window.tourkit.ai.getConfig();
      const key = window.tourkit.ai.getKey(cfg.provider);
      // Nếu user chọn cụ thể → gửi dealIds; nếu auto → để trống, backend lấy top 20 ưu tiên.
      const useIds = overrideIds || (selectedIds.size > 0 ? selectedIds : null);
      const body = { provider: cfg.provider, model: cfg.model, apiKey: key || undefined };
      if (useIds) body.dealIds = [...useIds];
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals/analyze', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
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
          pushToast(`Đã chấm ${b.deepScored || live.length} cơ hội`);
          // QUAN TRỌNG: refresh list để scoreStatus per item cập nhật từ cache backend
          // (giống pattern /customers: batch xong → loadList → reviewStatus per item refresh).
          // Auto effect dưới sẽ tự re-run với scoreStatus mới → trigger batch tiếp nếu còn 'none'.
          loadList();
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

  // Server đã trả per-item score data (cached) — frontend chỉ cần map ra field phẳng cho UI.
  // _hasScore = !!d.score (true nếu có cache). KHÔNG cần merge với board.items nữa.
  const items = list.map(d => {
    if (d.score) {
      return {
        ...d,
        winRate: d.score.winRate, level: d.score.level,
        priorityScore: d.score.priorityScore || 0, expectedValue: d.score.expectedValue || 0,
        analysis: d.score, _hasScore: true
      };
    }
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
          {selectedIds.size > 0 && !running && (
            <button className="btn btn-ghost btn-sm" onClick={clearSelected} title="Bỏ chọn">
              <Icon name="close" size={14} /> Bỏ chọn {selectedIds.size}
            </button>
          )}
          {running
            ? <button className="deals-btn" onClick={cancel}><Icon name="close" size={15} /> Hủy</button>
            : <button className="deals-btn primary" onClick={() => run()}>
                <Icon name="sparkle" size={15} />
                {selectedIds.size > 0 ? `Chấm AI ${selectedIds.size} deal` : 'Phân tích AI (top 20 ưu tiên)'}
              </button>}
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
                <col className="col-check" />
                <col className="col-rank" />
                <col className="col-cust" />
                <col className="col-val" />
                <col className="col-win" />
                <col className="col-prio" />
                <col className="col-action" />
                <col className="col-go" />
              </colgroup>
              <thead><tr>
                <th>
                  {(() => {
                    const allOn = filtered.length > 0 && filtered.every(it => selectedIds.has(it.id));
                    const someOn = !allOn && filtered.some(it => selectedIds.has(it.id));
                    return (
                      <window.TKCheckbox
                        checked={allOn}
                        indeterminate={someOn}
                        disabled={running}
                        ariaLabel="Chọn tất cả deal trên trang"
                        onChange={(checked) => setSelectedIds(s => {
                          const n = new Set(s);
                          filtered.forEach(it => checked ? n.add(it.id) : n.delete(it.id));
                          return n;
                        })} />
                    );
                  })()}
                </th>
                <th>#</th><th>Khách hàng / Cơ hội</th><th>Giá trị</th><th>Win</th>
                <th>Ưu tiên</th><th>Hành động nên làm</th><th></th>
              </tr></thead>
              <tbody>
                {filtered.map((it) => {
                  const lv = dealLevel(it.level);
                  // Rank chỉ có nghĩa cho deal đã chấm — unscored hiện "—" thay vì index list.
                  const rank = it._hasScore ? (boardItems.findIndex(b => b.id === it.id) + 1) : null;
                  return (
                    <tr key={it.id} onClick={() => setSel(it)}
                        className={'deals-row' + (selectedIds.has(it.id) ? ' is-selected' : '')}>
                      <td onClick={(e) => e.stopPropagation()}>
                        <window.TKCheckbox
                          checked={selectedIds.has(it.id)}
                          disabled={running}
                          ariaLabel={`Chọn deal ${it.code || it.id}`}
                          onChange={() => toggleDeal(it.id)} />
                      </td>
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
