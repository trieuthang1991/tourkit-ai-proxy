// pages/assistant.jsx — Trợ lý số liệu (Chat-Analytics).
// Bố cục 2 cột: TRÁI = chat, PHẢI = bảng/thẻ số liệu.
// Luồng: user chat → POST /api/v1/chat → backend tự chọn API TourKit lấy số liệu + AI phân tích.
// Đăng nhập: token mã hóa (Crypton) → POST /api/v1/login-token → sessionId (lưu localStorage).

const { useState: _aS, useEffect: _aE, useRef: _aR } = React;

const TK_SESSION_KEY = 'tourkit_tk_session';
const TK_USER_KEY = 'tourkit_tk_user';   // {fullName, companyName, tenantId} cho header app dùng chung

// money-ish column → format VND ở bảng (mirror ChatAgentService.IsMoney bên backend).
const _MONEY_HINTS = ['doanhthu','revenue','tongtien','thanhtien','thanhtoan','amount','money',
  'gia','price','tien','commission','hoahong','loinhuan','profit','congno','debt','paid','total','tong','payment','value',
  'expense','cost','chiphi'];
const _NOT_MONEY = ['count','qty','row','soluong','index','page','year','month','stt'];
const _isMoneyKey = (k) => {
  const s = String(k).toLowerCase();
  if (_NOT_MONEY.some(n => s.includes(n))) return false;
  return _MONEY_HINTS.some(h => s.includes(h));
};

// Rút gọn số tiền lớn cho thẻ KPI: 34.098.423.853 → "34,1 tỷ".
function _vndShort(n) {
  const a = Math.abs(n);
  if (a >= 1e9) return (n / 1e9).toFixed(2).replace(/\.?0+$/, '').replace('.', ',') + ' tỷ';
  if (a >= 1e6) return (n / 1e6).toFixed(1).replace(/\.?0+$/, '').replace('.', ',') + ' tr';
  return window.fmtVND(n);
}

// Nhãn cột tiếng Việt (key viết thường) — tránh hiển thị tên field tiếng Anh.
const _COL_VI = {
  label: 'Mục', name: 'Tên', title: 'Tên', count: 'Số lượng',
  revenue: 'Doanh thu', expense: 'Chi phí', profit: 'Lợi nhuận', money: 'Số tiền', amount: 'Số tiền',
  tourcode: 'Mã tour', tourname: 'Tên tour', tourguide: 'HDV',
  fullname: 'Họ tên', customername: 'Khách hàng', customerphone: 'SĐT', phonenumber: 'SĐT', phone: 'SĐT',
  email: 'Email', address: 'Địa chỉ', gender: 'Giới tính', birthday: 'Ngày sinh',
  totaltour: 'Số tour', totaltours: 'Số tour', totalpayment: 'Tổng chi tiêu', totalcustomers: 'Số khách',
  status: 'Trạng thái', statusname: 'Trạng thái', typeschedulename: 'Loại hẹn', typeof: 'Loại',
  caretitle: 'Nội dung hẹn', careendtime: 'Thời gian hẹn', carestarttime: 'Bắt đầu', timecareremind: 'Nhắc hẹn',
  staffname: 'Nhân viên', nguoitao: 'Người tạo', insdttm: 'Ngày tạo', createddate: 'Ngày tạo',
  startdate: 'Bắt đầu', enddate: 'Kết thúc', departuredate: 'Khởi hành', bookingdate: 'Ngày đặt', closedate: 'Đóng chỗ',
  pricepers: 'Giá/khách', pricepersolot: 'Giá/khách', pricepers_lot: 'Giá/khách', priceperslot: 'Giá/khách',
  tourprice: 'Giá tour', pricechild: 'Giá trẻ em', cusremaining: 'Còn chỗ', numerofslots: 'Tổng chỗ',
  placepickup: 'Điểm đón', placepickdown: 'Điểm trả', referencecode: 'Mã tham chiếu',
  commissionadult: 'Hoa hồng NL', code: 'Mã', note: 'Ghi chú', branch: 'Chi nhánh',
  customercode: 'Mã KH', customertype: 'Loại KH', customertypename: 'Loại KH',
  customersource: 'Nguồn KH', customersourcename: 'Nguồn KH', customergroupname: 'Nhóm KH',
  city: 'Tỉnh/TP', dob: 'Ngày sinh', tags: 'Thẻ', voucherno: 'Số phiếu', vouchertype: 'Loại phiếu',
  paymentmethod: 'Hình thức TT', reason: 'Lý do', guests: 'Số khách', departure: 'Khởi hành',
  status: 'Trạng thái', statustext: 'Trạng thái', available: 'Còn chỗ', booked: 'Đã đặt', slots: 'Tổng chỗ', onhold: 'Giữ chỗ',
  customerphone: 'SĐT', sellername: 'Phụ trách', mayeucau: 'Mã yêu cầu', actualrevenue: 'Thực thu',
  totalexpense: 'Tổng chi phí', avgstar: 'Đánh giá', parentid: 'Thuộc nhóm',
  tourtype: 'Loại', departuredate: 'Khởi hành', enddate: 'Kết thúc', percentage: 'Tỉ lệ', count: 'Số lượng',
  rank: 'Hạng', fullname: 'Họ tên', phone: 'SĐT', totalrevenue: 'Tổng chi tiêu', totaltours: 'Số tour',
};
// rút hậu tố twin (revenueFormatted→revenue, statusText→status…) để tra nhãn gốc
const _COL_SUFFIX = /(formatted|text|label|name)$/;

