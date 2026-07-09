// pages/deals.jsx — AI phân tích Cơ hội. Product UI nội bộ, bám design system warm-orange.
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

// isMobile hook (≤640px) → dùng chung window.tourkitHooks.useIsMobile (lib/hooks.jsx)

// Đọc SSE stream → onEvent(obj) mỗi event. Plumbing dùng chung window.tourkitUtil.readSSE.
async function readDealStream(url, onEvent) {
  // Gắn X-Session-Id: stream endpoint giờ verify session + tenant (như /reviews/batch stream).
  // Không gắn → 401. (Dùng header thay query để không lộ sessionId trên URL.)
  const sid = window.tourkitAuth && window.tourkitAuth.getSessionId ? window.tourkitAuth.getSessionId() : null;
  const headers = { Accept: 'text/event-stream' };
  if (sid) headers['X-Session-Id'] = sid;
  const resp = await fetch(url, { headers });
  if (!resp.ok || !resp.body) throw new Error('Stream không khả dụng (' + resp.status + ')');
  await window.tourkitUtil.readSSE(resp, onEvent);
}

// ─── Drawer chi tiết 1 deal ───────────────────────────────────────────────────
function DealDrawer({ item: initialItem, onClose, onRescored, pushToast }) {
  const [item, setItem] = React.useState(initialItem);
  const [rescoring, setRescoring] = React.useState(false);
  React.useEffect(() => { setItem(initialItem); }, [initialItem]);
  if (!item) return null;
  const lv = dealLevel(item.level);
  const a = item.analysis || {};

  // Gọi endpoint sync POST /deals/{id}/rescore → nhận DealBoardItem mới → cập nhật state drawer.
  const rescore = async () => {
    setRescoring(true);
    try {
      const resp = await window.tourkitAuth.authedFetch(`/api/v1/deals/${item.id}/rescore`, { method: 'POST' });
      const json = await resp.json();
      if (!resp.ok) throw new Error(json.error || 'Chấm lại thất bại');
      setItem(json.item);
      pushToast?.('✓ Đã chấm lại cơ hội');
      onRescored?.(json.item);
    } catch (e) {
      pushToast?.('Chấm lại lỗi: ' + e.message, 'warn');
    } finally {
      setRescoring(false);
    }
  };

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

      {/* Footer: nút Chấm lại (mirror Customer review "Cập nhật review") */}
      <div style={{marginTop: 16, display: 'flex', justifyContent: 'flex-end'}}>
        <button className="btn btn-primary" onClick={rescore} disabled={rescoring} style={{gap: 6}}>
          <Icon name="refresh" size={13} /> {rescoring ? 'Đang chấm...' : 'Chấm lại'}
        </button>
      </div>
    </aside>
  </>);
}

