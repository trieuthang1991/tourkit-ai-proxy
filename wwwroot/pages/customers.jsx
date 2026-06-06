// pages/customers.jsx — Customer Review (AI) feature.
// 3 chế độ trên cùng 1 page:
//   - List view: bảng KH + filter + checkbox + nút "Review bằng AI"
//   - Confirm modal: hiện trước khi chạy batch (số KH, cache hit dự kiến, est cost)
//   - Progress view: SSE stream từ backend, mỗi KH xong update inline
// Click row đã review → mở review-card drawer.

const { useState: _uC, useEffect: _uEC, useMemo: _uMc } = React;

function CustomersPage({ pushToast }) {
  const [items, setItems]         = _uC([]);
  const [loading, setLoading]     = _uC(true);
  const [err, setErr]             = _uC(null);
  const [segFilter, setSegFilter] = _uC('all');
  const [search, setSearch]       = _uC('');
  const [selected, setSelected]   = _uC(new Set());
  const [confirmOpen, setConfirm] = _uC(false);
  const [job, setJob]             = _uC(null);            // {jobId, total, ...}
  const [progress, setProgress]   = _uC({ done: 0, errors: 0, cached: 0, total: 0, status: 'idle' });
  const [rowStatus, setRowStatus] = _uC({});              // customerId → {stage, rank, summaryLine, streamText, error}
  const [drawerId, setDrawerId]   = _uC(null);            // KH đang xem detail
  const [forceFresh, setForceFresh] = _uC(false);

  // Bộ lọc nâng cao (state shared cho AdvancedFilterSheet)
  const EMPTY_FILTER = {
    customerTypeId: 0, customerSourceId: 0, sellerId: 0,
    gender: '', startDate: '', endDate: '', sortOrder: '',
    birthdayThisMonth: false, careFilter: ''
  };
  const [filter, setFilter] = _uC(EMPTY_FILTER);
  const [sheetOpen, setSheetOpen] = _uC(false);
  const [lookups, setLookups] = _uC({ customerTypes: [], customerSources: [], sellers: [] });
  const activeFilterCount = ['customerTypeId','customerSourceId','sellerId','gender','startDate','endDate','sortOrder','careFilter']
    .filter(k => filter[k] && filter[k] !== 0).length + (filter.birthdayThisMonth ? 1 : 0);

  // Load lookups 1 lần — best-effort (lỗi thì để rỗng, vẫn render filter rỗng)
  _uEC(() => {
    (async () => {
      try {
        const r = await window.tourkitAuth.authedFetch('/api/v1/customers/lookups');
        if (r.ok) {
          const d = await r.json();
          setLookups({
            customerTypes:   d.customerTypes   || [],
            customerSources: d.customerSources || [],
            sellers:         d.sellers         || [],
          });
        }
      } catch { /* ignore */ }
    })();
  }, []);

  // Pipeline counts theo stage hiện tại (derived từ rowStatus, nhưng cache để render mượt).
  const [stageCounts, setStageCounts] = _uC({ queue: 0, preparing: 0, calling: 0, parsing: 0, done: 0, error: 0 });

  // Activity log: rolling buffer event stream, capped 200 entries cho perf.
  const [activityLog, setActivityLog] = _uC([]);
  const [logOpen, setLogOpen] = _uC(false);

  // Track 1 stream đang active để hiển thị live text (chọn KH mới nhất bắt đầu chunk).
  const [activeStream, setActiveStream] = _uC(null);   // {customerId, text}

  const loadList = async () => {
    setLoading(true); setErr(null);
    try {
      const q = new URLSearchParams();
      if (segFilter !== 'all') q.set('segment', segFilter);
      if (search.trim()) q.set('search', search.trim());
      if (filter.customerTypeId)   q.set('customerTypeId',   filter.customerTypeId);
      if (filter.customerSourceId) q.set('customerSourceId', filter.customerSourceId);
      if (filter.sellerId)         q.set('sellerId',         filter.sellerId);
      if (filter.gender)           q.set('gender',           filter.gender);
      if (filter.startDate)        q.set('startDate',        filter.startDate);
      if (filter.endDate)          q.set('endDate',          filter.endDate);
      if (filter.sortOrder)        q.set('sortOrder',        filter.sortOrder);
      if (filter.birthdayThisMonth) q.set('birthdayThisMonth', 'true');
      if (filter.careFilter)       q.set('careFilter',       filter.careFilter);
      const resp = await window.tourkitAuth.authedFetch('/api/v1/customers?' + q.toString());
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      setItems(await resp.json());
    } catch (e) {
      setErr(e.message);
    } finally {
      setLoading(false);
    }
  };

  _uEC(() => { loadList(); }, [segFilter, filter]);

  // Debounce search
  _uEC(() => {
    const t = setTimeout(loadList, 300);
    return () => clearTimeout(t);
  }, [search]);

  const toggleOne = (id) => {
    setSelected(s => {
      const n = new Set(s);
      n.has(id) ? n.delete(id) : n.add(id);
      return n;
    });
  };

  const toggleAll = () => {
    setSelected(s => s.size === items.length ? new Set() : new Set(items.map(x => x.id)));
  };

  const selectedItems = _uMc(() => items.filter(i => selected.has(i.id)), [items, selected]);
  const willCacheHit  = _uMc(() => selectedItems.filter(i => i.reviewStatus === 'fresh').length, [selectedItems]);
  const willCallAi    = _uMc(() => selectedItems.length - (forceFresh ? 0 : willCacheHit), [selectedItems, willCacheHit, forceFresh]);

  const startBatch = async () => {
    setConfirm(false);
    // Reset state cho lượt mới
    const initial = {};
    [...selected].forEach(id => { initial[id] = { stage: 'queue' }; });
    setRowStatus(initial);
    setStageCounts({ queue: selected.size, preparing: 0, calling: 0, parsing: 0, done: 0, error: 0 });
    setActivityLog([]);
    setActiveStream(null);

    try {
      const resp = await window.tourkitAuth.authedFetch('/api/v1/reviews/batch', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ customerIds: [...selected], forceFresh })
      });
      if (!resp.ok) throw new Error('HTTP ' + resp.status);
      const j = await resp.json();
      setJob(j);
      setProgress({ done: 0, errors: 0, cached: 0, total: j.total, status: 'processing' });
      consumeStream(j.streamUrl);
    } catch (e) {
      pushToast('Không tạo được batch: ' + e.message, 'warn');
    }
  };

  // Helper: log timeline entry
  const pushLog = (customerId, type, msg) => {
    const ts = new Date().toLocaleTimeString('vi-VN', { hour12: false });
    setActivityLog(l => {
      const next = [...l, { ts, customerId, type, msg }];
      return next.length > 200 ? next.slice(-200) : next;
    });
  };

  // Re-compute stageCounts cũng khi rowStatus đổi — derive thay vì track 2 nguồn.
  _uEC(() => {
    const counts = { queue: 0, preparing: 0, calling: 0, parsing: 0, done: 0, error: 0 };
    for (const id of Object.keys(rowStatus)) {
      const s = rowStatus[id]?.stage || 'queue';
      if (counts[s] !== undefined) counts[s]++;
      else counts.queue++;
    }
    setStageCounts(counts);
  }, [rowStatus]);

  // SSE consumer — đọc events từ backend, update progress + rowStatus.
  // Dùng fetch streaming (browser EventSource không support stop trigger qua POST đã start).
  const consumeStream = async (url) => {
    try {
      const resp = await window.tourkitAuth.authedFetch(url);
      if (!resp.ok || !resp.body) throw new Error('Stream lỗi ' + resp.status);
      const reader = resp.body.getReader();
      const decoder = new TextDecoder('utf-8');
      let buf = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        let idx;
        while ((idx = buf.indexOf('\n\n')) >= 0) {
          const evt = buf.slice(0, idx);
          buf = buf.slice(idx + 2);
          const line = evt.split('\n').find(l => l.startsWith('data:'));
          if (!line) continue;
          const payload = line.slice(5).trimStart();
          let obj;
          try { obj = JSON.parse(payload); } catch { continue; }
          handleStreamEvent(obj);
        }
      }
    } catch (e) {
      pushToast('Stream lỗi: ' + e.message, 'warn');
    }
  };

  const handleStreamEvent = (evt) => {
    const id = evt.customerId;

    if (evt.type === 'start') {
      setProgress(p => ({ ...p, total: evt.payload?.total || p.total }));
      pushLog(null, 'start', `Bắt đầu batch ${evt.payload?.total || 0} KH`);
      return;
    }

    // Lifecycle events: preparing / calling / chunk / parsing
    if (evt.type === 'preparing') {
      setRowStatus(s => ({ ...s, [id]: { ...s[id], stage: 'preparing', streamText: '' } }));
      pushLog(id, 'preparing', 'Đọc data + build prompt');
      return;
    }
    if (evt.type === 'calling') {
      setRowStatus(s => ({ ...s, [id]: { ...s[id], stage: 'calling', streamText: '' } }));
      setActiveStream({ customerId: id, text: '' });
      pushLog(id, 'calling', 'Đang gọi AI...');
      return;
    }
    if (evt.type === 'chunk') {
      const delta = evt.payload?.delta || '';
      setRowStatus(s => {
        const prev = s[id] || {};
        return { ...s, [id]: { ...prev, stage: 'calling', streamText: (prev.streamText || '') + delta } };
      });
      // Live stream preview: chỉ track 1 KH (KH đang được focus) — cập nhật text
      setActiveStream(a => a && a.customerId === id ? { customerId: id, text: a.text + delta } : a);
      return;
    }
    if (evt.type === 'parsing') {
      setRowStatus(s => ({ ...s, [id]: { ...s[id], stage: 'parsing' } }));
      pushLog(id, 'parsing', 'Đang parse JSON output');
      return;
    }

    // Terminal events
    if (evt.type === 'progress' || evt.type === 'cached') {
      setRowStatus(s => ({
        ...s,
        [id]: {
          stage: 'done',
          fromCache: evt.type === 'cached',
          rank: evt.payload?.rank,
          summaryLine: evt.payload?.summaryLine,
          alertLevel: evt.payload?.alertLevel
        }
      }));
      setProgress(p => ({
        ...p,
        done: evt.payload?.done ?? (p.done + 1),
        cached: evt.type === 'cached' ? (p.cached + 1) : p.cached
      }));
      setActiveStream(a => a && a.customerId === id ? null : a);
      pushLog(id, evt.type, evt.type === 'cached' ? `Cache hit · Hạng ${evt.payload?.rank}` : `Xong · Hạng ${evt.payload?.rank}`);
      return;
    }

    if (evt.type === 'error') {
      setRowStatus(s => ({ ...s, [id]: { ...s[id], stage: 'error', error: evt.error } }));
      setProgress(p => ({ ...p, errors: p.errors + 1 }));
      setActiveStream(a => a && a.customerId === id ? null : a);
      pushLog(id, 'error', evt.error || 'Lỗi không rõ');
      return;
    }

    if (evt.type === 'done' || evt.type === 'cancelled') {
      setProgress(p => ({ ...p, status: evt.type, ...evt.payload }));
      setJob(null);
      setActiveStream(null);
      if (evt.type === 'done') {
        pushLog(null, 'done', `Hoàn tất · ${evt.payload?.done} xong · ${evt.payload?.cached} cache · ${evt.payload?.errors} lỗi`);
        pushToast(`✓ Đã review xong ${evt.payload?.done || 0} KH`);
        loadList();
      } else {
        pushLog(null, 'cancelled', 'Người dùng huỷ batch');
        pushToast('Đã huỷ batch', 'warn');
      }
    }
  };

  const cancelBatch = async () => {
    if (!job) return;
    try { await window.tourkitAuth.authedFetch(job.cancelUrl, { method: 'POST' }); }
    catch {}
  };

  const busy = progress.status === 'processing';

  return (
    <main className="page" style={{padding: '18px 28px 60px', width: '100%'}}>
      <window.PageShell.PageHero
        icon="users"
        title="Khách hàng"
        badge="AI review"
        sub="Chấm hạng A–D + đề xuất hành động cho từng khách bằng AI."
        status={{ label: items.length > 0 ? `${items.length} KHÁCH HÀNG` : 'CHƯA CÓ DỮ LIỆU',
          detail: selected.size > 0 ? `${selected.size} đã chọn` : 'Chọn KH để review' }}
        actions={<>
          <button className="btn btn-ghost btn-sm" onClick={loadList} disabled={loading || busy}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          <button className="btn btn-primary btn-sm"
            disabled={selected.size === 0 || busy}
            onClick={() => setConfirm(true)}>
            <Icon name="sparkle" size={14} /> Review {selected.size > 0 ? `${selected.size} KH` : 'bằng AI'}
          </button>
        </>}
      />

      {/* KPI strip — tính client-side từ items hiện tại */}
      <window.DataControls.KpiStrip items={(() => {
        const total = items.length;
        const lastFirst = items.filter(c => c.totalTours === 1).length;
        const repeat = items.filter(c => c.totalTours >= 2).length;
        const reviewed = items.filter(c => c.reviewStatus && c.reviewStatus !== 'none').length;
        return [
          { icon: 'users',  label: 'Tổng',       value: total },
          { icon: 'star',   label: 'Lần đầu',    value: lastFirst },
          { icon: 'trend',  label: 'Mua lại',    value: repeat, highlight: true },
          { icon: 'sparkle', label: 'Đã review', value: reviewed },
        ];
      })()} />

      {/* Filter bar — SearchInput + chip phân khúc + chip sinh nhật + nút Bộ lọc */}
      <div className="cust-filter">
        <div className="cust-filter-search">
          <window.SearchControls.SearchInput value={search} onChange={setSearch}
            placeholder="Tìm tên / SĐT / email / ID khách hàng…" />
        </div>
        <window.SearchControls.FilterChipRow>
          {[{v:'all', l:'Tất cả'}, {v:'VIP', l:'VIP'}, {v:'Thường', l:'Thường'}, {v:'Mới', l:'Mới'}].map(o => (
            <window.SearchControls.FilterChip key={o.v} on={segFilter === o.v} onClick={() => setSegFilter(o.v)}>
              {o.l}
            </window.SearchControls.FilterChip>
          ))}
          <window.SearchControls.FilterChip on={filter.birthdayThisMonth}
            onClick={() => setFilter(f => ({ ...f, birthdayThisMonth: !f.birthdayThisMonth }))}>
            🎂 Sinh nhật
          </window.SearchControls.FilterChip>
        </window.SearchControls.FilterChipRow>
        <window.SearchControls.FilterButton count={activeFilterCount} open={sheetOpen}
          onClick={() => setSheetOpen(o => !o)} />
        <window.DataControls.StatRow shown={items.length} total={items.length} suffix="khách hàng" />
      </div>

      {/* AdvancedFilterSheet — draft state + footer Apply/Clear auto */}
      <window.SearchControls.AdvancedFilterPanel
        open={sheetOpen} onClose={() => setSheetOpen(false)}
        value={filter} defaultValue={EMPTY_FILTER}
        onApply={setFilter}>
        {({ draft, set }) => (
          <div className="cust-sheet-grid">
            <div className="cust-sheet-row">
              <label>Loại khách hàng</label>
              <window.SearchControls.SearchSelect
                items={[{ id: 0, name: 'Tất cả loại' }, ...lookups.customerTypes]}
                value={draft.customerTypeId ? lookups.customerTypes.find(t => t.id === draft.customerTypeId)?.name : 'Tất cả loại'}
                getKey={it => it.name} getLabel={it => it.name}
                onChange={n => set('customerTypeId', lookups.customerTypes.find(x => x.name === n)?.id || 0)}
                placeholder="Tất cả loại" />
            </div>
            <div className="cust-sheet-row">
              <label>Nguồn khách hàng</label>
              <window.SearchControls.SearchSelect
                items={[{ id: 0, name: 'Tất cả nguồn' }, ...lookups.customerSources]}
                value={draft.customerSourceId ? lookups.customerSources.find(t => t.id === draft.customerSourceId)?.name : 'Tất cả nguồn'}
                getKey={it => it.name} getLabel={it => it.name}
                onChange={n => set('customerSourceId', lookups.customerSources.find(x => x.name === n)?.id || 0)}
                placeholder="Tất cả nguồn" />
            </div>
            <div className="cust-sheet-row">
              <label>NV phụ trách</label>
              <window.SearchControls.SearchSelect
                items={[{ id: 0, name: 'Tất cả nhân viên' }, ...lookups.sellers]}
                value={draft.sellerId ? lookups.sellers.find(t => t.id === draft.sellerId)?.name : 'Tất cả nhân viên'}
                getKey={it => it.name} getLabel={it => it.name}
                onChange={n => set('sellerId', lookups.sellers.find(x => x.name === n)?.id || 0)}
                placeholder="Tất cả nhân viên" />
            </div>
            <div className="cust-sheet-row">
              <label>Ngày tạo</label>
              <window.DataControls.DateRange
                from={draft.startDate} to={draft.endDate}
                onFrom={v => set('startDate', v)} onTo={v => set('endDate', v)} />
            </div>
            <div className="cust-sheet-row">
              <label>Giới tính</label>
              <window.SearchControls.FilterChipRow>
                {[['', 'Tất cả'], ['M', 'Nam'], ['F', 'Nữ']].map(([v, l]) => (
                  <window.SearchControls.FilterChip key={v || 'any'} on={draft.gender === v}
                    onClick={() => set('gender', v)}>{l}</window.SearchControls.FilterChip>
                ))}
              </window.SearchControls.FilterChipRow>
            </div>
            <div className="cust-sheet-row full">
              <label>Sắp xếp</label>
              <window.SearchControls.FilterChipRow>
                {[['', 'Mới nhất'], ['totalRevenue', 'Doanh thu cao'], ['totalTours', 'Số tour nhiều'], ['fullName', 'Tên A-Z']].map(([v, l]) => (
                  <window.SearchControls.FilterChip key={v || 'newest'} on={draft.sortOrder === v}
                    onClick={() => set('sortOrder', v)}>{l}</window.SearchControls.FilterChip>
                ))}
              </window.SearchControls.FilterChipRow>
            </div>
          </div>
        )}
      </window.SearchControls.AdvancedFilterPanel>

      {/* Progress section khi đang chạy: pipeline + bar + live stream + log */}
      {busy && (
        <div style={{padding: 14, background: '#f0f9ff', border: '1px solid #bae6fd', borderRadius: 10, marginBottom: 16}}>
          {/* Pipeline diagram: 5 stage chip với count */}
          <div style={{display: 'flex', alignItems: 'center', gap: 6, marginBottom: 12, flexWrap: 'wrap'}}>
            <StageChip icon="📥" label="Chờ"    count={stageCounts.queue}     color="var(--text-3)" />
            <Arrow />
            <StageChip icon="📋" label="Đọc DL" count={stageCounts.preparing} color="#6366f1" />
            <Arrow />
            <StageChip icon="🤖" label="AI"     count={stageCounts.calling}   color="#f59e0b" pulse />
            <Arrow />
            <StageChip icon="📝" label="Parse"  count={stageCounts.parsing}   color="#8b5cf6" />
            <Arrow />
            <StageChip icon="✅" label="Xong"   count={stageCounts.done}      color="#16a34a" />
            {stageCounts.error > 0 && (
              <>
                <span style={{color: 'var(--text-3)', margin: '0 6px'}}>·</span>
                <StageChip icon="✗" label="Lỗi" count={stageCounts.error} color="#dc2626" />
              </>
            )}
            <div style={{flex: 1}} />
            <button className="btn btn-ghost btn-sm" onClick={() => setLogOpen(o => !o)}>
              {logOpen ? '↑ Ẩn log' : `↓ Hiện log (${activityLog.length})`}
            </button>
            <button className="btn btn-ghost btn-sm" onClick={cancelBatch}>Dừng</button>
          </div>

          {/* Progress bar */}
          <div>
            <div style={{fontSize: 12, color: 'var(--text-2)', marginBottom: 4, display: 'flex', justifyContent: 'space-between'}}>
              <span>{progress.done}/{progress.total} KH xong{progress.errors > 0 && ` · ${progress.errors} lỗi`}{progress.cached > 0 && ` · ${progress.cached} cache`}</span>
              <span>{progress.total ? Math.round((progress.done / progress.total) * 100) : 0}%</span>
            </div>
            <div style={{height: 6, background: '#e0f2fe', borderRadius: 3, overflow: 'hidden'}}>
              <div style={{
                height: '100%', background: 'var(--accent)',
                width: progress.total ? `${(progress.done / progress.total) * 100}%` : '0%',
                transition: 'width 0.3s'
              }} />
            </div>
          </div>

          {/* Hiện list KH đang "calling AI" — không có text preview vì backend buffered
              để tránh reasoning content lẫn vào JSON output (DeepSeek/Kimi). */}
          {Object.values(rowStatus).some(s => s?.stage === 'calling') && (
            <div style={{marginTop: 12, padding: 10, background: 'white', borderRadius: 8, border: '1px solid #fef3c7'}}>
              <div style={{fontSize: 10, fontWeight: 700, color: '#92400e', letterSpacing: '0.08em', textTransform: 'uppercase', marginBottom: 6}}>
                🤖 Đang gọi AI
              </div>
              <div style={{display: 'flex', flexWrap: 'wrap', gap: 6}}>
                {Object.entries(rowStatus)
                  .filter(([_, s]) => s?.stage === 'calling')
                  .map(([id, _]) => {
                    const it = items.find(x => x.id === id);
                    return (
                      <span key={id} style={{
                        fontSize: 11, padding: '4px 10px', borderRadius: 999,
                        background: '#fef3c7', color: '#92400e', fontWeight: 500,
                        animation: 'pulse 1.2s ease-in-out infinite'
                      }}>
                        {id}{it ? ` · ${it.name}` : ''}
                      </span>
                    );
                  })}
              </div>
            </div>
          )}

          {/* Activity log */}
          {logOpen && (
            <div style={{marginTop: 12, padding: 10, background: '#0f172a', color: 'var(--border-strong)', borderRadius: 8, fontFamily: 'ui-monospace, "SF Mono", monospace', fontSize: 11, lineHeight: 1.6, maxHeight: 220, overflowY: 'auto'}}>
              {activityLog.length === 0 ? <em style={{opacity: 0.6}}>Chưa có event...</em> :
                activityLog.slice().reverse().map((e, i) => (
                  <div key={i} style={{display: 'flex', gap: 8}}>
                    <span style={{color: 'var(--text-3)'}}>{e.ts}</span>
                    {e.customerId && <span style={{color: '#fbbf24', fontWeight: 600}}>{e.customerId}</span>}
                    <span style={{color: stageLogColor(e.type)}}>[{e.type}]</span>
                    <span style={{flex: 1}}>{e.msg}</span>
                  </div>
                ))
              }
            </div>
          )}
        </div>
      )}

      {err && (
        <div style={{padding: 12, background: '#fef2f2', color: '#991b1b', borderRadius: 8, marginBottom: 12}}>
          Lỗi: {err}
        </div>
      )}

      {loading ? (
        <div style={{padding: 60, textAlign: 'center', color: 'var(--text-3)'}}>Đang tải...</div>
      ) : items.length === 0 ? (
        <div style={{padding: 60, textAlign: 'center', color: 'var(--text-3)', background: '#fafafa', borderRadius: 12}}>
          Không có khách hàng nào khớp bộ lọc.
        </div>
      ) : (
        <div style={{background: 'white', border: '1px solid var(--border)', borderRadius: 10, overflow: 'hidden'}}>
          <table style={{width: '100%', borderCollapse: 'collapse', fontSize: 13}}>
            <thead style={{background: 'var(--bg)'}}>
              <tr style={{textAlign: 'left'}}>
                <th style={th(40)}>
                  <input type="checkbox"
                    checked={items.length > 0 && selected.size === items.length}
                    onChange={toggleAll} />
                </th>
                <th style={th()}>Khách hàng</th>
                <th style={th(100)}>Phân khúc</th>
                <th style={th(70)}>Hạng AI</th>
                <th style={th(120)}>Tổng chi</th>
                <th style={th(80)}>Đơn cuối</th>
                <th style={th(200)}>Trạng thái review</th>
                <th style={th(80)}></th>
              </tr>
            </thead>
            <tbody>
              {items.map(it => {
                const live = rowStatus[it.id];
                const effRank = live?.rank || it.rank;
                const effSummary = live?.summaryLine || it.summaryLine;
                // Row status:
                //   - Khi batch đang chạy: live.stage = queue/preparing/calling/parsing/done/error
                //   - Khi không batch: dùng it.reviewStatus từ server (none/fresh/stale)
                const status = live?.stage || it.reviewStatus;
                return (
                  <tr key={it.id} style={{borderTop: '1px solid var(--border)', cursor: 'pointer'}}
                      onClick={() => effRank && setDrawerId(it.id)}>
                    <td style={td()} onClick={e => e.stopPropagation()}>
                      <input type="checkbox" checked={selected.has(it.id)} onChange={() => toggleOne(it.id)} />
                    </td>
                    <td style={td()}>
                      <div style={{fontWeight: 600}}>{it.name}</div>
                      <div style={{color: 'var(--text-3)', fontSize: 11}}>{it.id}</div>
                    </td>
                    <td style={td()}>
                      <SegBadge segment={it.segment} />
                    </td>
                    <td style={td()}>{effRank ? <RankBadge rank={effRank} /> : <span style={{color: 'var(--text-3)'}}>—</span>}</td>
                    <td style={td()}>{fmtVND(it.totalSpent)}</td>
                    <td style={td()}>{it.lastPurchaseDaysAgo == null ? '—' : `${it.lastPurchaseDaysAgo}d`}</td>
                    <td style={td()}><StatusCell status={status} summaryLine={effSummary} error={live?.error} ageHours={it.reviewAgeHours} /></td>
                    <td style={td()}>
                      {effRank && (
                        <button className="btn btn-ghost btn-sm" onClick={e => { e.stopPropagation(); setDrawerId(it.id); }}>
                          Xem
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {/* Confirm modal */}
      {confirmOpen && (
        <div className="modal-backdrop" onClick={() => setConfirm(false)}>
          <div className="dialog" onClick={e => e.stopPropagation()} style={{maxWidth: 480}}>
            <div className="dialog-head">
              <div className="dialog-head-icon"><Icon name="sparkle" size={16} /></div>
              <div style={{flex: 1, minWidth: 0}}>
                <div className="dialog-eyebrow">XÁC NHẬN BATCH</div>
                <h3 className="dialog-title">Review {selected.size} khách hàng bằng AI?</h3>
              </div>
              <button className="icon-btn" onClick={() => setConfirm(false)}><Icon name="close" size={18} /></button>
            </div>
            <div className="dialog-body">
              <div style={{display: 'grid', gap: 8, fontSize: 13}}>
                <Stat label="Tổng KH" value={selected.size} />
                <Stat label="Cache hit (skip AI)" value={forceFresh ? 0 : willCacheHit} muted={forceFresh} />
                <Stat label="Sẽ gọi AI" value={willCallAi} highlight />
                <Stat label="Ước tính thời gian" value={`~${Math.max(3, Math.ceil(willCallAi * 4 / 10))} giây`} />
                <Stat label="Ước tính token" value={`~${(willCallAi * 1.8).toFixed(1)}K tokens`} />
              </div>
              <label style={{display: 'flex', alignItems: 'center', gap: 8, marginTop: 14, fontSize: 13, cursor: 'pointer'}}>
                <input type="checkbox" checked={forceFresh} onChange={e => setForceFresh(e.target.checked)} />
                Bỏ qua cache, gọi AI cho TẤT CẢ
              </label>
            </div>
            <div className="dialog-foot">
              <button className="btn btn-ghost" onClick={() => setConfirm(false)}>Hủy</button>
              <button className="btn btn-primary" onClick={startBatch}>
                <Icon name="sparkle" size={14} /> Bắt đầu review
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Detail drawer */}
      {drawerId && window.CustomerReviewDrawer && (
        <window.CustomerReviewDrawer
          customerId={drawerId}
          onClose={() => setDrawerId(null)}
          onRefreshed={() => loadList()}
          pushToast={pushToast} />
      )}
    </main>
  );
}

// ─── Small helper components ──────────────────────────────────────────────────
const th = (w) => ({ padding: '10px 12px', fontWeight: 700, fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--text-3)', width: w });
const td = () => ({ padding: '12px', verticalAlign: 'middle' });

function SegBadge({ segment }) {
  const styles = {
    'VIP':     { bg: 'linear-gradient(135deg, #f59e0b, #ea580c)', fg: 'white' },
    'Thường':  { bg: '#e0e7ff', fg: '#3730a3' },
    'Mới':     { bg: '#dcfce7', fg: '#166534' }
  };
  const s = styles[segment] || { bg: 'var(--bg)', fg: 'var(--text-2)' };
  return <span style={{fontSize: 11, fontWeight: 700, padding: '3px 8px', borderRadius: 4, background: s.bg, color: s.fg}}>{segment}</span>;
}

function RankBadge({ rank }) {
  const colors = { A: '#16a34a', B: '#2563eb', C: '#f59e0b', D: '#dc2626' };
  return (
    <span style={{
      display: 'inline-block', width: 26, height: 26, lineHeight: '26px', textAlign: 'center',
      borderRadius: '50%', background: colors[rank] || 'var(--text-3)', color: 'white', fontWeight: 700, fontSize: 12
    }}>{rank}</span>
  );
}

// Map stage → icon + label. Dùng cho cả live batch states và static review states.
const STAGE_VIEW = {
  queue:      { icon: '⏳', label: 'Đang chờ',     color: 'var(--text-3)' },
  preparing:  { icon: '📋', label: 'Đọc dữ liệu',  color: '#6366f1', spin: true },
  calling:    { icon: '🤖', label: 'Đang gọi AI',  color: '#f59e0b', spin: true },
  parsing:    { icon: '📝', label: 'Đang parse',   color: '#8b5cf6', spin: true },
  done:       { icon: '✓',  label: 'Xong',         color: '#16a34a' },
  error:      { icon: '✗',  label: 'Lỗi',          color: '#dc2626' },
  fresh:      { icon: '✓',  label: 'Đã review',    color: '#16a34a' },
  stale:      { icon: '↻',  label: 'Cần cập nhật', color: '#d97706' },
  none:       { icon: '—',  label: 'Chưa review',  color: 'var(--text-3)' }
};

function StatusCell({ status, summaryLine, error, ageHours }) {
  const view = STAGE_VIEW[status] || STAGE_VIEW.none;

  if (status === 'error') {
    return <span style={{color: view.color, fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 6}}>
      <span>{view.icon}</span> {error || 'Lỗi'}
    </span>;
  }

  // Đang chạy batch
  if (['queue', 'preparing', 'calling', 'parsing'].includes(status)) {
    return <span style={{color: view.color, fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 6, fontWeight: 600}}>
      <span style={view.spin ? {animation: 'pulse 1s ease-in-out infinite'} : null}>{view.icon}</span>
      {view.label}
    </span>;
  }

  // Đã xong / cached / fresh
  if (status === 'done' || status === 'fresh') {
    return <span style={{fontSize: 12, color: 'var(--text-2)', display: 'inline-flex', alignItems: 'center', gap: 6}}>
      <span style={{color: view.color}}>{view.icon}</span>
      <span title={summaryLine}>{summaryLine ? summaryLine.slice(0, 56) + (summaryLine.length > 56 ? '…' : '') : (ageHours != null ? `${ageHours}h trước` : 'Đã review')}</span>
    </span>;
  }

  if (status === 'stale') {
    return <span style={{fontSize: 12, color: view.color, fontWeight: 500}}>
      {view.icon} {view.label}
    </span>;
  }

  return <span style={{fontSize: 12, color: view.color}}>{view.label}</span>;
}

// ─── Pipeline diagram helpers ─────────────────────────────────────────────────
function StageChip({ icon, label, count, color, pulse }) {
  const active = count > 0;
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center', gap: 6,
      padding: '4px 10px', borderRadius: 999,
      background: active ? `${color}22` : '#f1f5f9',
      border: `1px solid ${active ? color : 'var(--border)'}`,
      color: active ? color : 'var(--text-3)',
      fontSize: 11, fontWeight: 600,
      animation: (pulse && active) ? 'pulse 1.5s ease-in-out infinite' : null
    }}>
      <span style={{fontSize: 13}}>{icon}</span>
      <span>{label}</span>
      <span style={{
        background: active ? color : 'var(--text-3)', color: 'white',
        padding: '1px 6px', borderRadius: 8, fontSize: 10, fontWeight: 700, minWidth: 18, textAlign: 'center'
      }}>{count}</span>
    </div>
  );
}
function Arrow() {
  return <span style={{color: 'var(--border-strong)', fontSize: 11}}>→</span>;
}
function stageLogColor(type) {
  return {
    start: 'var(--border-strong)', preparing: '#a5b4fc', calling: '#fbbf24',
    chunk: 'var(--text-3)', parsing: '#c4b5fd', progress: '#86efac',
    cached: '#7dd3fc', error: '#fca5a5', done: '#86efac', cancelled: '#fbbf24'
  }[type] || 'var(--border-strong)';
}

function Stat({ label, value, highlight, muted }) {
  return (
    <div style={{display: 'flex', justifyContent: 'space-between', padding: '8px 10px', background: highlight ? '#fef3c7' : 'var(--bg)', borderRadius: 6, opacity: muted ? 0.5 : 1}}>
      <span style={{color: 'var(--text-3)'}}>{label}</span>
      <span style={{fontWeight: 700, color: highlight ? '#92400e' : 'var(--text-1)'}}>{value}</span>
    </div>
  );
}

window.CustomersPage = CustomersPage;
