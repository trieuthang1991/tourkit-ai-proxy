// pages/flow-preview.jsx — BẢN XEM THỬ sơ đồ luồng (React Flow).
//
// Mục đích: cho thấy hình hài "flow builder" trước khi quyết có đầu tư làm thật.
// Đây CHỈ là hình minh hoạ cho luồng "Bản tin sáng" — KHÔNG có bộ thông dịch phía sau,
// KHÔNG lưu, KHÔNG nối lại được. Kéo/thu phóng được để cảm nhận, thay đổi mất khi tải lại.
//
// Vì sao nói rõ như vậy ngay trên giao diện: trang widget-admin từng dựng kiểu "quản lý
// nhiều widget" trong khi backend chỉ cho 1/tenant → người dùng bấm nhầm mất dữ liệu.
// Giao diện KHÔNG được hứa khả năng mà hệ thống không có.

const { useState: _fpS, useEffect: _fpE, useMemo: _fpM } = React;

// ─── Dữ liệu luồng: LẤY TỪ SỔ ĐĂNG KÝ ─────────────────────────────────────────
// Mỗi sơ đồ nằm ở 1 file riêng trong wwwroot/flows/ và tự đăng ký vào window.tourkitFlows.
// Trang này KHÔNG giữ dữ liệu sơ đồ nữa — thêm luồng mới không phải sửa file này.
// Xem hướng dẫn thêm sơ đồ ở đầu wwwroot/flows/_registry.js.
const FP_DEMO_TYPE = '_demo-sale-brief';

const FP_LEGEND = [
  { cls: 'trigger', label: 'Điểm bắt đầu', desc: 'Cái gì kích hoạt luồng — lịch, sự kiện, thao tác tay' },
  { cls: 'step',    label: 'Bước xử lý',   desc: 'Lấy dữ liệu, gọi AI, soạn nội dung' },
  { cls: 'branch',  label: 'Rẽ nhánh',     desc: 'Điều kiện — đi tiếp hướng nào' },
  { cls: 'send',    label: 'Gửi đi',       desc: 'Đưa kết quả tới người nhận' },
];

// Bộ ô nhập + schema DÙNG CHUNG với trang Tự động hoá (components/workflow-options.jsx).
// KHÔNG tự dựng bộ form thứ hai — 2 bộ cho cùng 1 cấu hình là kiểu lỗi âm thầm khó tìm.
const _fpWO = () => window.tourkitWorkflowOptions;
// Kiểu field cần chiếm trọn 1 hàng (nhãn trên, ô nhập dưới) — GIỮ ĐỒNG BỘ với
// biến wideTypes trong pages/workflows.jsx.
const FP_WIDE_TYPES = ['select', 'multi', 'numbers'];

async function _fpApi(path, opts = {}) {
  const r = await window.tourkitAuth.authedFetch(path, {
    ...opts, headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
  });
  if (!r.ok) {
    let msg = `HTTP ${r.status}`;
    try { const j = await r.json(); msg = j.error || msg; } catch {}
    throw new Error(msg);
  }
  return r.json();
}