// ─── Bộ lọc kết quả ─── (mượn pattern mobile qua window.SearchControls) ───
// Filter bar: search (q) + chip Win (rank/minRank/maxRank) + nút Bộ lọc nâng cao.
// TẤT CẢ filter (q / trạng thái / nguồn / NV / rank / tuổi / giá trị) đẩy SERVER-SIDE qua
// /api/ai/booking-tickets. Riêng "Sắp xếp" client-side (priority/EV là heuristic proxy, không phải cột DB).
function DealFilters({ q, setQ, shown, total, advCount, advOpen, onAdvToggle, rankFilter, setRankFilter }) {
  const SC = window.SearchControls;
  // Win chip ngưỡng đồng bộ DealScoringService.cs: cao ≥60, TB 35-59, thấp <35.
  // Token gửi xuống proxy:
  //   any  → rank=any        (đã chấm bất kỳ)
  //   none → rank=-1         (chưa chấm)
  //   high → minRank=60      (Win cao)
  //   mid  → minRank=35&maxRank=59
  //   low  → minRank=1&maxRank=34
  return (
    <div className="cust-filter">
      <div className="cust-filter-search">
        <SC.SearchInput value={q} onChange={setQ} submitOnly
          placeholder="Tìm khách / cơ hội / phụ trách… (Enter để tìm)" />
      </div>
      <SC.FilterChipRow>
        {[['all','Tất cả'],['any','Đã chấm'],['none','Chưa chấm'],['high','Win cao'],['mid','Win TB'],['low','Win thấp']].map(([v, l]) => (
          <SC.FilterChip key={v} on={rankFilter === v} onClick={() => setRankFilter(v)}>{l}</SC.FilterChip>
        ))}
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

  // Confirm modal: hiện trước khi chấm thủ công (manual). Auto skip modal.
  const [confirmOpen, setConfirmOpen] = _dS(false);

  // Per-deal status (tương tự Customer): { dealId: { stage: 'queue'|'done'|'error' } }
  // Backend chỉ emit `scored` khi 1 deal chấm xong (không có "starting" per deal) → chỉ track
  // 2 trạng thái real: queue (chờ + đang chấm song song concurrency=6) → done.
  const [rowStatus, setRowStatus] = _dS({});

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
  // Win-rate filter chip ('all'|'any'|'none'|'high'|'mid'|'low')
  const [rankFilter, setRankFilter] = _dS('all');
  // Lookups cho dropdown (statuses/sources/staffs từ upstream /api/ai/reference)
  const [lookups, setLookups] = _dS({ statuses: [], sources: [], staffs: [] });

  // Bộ lọc nâng cao — ĐẨY SERVER-SIDE qua loadList → /api/ai/booking-tickets:
  //   • status/source/staff → trangThai/nguon/nhanVienPhuTrach (int ID)
  //   • customMinRank/customMaxRank → minRank/maxRank (% khả năng thắng)
  //   • maxAge → startDate (proxy convert = hôm nay − N) ; minValue/maxValue → minPrice/maxPrice
  //   • sortBy: client-side (priority/EV proxy-computed, không phải cột DB). Default 'newest' (id desc).
  const EMPTY_DEAL_FILTER = { status: 0, source: 0, staff: 0, minValue: '', maxValue: '', maxAge: '', sortBy: 'newest', customMinRank: '', customMaxRank: '' };
  const [adv, setAdv] = _dS(EMPTY_DEAL_FILTER);
  const [advOpen, setAdvOpen] = _dS(false);
  const advCount = (adv.status > 0 ? 1 : 0) + (adv.source > 0 ? 1 : 0) + (adv.staff > 0 ? 1 : 0)
                 + ['minValue','maxValue','maxAge','customMinRank','customMaxRank'].filter(k => adv[k] && adv[k] !== '').length
                 + (adv.sortBy && adv.sortBy !== 'newest' ? 1 : 0);
  const cancelUrl = _dR(null);
  const isMobile = window.tourkitHooks.useIsMobile();
  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const loadBoard = async () => {
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals/board');
      if (r.ok) { const b = await r.json(); if (b && b.items) setBoard(b); }
    } catch { /* ignore */ }
  };
  // loadList nhận trực tiếp page/pageSize/q làm tham số → không lệ thuộc closure stale.
  // Trước đây dùng `page` từ closure → khi search reset về 1, lần fetch đầu vẫn dùng page CŨ
  // (ví dụ user ở page 3 → search "abc" → fetch ?page=3&q=abc trả empty → page→1 mới fetch lại).
  const loadList = async (overrides = {}) => {
    setListLoading(true);
    try {
      const p   = overrides.page     ?? page;
      const ps  = overrides.pageSize ?? pageSize;
      const trm = ((overrides.q ?? q) || '').trim();
      const a   = overrides.adv ?? adv;
      const params = new URLSearchParams({ page: String(p), pageSize: String(ps) });
      // ↑ rankFilter có thể được truyền vào qua overrides để effect "đổi chip → reload" tránh closure stale
      if (trm) params.set('q', trm);   // server-side keyword search → TourKit upstream
      // Upstream filter (int ID) → đi thẳng /api/ai/booking-tickets
      if (a.status > 0) params.set('trangThai',        String(a.status));
      if (a.source > 0) params.set('nguon',            String(a.source));
      if (a.staff  > 0) params.set('nhanVienPhuTrach', String(a.staff));
      // ── Win-rate filter ──────────────────────────────────────────────────────
      // Khoảng tùy chỉnh (sheet nâng cao) GHI ĐÈ chip nhanh.
      // Chip: any/none → param `rank`; high/mid/low → param `minRank`/`maxRank`.
      const cMin = a.customMinRank, cMax = a.customMaxRank;
      const hasCustom = (cMin && Number(cMin) > 0) || (cMax && Number(cMax) > 0);
      if (hasCustom) {
        if (cMin && Number(cMin) > 0) params.set('minRank', String(cMin));
        if (cMax && Number(cMax) > 0) params.set('maxRank', String(cMax));
      } else {
        const rf = overrides.rankFilter ?? rankFilter;
        if (rf === 'any')  params.set('rank', 'any');
        if (rf === 'none') params.set('rank', '-1');
        if (rf === 'high') params.set('minRank', '60');
        if (rf === 'mid')  { params.set('minRank', '35'); params.set('maxRank', '59'); }
        if (rf === 'low')  { params.set('minRank', '1');  params.set('maxRank', '34'); }
      }
      // Tuổi + giá trị → server-side toàn DB (upstream: startDate / minPrice / maxPrice).
      if (a.maxAge   && Number(a.maxAge)   > 0) params.set('maxAge',   String(a.maxAge));
      if (a.minValue && Number(a.minValue) > 0) params.set('minValue', String(a.minValue));
      if (a.maxValue && Number(a.maxValue) > 0) params.set('maxValue', String(a.maxValue));
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals?' + params.toString());
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const data = await r.json();
      setList(data.items || []);
      setTotal(data.total ?? (data.items?.length || 0));
      // Lookups (statuses/sources/staffs) đính kèm response /deals. Reference upstream HIẾM khi đổi,
      // nhưng CÓ THỂ trả rỗng nhất thời (JWT hết hạn giữa chừng / reference blip) → KHÔNG được xóa
      // dropdown đã có. Keep-last-good: mỗi list chỉ thay khi payload mới có dữ liệu, rỗng thì giữ cũ.
      if (data.lookups) setLookups(prev => ({
        statuses: data.lookups.statuses?.length ? data.lookups.statuses : prev.statuses,
        sources:  data.lookups.sources?.length  ? data.lookups.sources  : prev.sources,
        staffs:   data.lookups.staffs?.length   ? data.lookups.staffs   : prev.staffs,
      }));
    } catch (e) {
      pushToast('Không tải được danh sách cơ hội: ' + e.message, 'error');
    } finally { setListLoading(false); }
  };
  _dE(() => { loadBoard(); }, []);
  _dE(() => { loadList(); }, [page, pageSize]);
  _dE(() => { setPage(1); }, [pageSize]);   // đổi pageSize → về page 1
  // Search debounce 350ms: user vẫn gõ thì hoãn fetch để khỏi spam API.
  // Skip mount-fire (q='' lần đầu — vì `[page,pageSize]` effect đã fetch). Ref-guard tránh
  // race với [page,pageSize] effect khi reset page về 1 (chỉ gọi `setPage(1)` để cho effect
  // duy nhất đó fetch; nếu page ĐANG = 1 thì gọi loadList trực tiếp với q mới).
  const qMounted = window.React.useRef(false);
  _dE(() => {
    if (!qMounted.current) { qMounted.current = true; return; }
    const t = setTimeout(() => {
      if (page !== 1) setPage(1);                  // → [page] effect sẽ fetch với q mới
      else loadList({ page: 1, q });               // page đã 1 → tự fetch
    }, 350);
    return () => clearTimeout(t);
  }, [q]);

  // Reset autoTriedRef CHỈ khi user navigate (page/filter/search) — giống Khách hàng.
  // KHÔNG reset khi list refresh sau batch → tránh chain auto-batch + spam toast.
  // 1 page = 1 batch auto duy nhất. Muốn batch tiếp → user paginate hoặc bấm "Phân tích AI" tay.
  _dE(() => {
    console.log('[auto-deal] reset autoTriedRef (navigation đổi)');
    autoTriedRef.current = false;
    clearSelected();   // page-scoped: sang trang/đổi filter/search → bỏ tick trang cũ (tránh chấm trùng deal khuất mắt)
  }, [page, pageSize, adv, q, rankFilter]);

  // Đổi chip rank → reload list ngay (server-side filter qua param `rank`/`minRank`/`maxRank`).
  // Skip mount-fire (rankFilter='all' lần đầu); đẩy về page 1 nếu cần.
  const rkMounted = window.React.useRef(false);
  _dE(() => {
    if (!rkMounted.current) { rkMounted.current = true; return; }
    if (page !== 1) setPage(1);                       // [page] effect sẽ fetch
    else            loadList({ page: 1, rankFilter }); // page = 1 → tự fetch
  }, [rankFilter]);

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
    const ids = new Set(unscored.map(d => d.id));
    setSelectedIds(ids);   // tick checkbox để user nhìn thấy deal nào sẽ được chấm
    pushToast(`Tự động chấm ${unscored.length} deal chưa chấm…`, 'info');
    // Delay 500ms để user kịp thấy tick + highlight trước khi spinner che → UX rõ ràng hơn
    setTimeout(() => run(new Set([...ids].map(String))), 500);
  }, [autoAnalyze, running, listLoading, list]);

  async function run(overrideIds = null) {
    setRunning(true); setProgress({ stage: 'scanning' });
    const useIds = overrideIds || (selectedIds.size > 0 ? selectedIds : null);
    // Init rowStatus = queue cho tất cả selected → pipeline hiển thị "Chờ: N"
    if (useIds) {
      const initial = {};
      [...useIds].forEach(id => { initial[id] = { stage: 'queue' }; });
      setRowStatus(initial);
    } else { setRowStatus({}); }
    const live = [];
    try {
      // Model do server quyết qua appsettings Models:DealScoring (xem AiModelRegistry).
      // FE KHÔNG gửi provider/model nữa — DealBatchService ignore override; gửi lên chỉ là noise.
      const body = {};
      if (useIds) body.dealIds = [...useIds].map(String);
      const r = await window.tourkitAuth.authedFetch('/api/v1/deals/analyze', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const data = await r.json();
      if (!r.ok) { pushToast(data.error || 'Không khởi động được phân tích', 'error'); setRunning(false); setProgress(null); return; }
      cancelUrl.current = data.cancelUrl;

      await readDealStream(data.streamUrl, (e) => {
        if (e.type === 'scanning') setProgress({ stage: 'scanning' });
        else if (e.type === 'ranked') {
          setProgress({ stage: 'scoring', done: 0, total: e.payload.total, scanned: e.payload.scanned });
        }
        else if (e.type === 'scored') {
          live.push(e.payload.item);
          // Đánh dấu deal vừa chấm xong → pipeline "Xong" tăng, "Chờ" giảm
          const sid = e.payload.item?.id;
          if (sid != null) setRowStatus(s => ({ ...s, [sid]: { stage: 'done', score: e.payload.item } }));
          setProgress(p => ({ ...p, stage: 'scoring', done: e.payload.done, total: e.payload.total }));
        }
        else if (e.type === 'done') {
          const b = e.payload.board || { items: [...live].sort((a, b) => b.priorityScore - a.priorityScore), scanned: 0, deepScored: live.length };
          setBoard(b);
          pushToast(`Đã chấm ${b.deepScored || live.length} cơ hội`);
          clearSelected();   // clear chọn sau khi xong → bấm Chấm lại không chấm trùng deal cũ
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
  // Dropdown statuses/sources/staffs nạp từ `lookups` (bundle trong response /api/v1/deals →
  // backend gọi /api/ai/reference). KHÔNG derive client-side từ items (50 dòng/trang → option lệch trang).
  // MỌI filter (q/status/source/staff/rank/maxAge/minValue/maxValue) đã đẩy server-side qua loadList.
  // Client-side CHỈ còn SORT (priority/EV là heuristic proxy tính, không phải cột DB).
  let filtered = [...items];
  // Sắp xếp tùy chọn (mặc định = newest = id desc, match mobile).
  filtered = [...filtered].sort((a, b) => {
    if (adv.sortBy === 'priority') return (b.priorityScore || 0) - (a.priorityScore || 0);
    if (adv.sortBy === 'value')    return (b.totalPrice || 0) - (a.totalPrice || 0);
    if (adv.sortBy === 'win')      return (b.winRate || 0) - (a.winRate || 0);
    if (adv.sortBy === 'age')      return (b.ageDays || 0) - (a.ageDays || 0);
    if (adv.sortBy === 'ev')       return (b.expectedValue || 0) - (a.expectedValue || 0);
    return (b.id || 0) - (a.id || 0);
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
        title="AI phân tích Cơ hội"
        badge="EV + độ gấp"
        sub="AI chấm khả năng thắng từng cơ hội bán hàng và xếp hạng nên xử lý deal nào trước."
        status={{ label: total > 0 ? `${total} CƠ HỘI` : (running ? 'ĐANG PHÂN TÍCH' : (listLoading ? 'ĐANG TẢI' : 'CHƯA CÓ DỮ LIỆU')),
          detail: selectedIds.size > 0 ? `${selectedIds.size} đã chọn` : 'Chọn cơ hội để chấm',
          tone: total > 0 ? 'live' : 'idle' }}
        actions={<>
          <button className={'btn btn-sm autotoggle ' + (autoAnalyze ? 'on' : 'off')}
            onClick={toggleAutoAnalyze}
            title="Tự động chấm cơ hội chưa có score khi mở page (lưu theo tài khoản)">
            <Icon name={autoAnalyze ? 'check' : 'close'} size={14} /> Tự động {autoAnalyze ? 'ON' : 'OFF'}
          </button>
          <button className="btn btn-ghost btn-sm" onClick={loadList} disabled={listLoading || running}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          {running
            ? <button className="btn btn-ghost btn-sm" onClick={cancel}>
                <Icon name="close" size={14} /> Hủy
              </button>
            : <button className="btn btn-primary btn-sm"
                disabled={selectedIds.size === 0}
                onClick={() => setConfirmOpen(true)}>
                <Icon name="sparkle" size={14} />
                Chấm {selectedIds.size > 0 ? `${selectedIds.size} cơ hội` : 'bằng AI'}
              </button>}
        </>}
      />

      {/* Progress panel khi đang chạy: pipeline stages + bar + ĐANG CHẤM box (pattern Khách hàng) */}
      {running && (() => {
        const total = progress?.total || selectedIds.size;
        const done = Object.values(rowStatus).filter(s => s?.stage === 'done').length;
        const remaining = Math.max(0, total - done);
        const concurrency = 6;
        const calling = Math.min(concurrency, remaining);
        const queue = Math.max(0, remaining - calling);
        const pct = total ? Math.round(100 * done / total) : 0;
        // Danh sách deal đang được AI chấm (queue stage nhưng giới hạn theo concurrency)
        const inFlight = [...selectedIds].filter(id => rowStatus[id]?.stage !== 'done').slice(0, concurrency);
        return (
          <div className="deals-batch-panel">
            <div className="deals-batch-stages">
              <span className="deals-stage" style={{ color: 'var(--text-3)' }}>
                <Icon name="users" size={12} /> Chờ <b>{queue}</b>
              </span>
              <span className="deals-stage-arrow">→</span>
              <span className="deals-stage pulse" style={{ color: '#f59e0b' }}>
                <Icon name="sparkle" size={12} /> AI <b>{calling}</b>
              </span>
              <span className="deals-stage-arrow">→</span>
              <span className="deals-stage" style={{ color: '#16a34a' }}>
                <Icon name="check" size={12} /> Xong <b>{done}</b>
              </span>
              <div style={{ flex: 1 }} />
              <button className="btn btn-ghost btn-sm" onClick={cancel}>
                <Icon name="close" size={12} /> Dừng
              </button>
            </div>
            <div className="deals-batch-meta">
              <span>{done}/{total} cơ hội xong</span>
              <span>{pct}%</span>
            </div>
            <div className="deals-batch-bar"><i style={{ width: pct + '%' }} /></div>

            {inFlight.length > 0 && (
              <div className="deals-batch-calling">
                <div className="deals-batch-calling-head">
                  <Icon name="sparkle" size={12} /> ĐANG GỌI AI
                </div>
                <div className="deals-batch-calling-list">
                  {inFlight.map(id => {
                    const it = list.find(d => d.id === id);
                    if (!it) return <span key={id} className="deals-batch-chip">{id}</span>;
                    const label = `${it.code || '#' + id} · ${it.customerName || ''}`.trim();
                    return <span key={id} className="deals-batch-chip" title={it.title || ''}>{label}</span>;
                  })}
                </div>
              </div>
            )}
          </div>
        );
      })()}

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
            Lần chấm gần nhất {new Date(board.generatedAt).toLocaleString('vi-VN')} · {board.deepScored} cơ hội đã chấm
          </div>
        )}

        <window.DataControls.KpiStrip items={[
          { icon: 'trend',   label: 'Tổng cơ hội',     value: c.total },
          { icon: 'star',    label: 'Win cao /đã chấm', value: c.cao, highlight: c.cao > 0 },
          { icon: 'sparkle', label: 'Win TB /đã chấm', value: c.tb },
          { icon: 'warning', label: 'Win thấp /đã chấm', value: c.thap },
          { icon: 'warning', label: 'Nguội /đã chấm',  value: c.nguoi },
        ]} />

        <DealFilters q={q} setQ={setQ} shown={filtered.length} total={items.length}
          advCount={advCount} advOpen={advOpen} onAdvToggle={() => setAdvOpen(o => !o)}
          rankFilter={rankFilter} setRankFilter={setRankFilter} />

        <window.SearchControls.AdvancedFilterPanel
          open={advOpen} onClose={() => setAdvOpen(false)}
          value={adv} defaultValue={EMPTY_DEAL_FILTER}
          onApply={(next) => { setAdv(next); setPage(1); loadList({ page: 1, adv: next }); }}>
          {({ draft, set }) => (
            <div className="cust-sheet-grid">
              <div className="cust-sheet-row">
                <label>Trạng thái</label>
                <window.SearchControls.SearchSelect
                  items={[{ id: 0, name: 'Tất cả trạng thái' }, ...(lookups.statuses || [])]}
                  value={draft.status ? (lookups.statuses || []).find(t => t.id === draft.status)?.name : 'Tất cả trạng thái'}
                  getKey={it => it.name} getLabel={it => it.name}
                  onChange={n => set('status', (lookups.statuses || []).find(x => x.name === n)?.id || 0)}
                  placeholder="Tất cả trạng thái" />
              </div>
              <div className="cust-sheet-row">
                <label>Nguồn</label>
                <window.SearchControls.SearchSelect
                  items={[{ id: 0, name: 'Tất cả nguồn' }, ...(lookups.sources || [])]}
                  value={draft.source ? (lookups.sources || []).find(t => t.id === draft.source)?.name : 'Tất cả nguồn'}
                  getKey={it => it.name} getLabel={it => it.name}
                  onChange={n => set('source', (lookups.sources || []).find(x => x.name === n)?.id || 0)}
                  placeholder="Tất cả nguồn" />
              </div>
              <div className="cust-sheet-row">
                <label>NV phụ trách</label>
                {/* SearchSelect (có ô tìm) thay <select> thường: NV nhiều → gõ tên tra nhanh, giống Khách hàng. */}
                <window.SearchControls.SearchSelect
                  items={[{ id: 0, name: 'Tất cả nhân viên' }, ...(lookups.staffs || [])]}
                  value={draft.staff ? (lookups.staffs || []).find(t => t.id === draft.staff)?.name : 'Tất cả nhân viên'}
                  getKey={it => it.name} getLabel={it => it.name}
                  onChange={n => set('staff', (lookups.staffs || []).find(x => x.name === n)?.id || 0)}
                  placeholder="Tất cả nhân viên" />
              </div>
              <div className="cust-sheet-row">
                <label>Khả năng thắng tối thiểu (%) <span style={{color:'var(--text-3)',fontSize:11}}>· upstream</span></label>
                <input type="number" className="tb-field" placeholder="vd 50 — ghi đè chip Win nhanh" value={draft.customMinRank}
                  onChange={e => set('customMinRank', e.target.value)} min={1} max={100} />
              </div>
              <div className="cust-sheet-row">
                <label>Khả năng thắng tối đa (%) <span style={{color:'var(--text-3)',fontSize:11}}>· upstream</span></label>
                <input type="number" className="tb-field" placeholder="vd 80 — bỏ trống = không giới hạn" value={draft.customMaxRank}
                  onChange={e => set('customMaxRank', e.target.value)} min={1} max={100} />
              </div>
              <div className="cust-sheet-row">
                <label>Tuổi cơ hội tối đa (ngày) <span style={{color:'var(--text-3)',fontSize:11}}>· toàn DB</span></label>
                <input type="number" className="tb-field" placeholder="vd 30" value={draft.maxAge}
                  onChange={e => set('maxAge', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row">
                <label>Giá trị tối thiểu (đ) <span style={{color:'var(--text-3)',fontSize:11}}>· toàn DB</span></label>
                <input type="number" className="tb-field" placeholder="vd 10000000" value={draft.minValue}
                  onChange={e => set('minValue', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row">
                <label>Giá trị tối đa (đ) <span style={{color:'var(--text-3)',fontSize:11}}>· toàn DB</span></label>
                <input type="number" className="tb-field" placeholder="bỏ trống = không giới hạn" value={draft.maxValue}
                  onChange={e => set('maxValue', e.target.value)} min={0} />
              </div>
              <div className="cust-sheet-row full">
                <label>Sắp xếp <span style={{color:'var(--text-3)',fontSize:11}}>· trên trang</span></label>
                <window.SearchControls.FilterChipRow>
                  {[['newest', 'Mới nhất (mặc định)'], ['priority', 'Ưu tiên AI'], ['ev', 'Doanh thu kỳ vọng'], ['value', 'Giá trị deal'], ['win', 'Khả năng thắng'], ['age', 'Tuổi cơ hội']].map(([v, l]) => (
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
          <window.TKTableScroll>
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
          </window.TKTableScroll>
        )}

        {/* Pagination — luôn hiện khi `total > 0`, KỂ CẢ đang loading (mờ + disable thay vì
            unmount). Trước đây `!listLoading &&` → upstream TourKit chậm 2-4s/trang là pagination
            biến mất suốt thời gian fetch → user tưởng "sang trang 2 mất phân trang". */}
        {total > 0 && (
          <window.TKPagination page={page} totalPages={totalPages} pageSize={pageSize}
            total={total} shown={filtered.length} loading={listLoading}
            onPage={setPage} onPageSize={setPageSize} />
        )}
      </>)}

      {sel && <DealDrawer
        item={sel}
        onClose={() => setSel(null)}
        pushToast={pushToast}
        onRescored={(updated) => {
          // Sync board local state (không cần reload full list): update deal item + swap board entry
          setSel(updated);
          setBoard(b => b ? { ...b, items: b.items.map(x => x.id === updated.id ? updated : x) } : b);
          setList(ls => ls.map(x => x.id === updated.id
            ? { ...x, winRate: updated.winRate, level: updated.level, scoreStatus: 'fresh',
                score: { winRate: updated.winRate, level: updated.level, signals: updated.analysis?.signals,
                         risks: updated.analysis?.risks, nextAction: updated.analysis?.nextAction,
                         reason: updated.analysis?.reason, priorityScore: updated.priorityScore,
                         expectedValue: updated.expectedValue, riskFlag: updated.riskFlag } }
            : x));
        }} />}

      {/* Confirm modal khi chấm THỦ CÔNG (đã chọn deal) — pattern Khách hàng.
          Auto mode skip modal (chấm thẳng). */}
      {confirmOpen && (() => {
        const selectedItems = items.filter(it => selectedIds.has(it.id));
        const cacheHit = selectedItems.filter(it => it._hasScore).length;
        const willCallAi = selectedItems.length - cacheHit;
        return (
          <div className="modal-backdrop" onClick={() => setConfirmOpen(false)}>
            <div className="dialog" onClick={e => e.stopPropagation()} style={{maxWidth: 480}}>
              <div className="dialog-head">
                <div className="dialog-head-icon"><Icon name="sparkle" size={16} /></div>
                <div style={{flex: 1, minWidth: 0}}>
                  <div className="dialog-eyebrow">XÁC NHẬN BATCH</div>
                  <h3 className="dialog-title">Chấm {selectedIds.size} cơ hội bằng AI?</h3>
                </div>
                <button className="icon-btn" onClick={() => setConfirmOpen(false)}><Icon name="close" size={18} /></button>
              </div>
              <div className="dialog-body">
                <div style={{display: 'grid', gap: 8, fontSize: 13}}>
                  <div className="deals-stat-row"><span>Tổng deal</span><b>{selectedIds.size}</b></div>
                  <div className="deals-stat-row"><span>Đã có score (cache hit)</span><b style={{color:'var(--text-3)'}}>{cacheHit}</b></div>
                  <div className="deals-stat-row"><span>Sẽ gọi AI</span><b style={{color:'var(--primary-dark)'}}>{willCallAi}</b></div>
                  <div className="deals-stat-row"><span>Ước tính thời gian</span><b>~{Math.max(5, Math.ceil(willCallAi * 6 / 6))} giây</b></div>
                  <div className="deals-stat-row"><span>Ước tính token</span><b>~{(willCallAi * 2.5).toFixed(1)}K tokens</b></div>
                </div>
              </div>
              <div className="dialog-foot">
                <button className="btn btn-ghost" onClick={() => setConfirmOpen(false)}>Hủy</button>
                <button className="btn btn-primary" onClick={() => { setConfirmOpen(false); run(); }}>
                  <Icon name="sparkle" size={14} /> Bắt đầu chấm
                </button>
              </div>
            </div>
          </div>
        );
      })()}
    </main>
  );
}

window.DealsPage = DealsPage;