// Cột nên hiển thị theo NGỮ CẢNH (loại dữ liệu). Tránh đổ hết field thô (vd hỏi tour mà hiện Khách/SĐT/Seller).
const _KIND_COLS = {
  tours:     ['tourCode', 'title', 'departureDateFormatted', 'statusText', 'available', 'revenueFormatted', 'totalExpenseFormatted'],
  cashflow:  ['label', 'revenueFormatted', 'expenseFormatted', 'profitFormatted'],
  marketing: ['label', 'count', 'percentageFormatted'],
  markets:   ['name'],
};
const _prettify = (k) => {
  const s = String(k).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').trim();
  return s.charAt(0).toUpperCase() + s.slice(1);
};
const _colLabel = (k) => {
  const low = String(k).toLowerCase();
  if (_COL_VI[low]) return _COL_VI[low];
  const base = low.replace(_COL_SUFFIX, '');
  if (base !== low && _COL_VI[base]) return _COL_VI[base];
  return _prettify(String(k).replace(/(Formatted|Text|Label|Name)$/, ''));
};

// Ẩn cột Id (id, customerId, marketId, insUid…) để không lộ thông tin nội bộ.
const _isIdKey = (k) => /^id$/i.test(k) || /Id$/.test(k) || /UID$/i.test(k) || /^uid$/i.test(k) || /^stt$/i.test(k);

const _fmtCell = (key, val) => {
  if (val == null) return '';
  if (typeof val === 'object') return JSON.stringify(val);
  if (typeof val === 'number' && _isMoneyKey(key)) return window.fmtVND(val);
  if (typeof val === 'number') return window.fmtNum(val);
  return String(val);
};

// Tìm mảng "rows" để render bảng từ data.raw (array, hoặc object có 1 property mảng).
function _extractRows(raw) {
  if (Array.isArray(raw)) return raw;
  if (raw && typeof raw === 'object') {
    for (const k of Object.keys(raw)) {
      const v = raw[k];
      if (Array.isArray(v) && v.length && typeof v[0] === 'object') return v;
    }
  }
  return null;
}

// Phát hiện dữ liệu vẽ biểu đồ: mỗi dòng có 1 nhãn (string) + ≥1 cột số.
const _LABEL_KEYS = ['label','name','thang','month','ngay','day','tuan','week','title','period','ky','nguon','source'];
const _SERIES_VI = { revenue:'Doanh thu', expense:'Chi phí', profit:'Lợi nhuận', count:'Số lượng', total:'Tổng', amount:'Giá trị', payment:'Thanh toán' };
const _seriesName = (k) => _SERIES_VI[String(k).toLowerCase()] || k;