// Bảng cấu hình của 1 node. CHỈ hiện các option thuộc node đó (data.cfg), dựng bằng
// OptionControl dùng chung → luôn khớp trang Tự động hoá, không có bộ form thứ hai.
// '@interval' là khoá đặc biệt: tần suất chạy (cột riêng trong DB, không nằm trong OptionsJson).
function NodeConfigPanel({ node, type, onClose, enabled, setEnabled, interval, setIntervalMin,
                           options, setOptions, dynOptions, dynLoading, loaded, neverSet }) {
  const WO = _fpWO();
  const schema = WO.WORKFLOW_OPTIONS[type] || [];
  const keys = node.data.cfg || [];
  // Giữ thứ tự như schema gốc + chỉ hiện option đang thoả showIf (giống trang Tự động hoá).
  const opts = schema.filter(o => keys.includes(o.key) && WO.optVisible(o, options));
  const hasInterval = keys.includes('@interval');

  return (
    <aside className="fp-panel">
      <div className="fp-panel-head">
        <span className="fp-panel-ic"><Icon name={node.data.icon} size={14} /></span>
        <div className="fp-panel-title">
          <b>{node.data.title}</b>
          <i>{node.data.sub}</i>
        </div>
        <button className="fp-panel-x" onClick={onClose} aria-label="Đóng">×</button>
      </div>

      {!loaded ? (
        <div className="fp-panel-empty">Đang tải cấu hình…</div>
      ) : (
        <div className="fp-panel-body">
          {neverSet && (
            <div className="fp-panel-new">
              Tác vụ này <b>chưa từng bật</b>. Các giá trị dưới đây là mặc định — sửa rồi bấm
              <b> Lưu cấu hình</b> là nó được tạo và bắt đầu chạy theo tần suất đã chọn.
            </div>
          )}
          {/* Markup GIỮ ĐÚNG cấu trúc của trang Tự động hoá: nhãn nằm trong .workflows-opt-row,
              phần gợi ý là .workflows-opt-hint RIÊNG bên dưới. Nếu nhét gợi ý vào trong
              .workflows-opt-label thì nó ăn font-weight 600 → cả khối đậm, không phân biệt được
              nhãn với chú thích (đúng lỗi đã gặp). */}
          {hasInterval && (
            <>
              <div className="workflows-opt is-toggle">
                <div className="workflows-opt-row">
                  <label className="workflows-opt-label">Bật tác vụ</label>
                  <div className="workflows-opt-control">
                    <div className="workflows-toggle-wrap">
                      <label className="workflows-toggle">
                        <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />
                        <span className="workflows-toggle-track" />
                      </label>
                      <span className="workflows-toggle-label">{enabled ? 'Bật' : 'Tắt'}</span>
                    </div>
                  </div>
                </div>
                <div className="workflows-opt-hint">Tắt thì toàn bộ luồng dưới đây không chạy.</div>
              </div>
              <div className="workflows-opt">
                <div className="workflows-opt-row">
                  <label className="workflows-opt-label">Tần suất chạy</label>
                  <div className="workflows-opt-control">
                    <select className="workflows-select workflows-opt-input" value={interval}
                      onChange={e => setIntervalMin(Number(e.target.value))}>
                      {WO.INTERVAL_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                  </div>
                </div>
                <div className="workflows-opt-hint">
                  Đếm từ lần chạy trước, chưa neo được vào giờ cố định trong ngày.
                </div>
              </div>
            </>
          )}

          {/* is-wide cho select/multi/numbers — giống trang Tự động hoá: nhãn và ô nhập nằm
              cùng một dòng, chỉ rớt xuống khi hết chỗ. is-multi dành cho dải chip tĩnh và
              cụm nhiều ô số: chúng tự xuống dòng bên trong nên luôn chiếm trọn một dòng. */}
          {opts.map(opt => (
            <div key={opt.key} className={'workflows-opt'
              + (opt.type === 'bool' ? ' is-toggle' : '')
              + (FP_WIDE_TYPES.includes(opt.type) ? ' is-wide' : '')
              + (['multi', 'numbers'].includes(opt.type) ? ' is-multi' : '')}>
              <div className="workflows-opt-row">
                <label className="workflows-opt-label">
                  {opt.label}{opt.required && <span className="req-star">*</span>}
                </label>
                <div className="workflows-opt-control">
                  <WO.OptionControl opt={opt} options={options} setOptions={setOptions}
                    dynOptions={dynOptions} dynLoading={dynLoading} />
                </div>
              </div>
              {opt.hint && <div className="workflows-opt-hint">{opt.hint}</div>}
            </div>
          ))}

          {!hasInterval && opts.length === 0 && (
            <div className="fp-panel-empty">
              Các tuỳ chọn của bước này đang bị ẩn — bật công tắc chính ở bước trước thì chúng sẽ hiện.
            </div>
          )}
        </div>
      )}
    </aside>
  );
}

function FlowPreviewPage({ pushToast, type }) {
  const [rf, setRf] = _fpS(null);       // module React Flow sau khi nạp
  const [err, setErr] = _fpS(null);

  // type = mã workflow (từ nút "Xem sơ đồ" ở trang Tự động hoá).
  // KHÔNG có type → luồng bản tin sáng (minh hoạ).
  // CÓ type nhưng CHƯA vẽ sơ đồ (vd tour-price-catalog-sync) → phải nói thẳng là chưa có,
  // TUYỆT ĐỐI không rơi về bản tin sáng: người dùng bấm "Xem sơ đồ" ở tác vụ đồng bộ giá tour
  // mà hiện ra sơ đồ bản tin sáng thì đó là giao diện nói dối.
  const FL = window.tourkitFlows;
  const flow = type ? FL.get(type) : FL.get(FP_DEMO_TYPE);
  const missingDiagram = !!type && !flow;
  const isDemo = !!(flow && flow.demo);
  const nodes = flow ? flow.nodes : [];
  const edges = flow ? flow.edges : [];
  const title = (flow && flow.label) || 'Bản tin sáng';
  // Memo hoá: mảng cạnh phải GIỮ NGUYÊN tham chiếu giữa các lần render, nếu không
  // React Flow coi là dữ liệu mới và dựng lại (giật khi đang kéo).
  const animatedEdges = _fpM(() => edges.map(e => ({ ...e, animated: true })), [type]);

  // ── Cấu hình THẬT của workflow (chỉ khi mở sơ đồ 1 workflow cụ thể) ──────────
  const [wf, setWf] = _fpS(null);            // bản ghi từ GET /workflows
  const [enabled, setEnabled] = _fpS(false);
  const [interval, setIntervalMin] = _fpS(15);
  const [options, setOptions] = _fpS({});
  const [dynOptions, setDynOptions] = _fpS({});
  const [dynLoading, setDynLoading] = _fpS({});
  const [saving, setSaving] = _fpS(false);
  const [pick, setPick] = _fpS(null);        // node đang chọn để cấu hình
  const [dirty, setDirty] = _fpS(false);
  const [neverSet, setNeverSet] = _fpS(false);   // chưa từng bật/chạy lần nào

  // LƯU TỪNG PHẦN: ghi lại ĐÚNG những gì user đã chạm vào. Lưu chỉ đụng các mục đó,
  // phần còn lại lấy từ bản mới nhất trên máy chủ ngay lúc lưu.
  // Vì sao cần: bảng cấu hình chỉ hiện vài mục của 1 node, nhưng trước đây khi lưu lại gửi
  // TOÀN BỘ cấu hình đang giữ trong trang. Nếu người khác vừa đổi mục khác (hoặc trang mở
  // đã lâu, dữ liệu cũ) thì lần lưu này ghi đè mất thay đổi của họ.
  const [touched, setTouched] = _fpS(() => new Set());
  const loadedRef = React.useRef(false);

  const setEnabledT = v => { setTouched(s => new Set(s).add('@enabled')); setDirty(true); setEnabled(v); };
  const setIntervalT = v => { setTouched(s => new Set(s).add('@interval')); setDirty(true); setIntervalMin(v); };
  // OptionControl gọi setOptions(prev => ...) → tự so sánh để biết khoá nào vừa đổi.
  const setOptionsT = updater => {
    const next = typeof updater === 'function' ? updater(options) : updater;
    const changed = Object.keys(next).filter(
      k => JSON.stringify(next[k]) !== JSON.stringify(options[k]));
    if (changed.length) {
      setTouched(s => { const n = new Set(s); changed.forEach(k => n.add(k)); return n; });
      setDirty(true);
    }
    setOptions(next);
  };

  _fpE(() => {
    let alive = true;
    window.ensureReactFlow()
      .then(m => { if (alive) setRf(m); })
      .catch(e => { if (alive) { setErr(e.message); pushToast && pushToast(e.message, 'error'); } });
    return () => { alive = false; };
  }, []);

  // Nạp (hoặc nạp lại) cấu hình từ máy chủ. Dùng cho cả lần mở trang lẫn nút "Huỷ".
  // `alive` để bỏ qua kết quả về muộn sau khi component đã rời màn.
  function loadConfig(alive = () => true) {
    return _fpApi('/api/v1/workflows')
      .then(d => {
        if (!alive()) return;
        // Backend merge sẵn catalog nên tác vụ CHƯA cấu hình vẫn có trong danh sách
        // (enabled=false + interval mặc định). Nếu vì lý do nào đó không thấy, KHÔNG được
        // im lặng bỏ qua — trước đây `return` ở đây khiến bảng đứng mãi ở "Đang tải cấu hình…".
        // Rơi về mặc định để vẫn sửa + lưu được (PUT là upsert, tự tạo bản ghi).
        const row = (d.items || []).find(x => x.type === type) || null;
        setWf(row || { type, label: flow.label, enabled: false, intervalMinutes: 15, options: {} });
        setEnabled(!!(row && row.enabled));
        setIntervalMin((row && row.intervalMinutes) || 15);
        setOptions({ ..._fpWO().optionDefaults(type), ...((row && row.options) || {}) });
        setNeverSet(!row || (!row.enabled && !row.lastRunUtc));
        setTouched(new Set());
        setDirty(false);
        loadedRef.current = true;
      })
      .catch(e => { if (alive()) pushToast && pushToast('Không tải được cấu hình: ' + e.message, 'error'); });
  }

  _fpE(() => {
    if (isDemo || !flow) return;   // sơ đồ minh hoạ: không có cấu hình để nạp
    let alive = true;
    loadedRef.current = false;
    loadConfig(() => alive);
    return () => { alive = false; };
  }, [type]);

  // Huỷ: chỉ nạp lại cấu hình bằng ajax rồi đóng bảng — KHÔNG tải lại trang.
  // Tải lại trang vừa chậm (dev mode phải biên dịch lại toàn bộ .jsx) vừa mất vị trí
  // node đang kéo, mà chẳng để làm gì vì dữ liệu chỉ nằm ở một lời gọi API.
  function handleCancel() {
    setPick(null);
    loadConfig();
  }

  // Danh sách trạng thái deal (options động) — giống trang Tự động hoá.
  _fpE(() => {
    if (type !== 'deal-auto-review') return;
    let alive = true;
    setDynLoading(l => ({ ...l, dealStatuses: true }));
    _fpApi('/api/v1/workflows/deal-statuses')
      .then(d => { if (alive) setDynOptions(o => ({ ...o, dealStatuses: d.items || [] })); })
      .catch(() => {})
      .finally(() => { if (alive) setDynLoading(l => ({ ...l, dealStatuses: false })); });
    return () => { alive = false; };
  }, [type]);

  async function handleSave() {
    const WO = _fpWO();
    const schema = WO.WORKFLOW_OPTIONS[type] || [];
    const missing = schema.filter(o => o.required && WO.optVisible(o, options) && WO.optEmpty(o, options, dynOptions));
    if (missing.length) {
      pushToast('Vui lòng chọn/điền: ' + missing.map(o => o.label).join(', '), 'error');
      return;
    }
    setSaving(true);
    try {
      // Lấy bản MỚI NHẤT ngay trước khi ghi → những mục mình không chạm vào giữ đúng giá trị
      // hiện hành trên máy chủ, không phải giá trị lúc mở trang.
      const fresh = ((await _fpApi('/api/v1/workflows')).items || []).find(x => x.type === type) || {};
      const optKeys = [...touched].filter(k => k !== '@enabled' && k !== '@interval');

      const body = {
        enabled:         touched.has('@enabled')  ? enabled  : !!fresh.enabled,
        intervalMinutes: touched.has('@interval') ? interval : (fresh.intervalMinutes || 15),
        // Không sửa option nào → gửi null, backend COALESCE giữ nguyên OptionsJson cũ.
        // Có sửa → nền là options mới nhất của máy chủ, chỉ đè đúng khoá đã chạm.
        options: optKeys.length === 0 ? null : (() => {
          const merged = { ...(fresh.options || {}) };
          optKeys.forEach(k => { merged[k] = options[k]; });
          return merged;
        })(),
      };

      await _fpApi(`/api/v1/workflows/${type}`, { method: 'PUT', body: JSON.stringify(body) });
      pushToast(`Đã lưu cấu hình "${flow.label}"`);
      setTouched(new Set());
      setDirty(false);
    } catch (e) {
      pushToast('Lưu cấu hình thất bại: ' + e.message, 'error');
    } finally {
      setSaving(false);
    }
  }

  // nodeTypes phải tạo SAU khi có module (cần Handle/Position) và chỉ tạo 1 lần —
  // đổi tham chiếu mỗi lần render sẽ khiến React Flow dựng lại toàn bộ node.
  const nodeTypes = _fpM(() => {
    if (!rf) return null;
    const { Handle, Position } = rf;

    // Luồng chạy DỌC → chấm nối ở trên (nhận) và dưới (phát), không phải trái/phải.
    // Node có `cfg` = có cấu hình sửa được → viền nhấn + nhãn "Cấu hình".
    const make = kind => ({ data, id }) => (
      <div className={'fp-node fp-' + kind + (data.cfg ? ' has-cfg' : '')}>
        {kind !== 'trigger' && <Handle type="target" position={Position.Top} />}
        <span className="fp-node-ic"><Icon name={data.icon} size={15} /></span>
        <span className="fp-node-txt">
          <b>{data.title}</b>
          <i>{data.sub}</i>
        </span>
        {data.cfg && <span className="fp-node-cfg"><Icon name="sliders" size={12} /></span>}
        <Handle type="source" position={Position.Bottom} />
      </div>
    );

    return { fpTrigger: make('trigger'), fpStep: make('step'), fpBranch: make('branch'), fpSend: make('send') };
  }, [rf]);

  if (missingDiagram) return (
    <main className="page wga fp-page">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">Tự động hoá · Sơ đồ hoạt động</div>
          <h1>Chưa có sơ đồ cho tác vụ này</h1>
          <p className="wga-sub">
            Tác vụ <b>{type}</b> đang chạy thật, nhưng sơ đồ các bước của nó chưa được vẽ.
          </p>
        </div>
        <a className="wga-btn" href="#/workflows">← Về Tự động hoá</a>
      </div>
      <div className="fp-warn">
        <span>
          Sơ đồ được vẽ tay theo mã nguồn nên phải bổ sung từng tác vụ một. Trang này cố tình
          <b> không hiển thị sơ đồ của tác vụ khác</b> để bạn khỏi nhầm. Mọi cấu hình của tác vụ
          này vẫn sửa bình thường ở trang <a href="#/workflows">Tự động hoá</a>.
        </span>
      </div>
    </main>
  );

  return (
    <main className="page wga fp-page">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">
            {isDemo ? 'Xem thử · Chưa chạy được' : 'Tự động hoá · Sơ đồ hoạt động'}
          </div>
          <h1>Sơ đồ luồng — {title}</h1>
          <p className="wga-sub">
            {isDemo
              ? 'Hình dung cách một luồng tự động sẽ được dựng bằng thao tác kéo thả, thay vì mô tả bằng chữ.'
              : 'Các bước tác vụ này thực sự chạy khi tới lượt. Xem để biết bật lên thì điều gì xảy ra.'}
          </p>
        </div>
        {/* Sơ đồ thiết kế: cho chuyển qua lại giữa các bản (giám đốc / nhân viên bán hàng)
            — nếu không thì chỉ vào được bằng cách gõ URL. */}
        {isDemo && (
          <div className="fp-demo-switch">
            {FL.all().filter(f => f.demo).map(f => (
              <a key={f.type} href={'#/flow-preview/' + f.type}
                 className={'wga-btn' + (f.type === (type || FP_DEMO_TYPE) ? ' primary' : '')}>
                {f.label.replace('Bản tin sáng — ', '')}
              </a>
            ))}
          </div>
        )}
        {!isDemo && (
          <a className="wga-btn" href="#/workflows">← Về Tự động hoá</a>
        )}
      </div>

      <div className={'fp-warn' + (isDemo ? '' : ' fp-warn-info')}>
        {!isDemo ? (
          <span>
            <b>Sơ đồ này mô tả tác vụ đang chạy thật</b> — vẽ theo đúng mã nguồn. Bấm vào ô có dấu{' '}
            <b>⚙</b> để sửa cấu hình ngay tại đó; lưu là áp dụng thật, giống hệt trang Tự động hoá.
            Riêng <b>hình dạng luồng thì cố định</b> — các bước do mã nguồn quyết định, không nối lại
            được. {flow.note}
          </span>
        ) : (
          <span>
            <b>Đây là bản xem thử, chưa chạy được.</b> Sơ đồ dưới đây vẽ sẵn để minh hoạ — bạn kéo và
            thu phóng được để cảm nhận, nhưng <b>không nối lại được và không lưu</b>. Tải lại trang là
            về như cũ. Bản tin sáng thật hiện chưa được xây; nếu làm, nó sẽ chạy bằng cấu hình chứ chưa
            phải bằng sơ đồ này.
          </span>
        )}
      </div>

      <div className={'fp-work' + (!isDemo && pick ? ' with-panel' : '')}>
        <div className="fp-canvas">
          {err ? (
            <div className="fp-state fp-state-err">Không tải được thư viện sơ đồ: {err}</div>
          ) : !rf ? (
            <div className="fp-state">Đang tải sơ đồ…</div>
          ) : (
            <rf.ReactFlow
              key={type || 'demo'}                      /* đổi workflow → dựng lại + fitView cho khớp sơ đồ mới */
              /* KHÔNG kiểm soát (defaultNodes/defaultEdges) — React Flow tự giữ vị trí node.
                 Trước dùng nodes={...} (chế độ có kiểm soát) mà KHÔNG có onNodesChange → thư viện
                 không ghi được vị trí mới, kéo bị giật/nảy về; thêm nữa edges.map() tạo mảng mới mỗi
                 lần render (mở panel, sửa option, dirty…) làm nó đồng bộ lại ngay giữa lúc đang kéo.
                 Ở đây vị trí node chỉ để xem nên không cần giữ trong state của trang. */
              defaultNodes={nodes}
              defaultEdges={animatedEdges}
              nodeTypes={nodeTypes}
              fitView
              fitViewOptions={{ padding: 0.18 }}
              minZoom={0.4}
              maxZoom={1.6}
              proOptions={{ hideAttribution: false }}   /* React Flow MIT — giữ credit của họ */
              nodesConnectable={false}                  /* hình dạng luồng do mã nguồn quyết → không cho nối lại */
              edgesFocusable={false}
              deleteKeyCode={null}                      /* chặn xoá bằng phím Delete */
              onNodeClick={(e, n) => { if (!isDemo && n.data && n.data.cfg) setPick(n); }}
            >
              <rf.Background gap={18} size={1} />
              <rf.Controls showInteractive={false} />
            </rf.ReactFlow>
          )}
        </div>

        {!isDemo && pick && <NodeConfigPanel
          node={pick} type={type} onClose={() => setPick(null)} neverSet={neverSet}
          enabled={enabled} setEnabled={setEnabledT}
          interval={interval} setIntervalMin={setIntervalT}
          options={options} setOptions={setOptionsT}
          dynOptions={dynOptions} dynLoading={dynLoading} loaded={!!wf} />}
      </div>

      {!isDemo && dirty && (
        <div className="fp-savebar">
          <span>Có thay đổi chưa lưu</span>
          <button className="wga-btn" onClick={handleCancel} disabled={saving}>Huỷ</button>
          <button className="wga-btn primary" onClick={handleSave} disabled={saving}>
            {saving ? 'Đang lưu…' : 'Lưu cấu hình'}
          </button>
        </div>
      )}

      <div className="fp-legend">
        {FP_LEGEND.map(l => (
          <div key={l.cls} className="fp-legend-item">
            <span className={'fp-legend-dot fp-' + l.cls} />
            <span>
              <b>{l.label}</b>
              <i>{l.desc}</i>
            </span>
          </div>
        ))}
      </div>
    </main>
  );
}

window.FlowPreviewPage = FlowPreviewPage;
