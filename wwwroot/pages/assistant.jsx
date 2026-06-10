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

  const timeline = _looksTimeline(rows, labelKey);
  // ─── Chart-picker rõ ràng (chọn mặc định) ─────────────────────────────────
  // • Tròn (donut) — CƠ CẤU/SHARE: 1 chỉ số duy nhất, dữ liệu phân loại (không timeline),
  //   2-10 mục → vd cơ cấu sản phẩm, nguồn marketing, top KH theo share.
  // • Cột DỌC (vertical bar) — TIMELINE: trục X là thời gian (T1, T2…, ngày…)
  //   → vd doanh thu/chi phí theo tháng, dòng tiền theo ngày.
  // • Cột NGANG (horizontal bar) — RANKING dài hoặc multi-metric: phân loại nhưng >10 mục
  //   hoặc nhiều series → vd ranking 20 mục, so sánh nhiều chỉ số/cùng category.
  return { labelKey, series: numKeys.map(k => ({ key: k })), timeline };
}

// Kind gợi ý "cơ cấu" → ưu tiên donut nếu 1 chỉ số (marketing, top theo share…)
const _COMP_KINDS = new Set(['marketing', 'topcustomers', 'topsellers']);
function _defaultChartType(info, rows, kind) {
  if (info.timeline) return 'bar-vertical';
  const singleMetric = info.series.length === 1;
  const fewRows = rows.length <= 10;
  if (singleMetric && fewRows) return 'doughnut';
  if (singleMetric && _COMP_KINDS.has(kind)) return 'doughnut';
  return 'bar-horizontal';
}

// Bảng màu cho từng lát biểu đồ tròn.
const _SLICE_COLORS = ['#F97316', '#0EA5E9', '#10B981', '#8B5CF6', '#F59E0B', '#EF4444', '#14B8A6', '#6366F1', '#EC4899', '#84CC16'];