function _looksTimeline(rows, labelKey) {
  const vals = rows.slice(0, 8).map(r => String(r[labelKey] || ''));
  const datey = vals.filter(v => /\d/.test(v) && (/[\/\-]/.test(v) || /th[aá]ng/i.test(v) || /^t\d/i.test(v) || /\d{4}/.test(v) || /^\d{1,2}$/.test(v))).length;
  return datey >= Math.ceil(vals.length / 2);
}

function _chartInfo(rows) {
  if (!rows || rows.length < 2 || rows.length > 31) return null;
  const first = rows[0];
  if (!first || typeof first !== 'object') return null;
  const keys = Object.keys(first);
  const labelKey = keys.find(k => _LABEL_KEYS.includes(k.toLowerCase()) && typeof first[k] === 'string')
                || keys.find(k => typeof first[k] === 'string');
  if (!labelKey) return null;
  // cột số (bỏ id + bỏ % phụ trợ). >4 cột số → là danh sách bản ghi → KHÔNG vẽ chart.
  let numKeys = keys.filter(k => k !== labelKey && typeof first[k] === 'number' && !_isIdKey(k) && !/percent/i.test(k));
  if (!numKeys.length || numKeys.length > 4) return null;
  numKeys = numKeys.sort((a, b) => (_isMoneyKey(b) ? 1 : 0) - (_isMoneyKey(a) ? 1 : 0)).slice(0, 4);
  return { labelKey, series: numKeys.map(k => ({ key: k })), timeline: _looksTimeline(rows, labelKey) };
}

// Bảng màu cho từng lát biểu đồ tròn.
const _SLICE_COLORS = ['#F97316', '#0EA5E9', '#10B981', '#8B5CF6', '#F59E0B', '#EF4444', '#14B8A6', '#6366F1', '#EC4899', '#84CC16'];

// Biểu đồ chuyên nghiệp bằng Chart.js (nạp qua CDN ở index.html).
function ChartView({ rows, info, focus }) {
  const canvasRef = React.useRef(null);
  const chartRef = React.useRef(null);
  const allKeys = info.series.map(s => s.key);
  const focusSel = (focus || []).filter(f => allKeys.includes(f));
  const [sel, setSel] = React.useState(focusSel.length ? focusSel : allKeys.slice(0, info.timeline ? 3 : 1));
  const [chartType, setChartType] = React.useState('bar');   // 'bar' | 'doughnut'

  const activeCount = info.series.filter(s => sel.includes(s.key)).length;
  // Tròn hợp lý: dữ liệu phân loại (không timeline), đúng 1 chỉ số, 2–10 mục.
  const canPie = !info.timeline && rows.length >= 2 && rows.length <= 10;

  React.useEffect(() => {
    if (!window.Chart || !canvasRef.current) return;
    const active = info.series.filter(s => sel.includes(s.key));
    if (!active.length) return;

    let data = rows.slice();
    if (!info.timeline) {                       // dữ liệu phân loại → sắp xếp giảm dần
      const k = active[0].key;
      data = data.sort((a, b) => (Number(b[k]) || 0) - (Number(a[k]) || 0));
    }
    const labels = data.map(r => String(r[info.labelKey]));
    const moneyActive = active.some(s => _isMoneyKey(s.key));
    const fmt = (v) => (moneyActive ? window.fmtVND(v) : window.fmtNum(v));

    const usePie = chartType === 'doughnut' && canPie && active.length === 1;

    let cfg;
    if (usePie) {
      const key = active[0].key;
      const vals = data.map(r => Number(r[key]) || 0);
      const total = vals.reduce((a, b) => a + b, 0) || 1;
      cfg = {
        type: 'doughnut',
        data: { labels, datasets: [{ data: vals, backgroundColor: labels.map((_, i) => _SLICE_COLORS[i % _SLICE_COLORS.length]), borderColor: '#fff', borderWidth: 2 }] },
        options: {
          responsive: true, maintainAspectRatio: false, cutout: '58%',
          plugins: {
            legend: { position: 'right', labels: { font: { family: 'Be Vietnam Pro', size: 11 }, boxWidth: 12, padding: 8 } },
            tooltip: { callbacks: { label: (ctx) => ` ${ctx.label}: ${fmt(ctx.parsed)} (${(ctx.parsed / total * 100).toFixed(1)}%)` } }
          }
        }
      };
    } else {
      const horizontal = !info.timeline;
      const datasets = active.map((s, i) => ({
        label: _seriesName(s.key),
        data: data.map(r => Number(r[s.key]) || 0),
        backgroundColor: _SLICE_COLORS[i % _SLICE_COLORS.length],
        borderRadius: 5,
        maxBarThickness: 48
      }));
      cfg = {
        type: 'bar',
        data: { labels, datasets },
        options: {
          indexAxis: horizontal ? 'y' : 'x',
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: datasets.length > 1, position: 'bottom', labels: { font: { family: 'Be Vietnam Pro' }, boxWidth: 12 } },
            tooltip: { callbacks: { label: (ctx) => ` ${ctx.dataset.label}: ${fmt(ctx.parsed[horizontal ? 'x' : 'y'])}` } }
          },
          scales: horizontal
            ? { x: { ticks: { callback: (v) => fmt(v), font: { size: 10 } }, grid: { color: '#EEF2F6' } },
                y: { ticks: { font: { size: 11 } }, grid: { display: false } } }
            : { y: { ticks: { callback: (v) => fmt(v), font: { size: 10 } }, grid: { color: '#EEF2F6' } },
                x: { ticks: { font: { size: 10 }, maxRotation: 0, autoSkip: true }, grid: { display: false } } }
        }
      };
    }

    if (chartRef.current) chartRef.current.destroy();
    chartRef.current = new window.Chart(canvasRef.current, cfg);

    return () => { if (chartRef.current) { chartRef.current.destroy(); chartRef.current = null; } };
  }, [rows, info, sel, chartType, canPie]);

  const toggle = (k) => setSel(prev =>
    prev.includes(k) ? (prev.length > 1 ? prev.filter(x => x !== k) : prev) : [...prev, k]);

  return (
    <div className="asst-chart">
      {(info.series.length > 1 || (canPie && activeCount === 1)) && (
        <div className="asst-chart-toolbar">
          {info.series.length > 1 ? (
            <div className="asst-metrics">
              {info.series.map(s => (
                <button key={s.key} className={'asst-mchip' + (sel.includes(s.key) ? ' on' : '')} onClick={() => toggle(s.key)}>
                  {_seriesName(s.key)}
                </button>
              ))}
            </div>
          ) : <span />}
          {canPie && activeCount === 1 && (
            <div className="asst-type-toggle">
              <button className={chartType === 'bar' ? 'on' : ''} onClick={() => setChartType('bar')}>Cột</button>
              <button className={chartType === 'doughnut' ? 'on' : ''} onClick={() => setChartType('doughnut')}>Tròn</button>
            </div>
          )}
        </div>
      )}
      <div className="asst-canvas-wrap"><canvas ref={canvasRef} /></div>
    </div>
  );
}

// Chấm nhảy + nhãn giai đoạn khi đang chờ/stream.
function TypingDots({ stage }) {
  const label = { planning: 'Đang hiểu câu hỏi', fetching: 'Đang lấy số liệu', analyzing: 'Đang phân tích' }[stage] || 'Đang xử lý';
  return (
    <span className="asst-typing">
      {label}<span className="asst-dots"><i /><i /><i /></span>
    </span>
  );
}