// Biểu đồ chuyên nghiệp bằng Chart.js (nạp qua CDN ở index.html).
// chartType: 'doughnut' | 'bar-vertical' | 'bar-horizontal' (mặc định từ _defaultChartType + kind).
function ChartView({ rows, info, focus, kind }) {
  const canvasRef = React.useRef(null);
  const chartRef = React.useRef(null);
  const allKeys = info.series.map(s => s.key);
  const focusSel = (focus || []).filter(f => allKeys.includes(f));
  const [sel, setSel] = React.useState(focusSel.length ? focusSel : allKeys.slice(0, info.timeline ? 3 : 1));
  const [chartType, setChartType] = React.useState(() => _defaultChartType(info, rows, kind));
  // Khi data đổi (panelData khác câu hỏi khác) → reset chartType + sel theo mặc định mới.
  React.useEffect(() => {
    setChartType(_defaultChartType(info, rows, kind));
    setSel(focusSel.length ? focusSel : allKeys.slice(0, info.timeline ? 3 : 1));
  }, [info.labelKey, rows.length, kind]);

  const activeCount = info.series.filter(s => sel.includes(s.key)).length;
  // Tròn hợp lý: dữ liệu phân loại (không timeline), 2–12 mục (donut chart vẫn đọc được).
  const canPie = !info.timeline && rows.length >= 2 && rows.length <= 12;

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
          responsive: true, maintainAspectRatio: false, cutout: '62%',
          plugins: {
            legend: { display: false },   // legend dòng do React render bên ngoài (mockup)
            tooltip: { callbacks: { label: (ctx) => ` ${ctx.label}: ${fmt(ctx.parsed)} (${(ctx.parsed / total * 100).toFixed(1)}%)` } }
          }
        }
      };
    } else {
      const horizontal = chartType === 'bar-horizontal';
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

  // Tính legend dòng (dot màu + nhãn + giá trị) cho donut — mockup style.
  const showRowLegend = chartType === 'doughnut' && canPie && activeCount === 1;
  const legendRows = showRowLegend ? (() => {
    const key = info.series.find(s => sel.includes(s.key))?.key;
    if (!key) return [];
    const money = _isMoneyKey(key);
    return rows.slice().sort((a, b) => (Number(b[key]) || 0) - (Number(a[key]) || 0)).map((r, i) => ({
      label: String(r[info.labelKey]),
      value: Number(r[key]) || 0,
      formatted: money ? _vndShort(Number(r[key]) || 0) : window.fmtNum(Number(r[key]) || 0),
      unit: money ? 'đ' : '',
      color: _SLICE_COLORS[i % _SLICE_COLORS.length],
    }));
  })() : [];

  return (
    <div className="asst-chart">
      {(info.series.length > 1 || canPie) && (
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
          <div className="asst-type-toggle">
            {canPie && activeCount === 1 && (
              <button className={chartType === 'doughnut' ? 'on' : ''} onClick={() => setChartType('doughnut')} title="Cơ cấu (tròn)">
                <Icon name="chart" size={13} /> Tròn
              </button>
            )}
            <button className={chartType === 'bar-vertical' ? 'on' : ''} onClick={() => setChartType('bar-vertical')} title="Theo thời gian (cột dọc)">
              <Icon name="trend" size={13} /> Cột dọc
            </button>
            <button className={chartType === 'bar-horizontal' ? 'on' : ''} onClick={() => setChartType('bar-horizontal')} title="So sánh / xếp hạng (cột ngang)">
              <Icon name="list" size={13} /> Cột ngang
            </button>
          </div>
        </div>
      )}
      <div className={'asst-canvas-wrap' + (showRowLegend ? ' donut' : '')}><canvas ref={canvasRef} /></div>
      {showRowLegend && legendRows.length > 0 && (
        <div className="asst-legend">
          {legendRows.map((l, i) => (
            <div key={i} className="asst-legend-row">
              <span className="asst-legend-dot" style={{ background: l.color }} />
              <span className="asst-legend-label">{l.label.toUpperCase()}</span>
              <span className="asst-legend-val">{l.formatted}{l.unit && <em> {l.unit}</em>}</span>
            </div>
          ))}
        </div>
      )}
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

// TraceView đã extract sang components/trace-view.jsx (window.TraceView).
// Local alias để giữ JSX cũ trong file này.
const TraceView = (props) => window.TraceView ? <window.TraceView {...props} /> : null;

function DataPanel({ data, onAsk }) {
  // Memo hóa rows/chart theo `data` (ổn định suốt lúc stream) → KHÔNG dựng lại chart mỗi token
  // (trước đây tính lại mỗi render → ChartView destroy+create liên tục → nhấp nháy khó chịu).
  const rows = React.useMemo(() => _extractRows(data ? data.raw : null), [data]);
  const isKpi = !!data && data.kind === 'kpi';   // financial-summary: items đã thành thẻ → không bảng/chart trùng
  const chart = React.useMemo(() => (data && !isKpi) ? _chartInfo(rows) : null, [data, isKpi, rows]);
  // Map nhanh label → compareStat (để hiện delta vs kỳ đối chiếu khi render).
  // HOOK PHẢI ở đây — trước early return — để React giữ hook order ổn định.
  const compareMap = React.useMemo(() => {
    if (!data || !data.compare || !data.compare.compareStats) return null;
    const m = {};
    for (const s of data.compare.compareStats) m[s.label] = s;
    return m;
  }, [data]);

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
      {data.compare && (
        <div className="asst-compare-banner">
          <Icon name="chart" size={14} />
          <span>Đang so sánh:</span>
          <strong>{data.compare.primaryLabel}</strong>
          <span className="asst-cmp-vs">vs</span>
          <strong className="asst-cmp-prev">{data.compare.compareLabel}</strong>
        </div>
      )}

      {data.stats && data.stats.length > 0 && (() => {
        const GROUP_VI = { revenue: 'Doanh thu', expense: 'Chi phí', profit: 'Lợi nhuận', other: 'Khác' };
        const ORDER = ['revenue', 'expense', 'profit', 'other'];
        const hasGroups = data.stats.some(s => s.group);
        const card = (s, i) => {
          const cmp = compareMap ? compareMap[s.label] : null;
          // Bỏ delta cho stat đếm bản ghi (Tổng số / Số tour / Số khách) — % không có nghĩa.
          const noisyDelta = /^(Tổng số|Số tour|Số khách|Số bản ghi|Số lượng)$/i.test(s.label);
          let delta = null;
          if (cmp && typeof cmp.value === 'number' && cmp.value !== 0 && !noisyDelta) {
            const pct = ((s.value - cmp.value) / Math.abs(cmp.value)) * 100;
            const isUp = pct >= 0;
            const arrow = isUp ? '▲' : '▼';
            // Lợi nhuận tăng = tốt (xanh); chi phí tăng = xấu (đỏ); doanh thu tăng = tốt.
            const goodIfUp = (s.group !== 'expense');
            const cls = (isUp === goodIfUp) ? 'asst-delta-pos' : 'asst-delta-neg';
            delta = (
              <div className={'asst-stat-delta ' + cls}>
                <span className="asst-delta-arrow">{arrow}</span>
                <span className="asst-delta-pct">{Math.abs(pct).toFixed(1)}%</span>
                <span className="asst-delta-prev" title={cmp.unit === 'đ' ? window.fmtVND(cmp.value) : window.fmtNum(cmp.value)}>
                  vs {cmp.unit === 'đ' ? _vndShort(cmp.value) : window.fmtNum(cmp.value)}
                </span>
              </div>
            );
          }
          return (
            <div key={i} className="asst-stat">
              <div className="asst-stat-val" title={s.unit === 'đ' ? window.fmtVND(s.value) : window.fmtNum(s.value)}>
                {s.unit === 'đ' ? _vndShort(s.value) : window.fmtNum(s.value)}
              </div>
              <div className="asst-stat-label">{s.label}</div>
              {delta}
            </div>
          );
        };
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
          <ChartView rows={rows} info={chart} focus={data.focus} kind={data.kind} />
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

  // Xóa bộ nhớ hội thoại (SessionChatMemory) + reset UI về trạng thái ban đầu.
  async function resetMemory() {
    const ok = await window.appConfirm('Bắt đầu cuộc trò chuyện mới? Lịch sử hiện tại sẽ xoá.', {
      title: 'Đoạn mới', confirmLabel: 'Xoá hết', danger: true
    });
    if (!ok) return;
    try {
      await window.tourkitAuth.authedFetch('/api/v1/chat/memory', { method: 'DELETE' });
      setMessages([]);
      setPanelData(null);
      pushToast('Đã reset hội thoại', 'success');
    } catch (e) {
      pushToast('Reset lỗi: ' + e.message, 'error');
    }
  }

  const [messages, setMessages] = _aS([]);          // {role, content, trace?}
  const [input, setInput] = _aS('');
  const [loading, setLoading] = _aS(false);
  const [stage, setStage] = _aS(null);   // 'planning'|'fetching'|'analyzing' khi đang stream
  const [panelData, setPanelData] = _aS(null);
  // Debug toggle: hiện "Cách vận hành" dưới mỗi reply. Persist localStorage để giữ qua reload.
  const [debug, setDebug] = _aS(() => localStorage.getItem('tourkit_chat_debug') === '1');
  _aE(() => { localStorage.setItem('tourkit_chat_debug', debug ? '1' : '0'); }, [debug]);
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
          apiKey: (window.tourkit.ai.getKey && cfg.provider) ? window.tourkit.ai.getKey(cfg.provider) : undefined,
          debug: debug
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
            if (o.trace) patch(a => ({ ...a, trace: o.trace }));   // trace event đính cùng done (cache-hit path)
          }
          if (o.trace && !o.done) patch(a => ({ ...a, trace: o.trace }));   // trace event riêng (sau analysis stream)

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

  // ─── Suggestions: 4 quick + collapsible toàn bộ 17 tool theo nhóm ─────────
  // Quick chips (luôn hiện) = 4 câu hỏi đại diện cho 4 nhóm phổ biến nhất
  const suggestions = [
    { q: 'Doanh thu tháng này',              icon: 'dollar' },
    { q: 'Top khách hàng',                    icon: 'star' },
    { q: 'Cơ cấu nguồn khách Marketing',     icon: 'chart' },
    { q: 'Tour sắp khởi hành',                icon: 'plane' },
  ];

  // Toàn bộ gợi ý phân theo 7 nhóm — tương ứng 17 tool của AI catalog.
  // User bấm "Xem tất cả gợi ý" sẽ thấy grid full, cho biết hệ thống có gì.
  const allSuggestions = [
    { group: '💰 Tài chính', items: [
      { q: 'Doanh thu tháng này',                                  icon: 'dollar' },
      { q: 'So sánh doanh thu tháng này với tháng trước',          icon: 'trend' },
      { q: 'Chi tiết tài chính tháng 6/2026 (12 chỉ số)',          icon: 'chart' },
      { q: 'Dòng tiền 30 ngày qua theo ngày',                      icon: 'cashflow' },
    ]},
    { group: '👥 Khách hàng', items: [
      { q: 'Top 10 khách hàng tháng này',                          icon: 'star' },
      { q: 'Khách hàng chưa chăm sóc 30 ngày',                     icon: 'warning' },
      { q: 'Khách sinh nhật tháng này',                            icon: 'gift' },
      { q: 'Lịch hẹn CSKH tuần này',                               icon: 'calendar' },
    ]},
    { group: '📊 Marketing', items: [
      { q: 'Cơ cấu nguồn khách năm 2026',                          icon: 'chart' },
      { q: 'Nguồn khách marketing tháng 5/2026',                   icon: 'megaphone' },
    ]},
    { group: '🧳 Sản phẩm Tour', items: [
      { q: 'Tour sắp khởi hành',                                   icon: 'plane' },
      { q: 'Danh sách tour FIT đang mở',                           icon: 'tours' },
      { q: 'Tour Visa tháng này',                                  icon: 'docs' },
      { q: 'Tour thị trường Nội địa Miền Nam',                     icon: 'map' },
    ]},
    { group: '💼 Bán hàng', items: [
      { q: 'Cơ hội bán hàng đang chờ xử lý',                       icon: 'inbox' },
      { q: 'Top seller doanh số cao nhất tháng này',               icon: 'trophy' },
      { q: 'Lead từ Pancake tuần này',                             icon: 'lead' },
    ]},
    { group: '🏢 Hiệu suất', items: [
      { q: 'Chi nhánh nào doanh số cao nhất quý 2/2026?',          icon: 'building' },
      { q: 'Dòng sản phẩm nào lãi nhất tháng này?',                icon: 'package' },
      { q: 'Thị trường nào doanh thu cao nhất năm nay?',           icon: 'world' },
    ]},
    { group: '✅ Quản lý', items: [
      { q: 'Công việc cần làm hôm nay',                            icon: 'check' },
      { q: 'Phiếu chi chờ duyệt',                                  icon: 'receipt' },
      { q: 'Thông báo cần xử lý',                                  icon: 'bell' },
    ]},
  ];
  const [expandedSuggest, setExpandedSuggest] = _aS(false);

  const panelTitle = panelData ? (panelData.title || 'Cơ cấu số liệu') : null;

  return (
    <main className="page asst">
      <window.PageShell.PageHero
        icon="sparkle"
        title="Trợ lý AI đọc báo cáo"
        badge="trực quan"
        sub="Hỏi đáp thông minh — AI tự truy xuất số liệu TourKit và trực quan hóa."
        status={{ label: 'DỮ LIỆU ĐANG KẾT NỐI', detail: `Tenant ${sessionInfo?.tenantId || '—'}` }}
        actions={<>
          <button
            className={'asst-debug-toggle' + (debug ? ' on' : '')}
            onClick={() => setDebug(v => !v)}
            title={debug ? 'Đang HIỆN cách vận hành dưới mỗi reply — bấm để TẮT' : 'Bấm để HIỆN cách vận hành (debug trace)'}>
            <Icon name="info" size={13} /> Cách vận hành {debug ? 'ON' : 'OFF'}
          </button>
          <button className="asst-reset" onClick={resetMemory} title="Bắt đầu cuộc trò chuyện mới">
            <Icon name="plus" size={14} /> Đoạn mới
          </button>
          <button className="asst-status-refresh" onClick={clearCache} disabled={clearing}
            title="Xóa cache số liệu — buộc hỏi lại lấy số mới">
            <Icon name="refresh" size={15} stroke={2.4} />
          </button>
        </>}
      />

      <div className="asst-grid2">
        {/* TRÁI: chat */}
        <section className="asst-pane asst-chat">
          <div className="asst-eyebrow">KÊNH PHÂN TÍCH NGÔN NGỮ TỰ NHIÊN <em>· NLP CHAT</em></div>

          <div className="asst-messages" ref={scrollRef}>
            {messages.length === 0 && (
              <div className="asst-greet">
                <div className="asst-greet-bubble">
                  <p><b>Xin chào!</b> Tôi là <b>Trợ lý số liệu</b> của hệ thống TourKit.</p>
                  <p>Tôi đã nạp toàn bộ dữ liệu vận hành du lịch năm 2026:
                    <b> Tài chính, Hiệu suất chi nhánh, Doanh số theo Sản phẩm, Thị trường</b> và <b>Nguồn khách Marketing</b>.</p>
                  <p>Bạn muốn tôi đọc và trực quan hóa báo cáo nào? Bấm gợi ý hoặc gõ câu hỏi bên dưới.</p>
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
                {m.trace && <TraceView trace={m.trace} />}
              </div>
            ))}
          </div>

          {/* GỢI Ý — luôn hiện (cả khi đã chat lẫn chưa). Toggle expand cho user
              discover được tất cả 17 tool. Khi chat: collapsed = 3 chips inline để
              không chiếm chỗ. Khi chưa chat: collapsed = grid 2x2 wide. */}
          <div className={'asst-quick' + (messages.length === 0 ? ' wide' : '')}>
            <div className="asst-quick-head">
              <span className="asst-quick-lbl">
                {messages.length > 0 ? 'GỢI Ý:' : 'GỢI Ý CÂU HỎI NHANH:'}
              </span>
              <button className="asst-suggest-toggle"
                onClick={() => setExpandedSuggest(v => !v)} disabled={loading}>
                <Icon name={expandedSuggest ? 'chevronUp' : 'chevronDown'} size={12} />
                {expandedSuggest ? 'Thu gọn' : 'Xem tất cả gợi ý'}
              </button>
            </div>
            {!expandedSuggest ? (
              messages.length > 0 ? (
                <div className="asst-suggest-row">
                  {suggestions.slice(0, 3).map(s => (
                    <button key={s.q} className="asst-quick-chip" onClick={() => send(s.q)} disabled={loading}>
                      <Icon name={s.icon} size={12} /> {s.q}
                    </button>
                  ))}
                </div>
              ) : (
                <div className="asst-quick-grid">
                  {suggestions.map(s => (
                    <button key={s.q} className="asst-quick-chip" onClick={() => send(s.q)}>
                      <Icon name={s.icon} size={13} /> {s.q}
                    </button>
                  ))}
                </div>
              )
            ) : (
              <div className="asst-suggest-all">
                {allSuggestions.map(grp => (
                  <div key={grp.group} className="asst-suggest-group">
                    <div className="asst-suggest-group-title">{grp.group}</div>
                    <div className="asst-suggest-group-chips">
                      {grp.items.map(s => (
                        <button key={s.q} className="asst-quick-chip" onClick={() => send(s.q)} disabled={loading}>
                          <Icon name={s.icon} size={13} /> {s.q}
                        </button>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="asst-input-row">
            <input
              className="asst-input"
              placeholder="Hỏi AI phân tích báo cáo du lịch… (nhấn Enter để gửi)"
              value={input}
              onChange={e => setInput(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') send(); }}
              disabled={loading}
            />
            <button className="asst-send" onClick={send} disabled={loading || !input.trim()}>
              <Icon name="arrowRight" size={16} stroke={2.4} />
            </button>
          </div>
        </section>

        {/* PHẢI: visualization slate */}
        <section className="asst-pane asst-right">
          <div className="asst-slate-head">
            <div>
              <div className="asst-eyebrow">
                TRỰC QUAN HÓA <em>· ACTIVE VISUALIZATION SLATE</em>
              </div>
              {panelTitle && <h2 className="asst-slate-title">{panelTitle}</h2>}
            </div>
            {panelData && <div className="asst-slate-ic"><Icon name="chart" size={18} /></div>}
          </div>
          <DataPanel data={panelData} onAsk={(q) => send(q)} />
        </section>
      </div>
    </main>
  );
}

window.AssistantPage = AssistantPage;