function DataPanel({ data, onAsk }) {
  // Memo hóa rows/chart theo `data` (ổn định suốt lúc stream) → KHÔNG dựng lại chart mỗi token
  // (trước đây tính lại mỗi render → ChartView destroy+create liên tục → nhấp nháy khó chịu).
  const rows = React.useMemo(() => _extractRows(data ? data.raw : null), [data]);
  const isKpi = !!data && data.kind === 'kpi';   // financial-summary: items đã thành thẻ → không bảng/chart trùng
  const chart = React.useMemo(() => (data && !isKpi) ? _chartInfo(rows) : null, [data, isKpi, rows]);

  if (!data) {
    return (
      <div className="asst-panel-empty">
        <div className="asst-empty-icon"><Icon name="paper" size={26} /></div>
        <p className="asst-empty-title">Số liệu sẽ hiển thị ở đây</p>
        <p className="asst-hint">Ví dụ: “Doanh thu tháng này”, “Top khách hàng”, “Tour sắp khởi hành”.</p>
      </div>
    );
  }

  let columns = [];
  if (rows && rows.length) {
    // map lowercased→tên key thật có trong dữ liệu
    const keyMap = {};
    for (const r of rows.slice(0, 20))
      if (r && typeof r === 'object')
        for (const k of Object.keys(r)) if (!(k.toLowerCase() in keyMap)) keyMap[k.toLowerCase()] = k;

    // Ưu tiên cột theo NGỮ CẢNH (loại dữ liệu); chỉ giữ cột thực sự có trong dữ liệu.
    const pref = _KIND_COLS[data.kind];
    if (pref) columns = pref.map(p => keyMap[p.toLowerCase()]).filter(Boolean);

    // Fallback (kind lạ): field đầu, bỏ Id + bỏ cột thô nếu có twin hiển thị (*Formatted/*Text/*Label/*Name).
    if (!columns.length)
      columns = Object.values(keyMap).filter(k => {
        if (_isIdKey(k)) return false;
        const low = k.toLowerCase();
        if (_COL_SUFFIX.test(low)) return true;
        return !(keyMap[low + 'formatted'] || keyMap[low + 'text'] || keyMap[low + 'label'] || keyMap[low + 'name']);
      }).slice(0, 8);

    // Nếu user hỏi 1 chỉ số cụ thể (focus) → bảng chỉ giữ nhãn + chỉ số đó.
    if (data.focus && data.focus.length && chart) {
      const keep = new Set([chart.labelKey, ...data.focus]);
      const filtered = columns.filter(c => keep.has(c));
      if (filtered.length > 1) columns = filtered;
    }
  }

  return (
    <div className="asst-data">
      {data.stats && data.stats.length > 0 && (() => {
        const GROUP_VI = { revenue: 'Doanh thu', expense: 'Chi phí', profit: 'Lợi nhuận', other: 'Khác' };
        const ORDER = ['revenue', 'expense', 'profit', 'other'];
        const hasGroups = data.stats.some(s => s.group);
        const card = (s, i) => (
          <div key={i} className="asst-stat">
            <div className="asst-stat-val" title={s.unit === 'đ' ? window.fmtVND(s.value) : window.fmtNum(s.value)}>
              {s.unit === 'đ' ? _vndShort(s.value) : window.fmtNum(s.value)}
            </div>
            <div className="asst-stat-label">{s.label}</div>
          </div>
        );
        if (!hasGroups) return <div className="asst-stats">{data.stats.map(card)}</div>;
        // Gom nhóm rõ ràng: Doanh thu / Chi phí / Lợi nhuận
        return (
          <div className="asst-stat-groups">
            {ORDER.filter(g => data.stats.some(s => (s.group || 'other') === g)).map(g => (
              <div key={g} className={'asst-stat-group grp-' + g}>
                <div className="asst-group-label">{GROUP_VI[g]}</div>
                <div className="asst-stats">{data.stats.filter(s => (s.group || 'other') === g).map(card)}</div>
              </div>
            ))}
          </div>
        );
      })()}

      {chart && window.Chart && (
        <div className="asst-block">
          <div className="label">Biểu đồ</div>
          <ChartView rows={rows} info={chart} focus={data.focus} />
        </div>
      )}

      {!isKpi && (rows && rows.length > 0 ? (
        <div className="asst-block">
          <div className="label">Bảng dữ liệu</div>
          <div className="asst-table-wrap">
            <table className="asst-table">
              <thead>
                <tr>{columns.map(c => <th key={c}>{_colLabel(c)}</th>)}</tr>
              </thead>
              <tbody>
                {rows.slice(0, 50).map((r, ri) => (
                  <tr key={ri}>{columns.map(c => <td key={c}>{_fmtCell(c, r[c])}</td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
          {rows.length > 50 && <div className="asst-hint">Hiển thị 50/{rows.length} dòng đầu.</div>}
        </div>
      ) : (
        <details className="asst-raw">
          <summary>Xem dữ liệu gốc</summary>
          <pre className="asst-json">{JSON.stringify(data.raw, null, 2)}</pre>
        </details>
      ))}

      {data.suggestions && data.suggestions.length > 0 && onAsk && (
        <div className="asst-suggest-next">
          <div className="label">Xem tiếp</div>
          <div className="asst-suggest-grid">
            {data.suggestions.map(q => (
              <button key={q} className="asst-chip" onClick={() => onAsk(q)}>
                <Icon name="sparkle" size={12} /> <span>{q}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function AssistantPage({ pushToast }) {
  // Auth toàn cục (core/auth.jsx) — trang này chỉ render khi đã đăng nhập (app.jsx gate).
  const sessionId = window.tourkitAuth.getSessionId();
  const sessionInfo = window.tourkitAuth.getUser();
  const logout = () => window.tourkitAuth.logout();
  const [clearing, setClearing] = _aS(false);

  async function clearCache() {
    if (clearing) return;
    setClearing(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/chat/cache/clear', { method: 'POST' });
      const d = await r.json().catch(() => ({}));
      if (r.ok) pushToast(`Đã xóa cache (${d.cleared || 0} mục) — hỏi lại sẽ lấy số liệu mới`);
      else pushToast(d.error || 'Xóa cache lỗi', 'error');
    } catch (e) { pushToast('Xóa cache lỗi: ' + e.message, 'error'); }
    finally { setClearing(false); }
  }

  const [messages, setMessages] = _aS([]);          // {role, content}
  const [input, setInput] = _aS('');
  const [loading, setLoading] = _aS(false);
  const [stage, setStage] = _aS(null);   // 'planning'|'fetching'|'analyzing' khi đang stream
  const [panelData, setPanelData] = _aS(null);
  const scrollRef = _aR(null);

  _aE(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages, loading]);

  async function send(textArg) {
    const text = (typeof textArg === 'string' ? textArg : input).trim();
    if (!text || loading || !sessionId) return;
    const next = [...messages, { role: 'user', content: text }];
    const asstIdx = next.length;   // vị trí message assistant sẽ thêm
    setMessages([...next, { role: 'assistant', content: '', tool: null, streaming: true }]);
    setInput('');
    setLoading(true);
    setStage('planning');

    // cập nhật message assistant tại asstIdx
    const patch = (fn) => setMessages(m => { const c = [...m]; if (c[asstIdx]) c[asstIdx] = fn(c[asstIdx]); return c; });

    const cfg = (window.tourkit && window.tourkit.ai && window.tourkit.ai.getConfig)
      ? window.tourkit.ai.getConfig() : {};
    try {
      const resp = await fetch('/api/v1/chat/stream', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream', 'X-Session-Id': sessionId },
        body: JSON.stringify({
          messages: next, provider: cfg.provider, model: cfg.model,
          apiKey: (window.tourkit.ai.getKey && cfg.provider) ? window.tourkit.ai.getKey(cfg.provider) : undefined
        })
      });
      if (resp.status === 401) { pushToast('Phiên hết hạn — đăng nhập lại', 'error'); logout(); return; }
      if (!resp.ok || !resp.body) {
        const t = await resp.text().catch(() => '');
        throw new Error(t.slice(0, 200) || ('HTTP ' + resp.status));
      }

      const reader = resp.body.getReader();
      const dec = new TextDecoder('utf-8');
      let buf = '';
      let dataSet = false;   // panel data set 1 lần (stage analyzing) → done KHÔNG set lại → chart không vẽ lại
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        let i;
        while ((i = buf.indexOf('\n\n')) >= 0) {
          const evt = buf.slice(0, i); buf = buf.slice(i + 2);
          const line = evt.split('\n').find(l => l.startsWith('data:'));
          if (!line) continue;
          const payload = line.slice(5).trim();
          if (!payload) continue;
          let o; try { o = JSON.parse(payload); } catch { continue; }

          if (o.error) { patch(a => ({ ...a, content: '⚠️ ' + o.error, error: true, streaming: false })); setStage(null); continue; }
          if (o.stage) { setStage(o.stage); if (o.data) { setPanelData(o.data); dataSet = true; } if (o.tool) patch(a => ({ ...a, tool: o.tool })); continue; }
          if (o.delta) { setStage(null); patch(a => ({ ...a, content: a.content + o.delta })); continue; }
          if (o.done) {
            if (o.reply) patch(a => ({ ...a, content: o.reply }));
            if (o.toolName) patch(a => ({ ...a, tool: o.toolName }));
            if (o.data && !dataSet) setPanelData(o.data);   // chỉ set nếu chưa có (luồng cache-hit)
          }

        }
      }
    } catch (e) {
      patch(a => ({ ...a, content: '⚠️ ' + e.message, error: true }));
    } finally {
      patch(a => ({ ...a, streaming: false }));
      setLoading(false);
      setStage(null);
    }
  }

  // ─── 2 cột (luôn đã đăng nhập nhờ gate toàn cục) ─────────────────────────────
  const suggestions = ['Doanh thu tháng này', 'Top khách hàng', 'Tour sắp khởi hành', 'Nguồn marketing tháng này'];

  const panelTitle = panelData ? (panelData.title || 'Số liệu') : 'Số liệu';

  return (
    <main className="page asst">
      <div className="page-title-block">
        <h1 className="page-title">Trợ lý số liệu</h1>
        <p className="page-sub">Hỏi bằng ngôn ngữ tự nhiên, AI tự truy xuất số liệu TourKit và phân tích.</p>
      </div>

      <div className="asst-grid">
        {/* TRÁI: chat */}
        <section className="asst-chat">
          <div className="card-header asst-head">
            <div className="card-icon"><Icon name="sparkle" size={18} /></div>
            <h3>HỘI THOẠI</h3>
            <button className="btn btn-ghost btn-sm" style={{ marginLeft: 'auto' }} onClick={clearCache}
              disabled={clearing} title="Xóa cache số liệu — buộc hỏi lại lấy số mới">
              ↻ {clearing ? 'Đang xóa…' : 'Xóa cache'}
            </button>
          </div>

          <div className="asst-messages" ref={scrollRef}>
            {messages.length === 0 && (
              <div className="asst-suggest">
                <div className="label">Gợi ý câu hỏi</div>
                <div className="asst-suggest-grid">
                  {suggestions.map(s => (
                    <button key={s} className="asst-chip" onClick={() => send(s)}>
                      <Icon name="sparkle" size={13} /> <span>{s}</span>
                    </button>
                  ))}
                </div>
              </div>
            )}
            {messages.map((m, i) => (
              <div key={i} className={`asst-msg ${m.role} ${m.error ? 'error' : ''}`}>
                {m.role === 'assistant' && m.tool && m.tool !== 'none' &&
                  <span className="asst-tool-tag">{m.tool}</span>}
                <div className="asst-bubble">
                  {m.content
                    ? m.content
                    : (m.streaming ? <TypingDots stage={stage} /> : '')}
                </div>
              </div>
            ))}
          </div>

          <div className="asst-input-row">
            <input
              className="asst-input"
              placeholder="Hỏi về số liệu kinh doanh…"
              value={input}
              onChange={e => setInput(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') send(); }}
              disabled={loading}
            />
            <button className="btn btn-primary" onClick={send} disabled={loading || !input.trim()}>
              <Icon name="sparkle" size={14} /> Gửi
            </button>
          </div>
        </section>

        {/* PHẢI: số liệu */}
        <section className="asst-rightcol">
          <div className="card-header asst-head">
            <div className="card-icon"><Icon name="paper" size={18} /></div>
            <h3>{panelTitle.toUpperCase()}</h3>
            {panelData && panelData.kind && <span className="asst-kind-tag">{panelData.kind}</span>}
          </div>
          <DataPanel data={panelData} onAsk={(q) => send(q)} />
        </section>
      </div>
    </main>
  );
}

window.AssistantPage = AssistantPage;
