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
  'expense','cost','chiphi',
  // key Việt từ 3 SP legacy (branch-performance / product-line / market-analysis):
  // ThucThu, ThucChi + 'comission' (SP đánh vần thiếu chữ m)
  'thucthu','thucchi','comission'];
// 'cohoi'/'donhang' = ĐẾM SỐ (cơ hội / đơn hàng) — chặn match nhầm hint 'tong'/'total' → tránh "78đ".
const _NOT_MONEY = ['count','qty','row','soluong','index','page','year','month','stt','cohoi','donhang'];
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
  statustext: 'Trạng thái', available: 'Còn chỗ', booked: 'Đã đặt', slots: 'Tổng chỗ', onhold: 'Giữ chỗ',
  sellername: 'Phụ trách', mayeucau: 'Mã yêu cầu', actualrevenue: 'Thực thu',
  totalexpense: 'Tổng chi phí', avgstar: 'Đánh giá', parentid: 'Thuộc nhóm',
  tourtype: 'Loại', percentage: 'Tỉ lệ',
  rank: 'Hạng', totalrevenue: 'Tổng chi tiêu',
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

// Badge tăng/giảm % so KỲ TRƯỚC (giống bảng web /sellers): tìm field anh em "<key>GrowthPct"
// (vd doanhThuFormatted → doanhThuGrowthPct, soDataKH → soDataKHGrowthPct). Xanh ▲ tăng / đỏ ▼ giảm;
// 0% hoặc không có → bỏ qua cho gọn. Render dưới giá trị trong cùng ô.
function _growthBadge(key, row) {
  if (!row || typeof row !== 'object') return null;
  const base = String(key).replace(/Formatted$/i, '');
  let g = row[base + 'GrowthPct'];
  if (g == null) g = row[key + 'GrowthPct'];
  if (typeof g !== 'number' || !isFinite(g) || g === 0) return null;
  const up = g > 0;
  return React.createElement('span',
    { className: 'asst-growth ' + (up ? 'up' : 'down'), title: 'So với kỳ trước' },
    (up ? '▲ ' : '▼ ') + Math.abs(g).toFixed(2) + '%');
}

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

// Donut chỉ hợp lệ khi mọi giá trị ≥ 0 và tổng > 0 — slice âm Chart.js bỏ qua
// (biểu đồ sai), tổng 0 thì vẽ vòng trống. Cả 2 case → dùng cột.
function _pieSafe(rows, series) {
  let sum = 0;
  for (const r of rows) {
    for (const s of series) {
      const v = Number(r[s.key]) || 0;
      if (v < 0) return false;
      sum += v;
    }
  }
  return sum > 0;
}

// Kind gợi ý "cơ cấu" → ưu tiên donut nếu 1 chỉ số (marketing, top theo share…)
const _COMP_KINDS = new Set(['marketing', 'topcustomers', 'topsellers']);
function _defaultChartType(info, rows, kind) {
  if (info.timeline) return 'bar-vertical';
  if (!_pieSafe(rows, info.series)) return 'bar-horizontal';   // số âm / toàn 0 → cột
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
function ChartView({ rows, info, focus, kind, compare }) {
  const canvasRef = React.useRef(null);
  const chartRef = React.useRef(null);
  // Chart.js lazy-load (chart-loader.js) — chỉ tải 201KB khi trang này mở.
  // chartReady flip true khi window.Chart sẵn sàng → effect render bên dưới re-run.
  const [chartReady, setChartReady] = React.useState(!!window.Chart);
  React.useEffect(() => {
    if (window.Chart) { setChartReady(true); return; }
    let alive = true;
    window.ensureChart?.().then(() => { if (alive) setChartReady(true); })
      .catch(e => console.warn('[chart] lazy-load fail:', e.message));
    return () => { alive = false; };
  }, []);
  const allKeys = info.series.map(s => s.key);
  const focusSel = (focus || []).filter(f => allKeys.includes(f));
  const [sel, setSel] = React.useState(focusSel.length ? focusSel : allKeys.slice(0, info.timeline ? 3 : 1));
  // Compare mode: BẮT BUỘC dạng cột (donut so sánh vô nghĩa). User: "compare lên cột chuẩn hơn".
  const isCompare = !!(compare && compare.compareRaw);
  const [chartType, setChartType] = React.useState(() =>
    isCompare ? 'bar-vertical' : _defaultChartType(info, rows, kind));
  // Khi data đổi (panelData khác câu hỏi khác) → reset chartType + sel theo mặc định mới.
  React.useEffect(() => {
    setChartType(isCompare ? 'bar-vertical' : _defaultChartType(info, rows, kind));
    setSel(focusSel.length ? focusSel : allKeys.slice(0, info.timeline ? 3 : 1));
  }, [info.labelKey, rows.length, kind, isCompare]);

  const activeCount = info.series.filter(s => sel.includes(s.key)).length;
  // Tròn hợp lý: phân loại + ko âm + tổng > 0. Compare KHÔNG cho phép tròn.
  const canPie = !isCompare && !info.timeline && rows.length >= 2 && rows.length <= 12 && _pieSafe(rows, info.series);

  React.useEffect(() => {
    if (!window.Chart || !canvasRef.current) return;
    const active = info.series.filter(s => sel.includes(s.key));
    if (!active.length) return;

    let data = rows.slice();
    if (!info.timeline) {                       // dữ liệu phân loại → sắp xếp giảm dần
      const k = active[0].key;
      data = data.sort((a, b) => (Number(b[k]) || 0) - (Number(a[k]) || 0));
    }
    // Compare: union labels từ 2 kỳ. Vd T6 có 2 kênh marketing, T5 có 7 → hiện đủ 7
    // (kênh nào kỳ kia không có sẽ 0). Không union → bỏ sót dữ liệu kỳ đối chiếu.
    let labels;
    let cmpMap = null;
    if (isCompare) {
      const cmpRows = _extractRows(compare.compareRaw) || [];
      cmpMap = {};
      for (const r of cmpRows) cmpMap[String(r[info.labelKey])] = r;
      const metric = active[0].key;
      const union = new Map();
      for (const r of data) union.set(String(r[info.labelKey]), r);
      for (const r of cmpRows) if (!union.has(String(r[info.labelKey]))) union.set(String(r[info.labelKey]), r);
      // Sắp theo MAX(primary, compare) giảm dần để cột dài nhất trước
      const sorted = [...union.entries()].sort((a, b) => {
        const av = Math.max(Number((data.find(x => String(x[info.labelKey]) === a[0]) || {})[metric]) || 0,
                            Number((cmpMap[a[0]] || {})[metric]) || 0);
        const bv = Math.max(Number((data.find(x => String(x[info.labelKey]) === b[0]) || {})[metric]) || 0,
                            Number((cmpMap[b[0]] || {})[metric]) || 0);
        return bv - av;
      });
      labels = sorted.map(([l]) => l);
      // primaryByLabel để build dataset
      const primMap = {};
      for (const r of data) primMap[String(r[info.labelKey])] = r;
      data = labels.map(l => primMap[l] || {});   // re-align với union order
      cmpMap = cmpMap;   // (giữ tên trong scope)
      // gắn vào closure để dataset dùng
      data.__cmpMap = cmpMap;
    } else {
      labels = data.map(r => String(r[info.labelKey]));
    }
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
      let datasets;
      if (isCompare) {
        // Grouped 2-series: 1 chỉ số × 2 kỳ. cmpMap + data đã reorder theo union labels ở trên.
        const metric = active[0].key;
        datasets = [
          {
            label: compare.primaryLabel || 'Kỳ chính',
            data: labels.map((l, i) => Number((data[i] || {})[metric]) || 0),
            backgroundColor: _SLICE_COLORS[0],
            borderRadius: 5, maxBarThickness: 36
          },
          {
            label: compare.compareLabel || 'Kỳ đối chiếu',
            data: labels.map(l => Number((cmpMap[l] || {})[metric]) || 0),
            backgroundColor: _SLICE_COLORS[1],
            borderRadius: 5, maxBarThickness: 36
          }
        ];
      } else {
        datasets = active.map((s, i) => ({
          label: _seriesName(s.key),
          data: data.map(r => Number(r[s.key]) || 0),
          backgroundColor: _SLICE_COLORS[i % _SLICE_COLORS.length],
          borderRadius: 5,
          maxBarThickness: 48
        }));
      }
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
  }, [rows, info, sel, chartType, canPie, isCompare, compare, chartReady]);  // chartReady → re-render khi Chart.js lazy-load xong

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
      {(info.series.length > 1 || canPie || isCompare) && (
        <div className="asst-chart-toolbar">
          {info.series.length > 1 && !isCompare ? (
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
      <div className={'asst-canvas-wrap' + (showRowLegend ? ' donut' : '')}>
        <canvas ref={canvasRef} />
        {!chartReady && (
          <div className="asst-chart-loading">
            <span className="asst-dots"><i /><i /><i /></span> Đang tải biểu đồ…
          </div>
        )}
      </div>
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

// Tăng mỗi khi panelData đổi — dùng làm key remount ChartView để câu hỏi mới
// luôn nhận chartType/metric mặc định mới (trước đây cùng kind+labelKey+số dòng
// thì giữ nguyên lựa chọn tay của câu TRƯỚC → đồ thị sai kiểu).
let _panelVer = 0;

function DataPanel({ data, onAsk, proposal, clarify, actionBusy, onConfirmProposal, onCancelProposal, onChooseClarify }) {
  // Memo hóa rows/chart theo `data` (ổn định suốt lúc stream) → KHÔNG dựng lại chart mỗi token
  // (trước đây tính lại mỗi render → ChartView destroy+create liên tục → nhấp nháy khó chịu).
  const rows = React.useMemo(() => _extractRows(data ? data.raw : null), [data]);
  const isKpi = !!data && data.kind === 'kpi';   // financial-summary: items đã thành thẻ → không bảng/chart trùng
  const chart = React.useMemo(() => (data && !isKpi) ? _chartInfo(rows) : null, [data, isKpi, rows]);
  const dataVer = React.useMemo(() => ++_panelVer, [data]);
  // Map nhanh label → compareStat (để hiện delta vs kỳ đối chiếu khi render).
  // HOOK PHẢI ở đây — trước early return — để React giữ hook order ổn định.
  const compareMap = React.useMemo(() => {
    if (!data || !data.compare || !data.compare.compareStats) return null;
    const m = {};
    for (const s of data.compare.compareStats) m[s.label] = s;
    return m;
  }, [data]);

  // Phân trang bảng: mặc định 50 dòng, "Xem tất cả" để bung hết (scroll trong khung). Reset khi data đổi.
  const [showAllRows, setShowAllRows] = React.useState(false);
  React.useEffect(() => { setShowAllRows(false); }, [data]);

  // Action tools (Task 12 → UX 2026-07-14): thẻ đề xuất/làm rõ hành động render Ở PANEL PHẢI này,
  // KHÔNG ở luồng chat trái nữa — ưu tiên cao nhất, che luôn số liệu cũ đang hiện cho tới khi
  // user xác nhận/hủy/chọn xong (đóng thẻ → panel quay lại `data` như bình thường).
  // Đang gọi /action/resolve (sau khi chọn clarify) hoặc /action/execute (sau khi Xác nhận) —
  // che thẻ + số liệu cũ bằng loading để user biết hệ thống đang xử lý, không tưởng bị treo.
  if (actionBusy) {
    return (
      <div className="asst-data asst-action-panel">
        <div className="jv-action-card jv-action-busy">
          <div className="jv-action-spinner" />
          <div className="jv-action-busy-text">Đang xử lý…</div>
        </div>
      </div>
    );
  }
  if (proposal) {
    return (
      <div className="asst-data asst-action-panel">
        <window.ActionConfirmCard proposal={proposal} onConfirm={onConfirmProposal} onCancel={onCancelProposal} />
      </div>
    );
  }
  if (clarify) {
    return (
      <div className="asst-data asst-action-panel">
        <window.ActionClarifyList clarify={clarify} onChoose={onChooseClarify} />
      </div>
    );
  }

  if (!data) {
    return (
      <div className="asst-panel-empty">
        <div className="asst-empty-icon"><Icon name="paper" size={26} /></div>
        <p className="asst-empty-title">Số liệu sẽ hiển thị ở đây</p>
        <p className="asst-hint">Ví dụ: “Doanh thu tháng này”, “Top khách hàng”, “Tour sắp khởi hành”.</p>
      </div>
    );
  }

  // action-result (Task 12): customer-review/deal-score/mail-list không phải shape bảng/chart —
  // render qua ActionDataCard chuyên biệt, bỏ qua toàn bộ logic bảng/chart bên dưới.
  if (data.kind === 'customer-review' || data.kind === 'deal-score' || data.kind === 'mail-list') {
    // Tiêu đề đã hiển thị ở slate-head (panelTitle = data.title) — KHÔNG lặp lại trong panel nữa.
    return (
      <div className="asst-data">
        <window.ActionDataCard data={data} />
      </div>
    );
  }

  // Data RỖNG (tool chạy nhưng 0 bản ghi, 0 chỉ số) — hiện empty state rõ ràng
  // thay vì <details>JSON thô (case: lọc không khớp, kỳ không có số liệu).
  const rawEmpty = data.raw == null
    || (Array.isArray(data.raw) && data.raw.length === 0)
    || (typeof data.raw === 'object' && !Array.isArray(data.raw) && Object.keys(data.raw).length === 0);
  const noStats = !data.stats || data.stats.length === 0;
  if (noStats && (!rows || rows.length === 0) && rawEmpty) {
    return (
      <div className="asst-panel-empty">
        <div className="asst-empty-icon"><Icon name="search" size={26} /></div>
        <p className="asst-empty-title">Không có số liệu khớp với câu hỏi</p>
        <p className="asst-hint">Thử đổi khoảng thời gian, bỏ bớt điều kiện lọc, hoặc hỏi cách khác.</p>
        {data.suggestions && data.suggestions.length > 0 && onAsk && (
          <div className="asst-suggest-grid" style={{ marginTop: 12 }}>
            {data.suggestions.map(q => (
              <button key={q} className="asst-chip" onClick={() => onAsk(q)}>
                <Icon name="sparkle" size={12} /> <span>{q}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    );
  }

  // Nhãn cột do SERVER khai báo (envelope.columns: field→nhãn TV có dấu). Khớp case-insensitive
  // vì key dữ liệu có thể camelCase còn key columns có thể PascalCase.
  const serverCols = (data.columns && typeof data.columns === 'object') ? data.columns : null;
  const serverColLower = {};
  if (serverCols) for (const k of Object.keys(serverCols)) serverColLower[k.toLowerCase()] = serverCols[k];
  const labelOf = (c) => serverColLower[String(c).toLowerCase()] || _colLabel(c);

  let columns = [];
  if (rows && rows.length) {
    // map lowercased→tên key thật có trong dữ liệu
    const keyMap = {};
    for (const r of rows.slice(0, 20))
      if (r && typeof r === 'object')
        for (const k of Object.keys(r)) if (!(k.toLowerCase() in keyMap)) keyMap[k.toLowerCase()] = k;

    // 1) ƯU TIÊN cột do SERVER khai báo — đúng thứ tự + nhãn có dấu (mọi section /api/ai/* đều có columns).
    if (serverCols)
      columns = Object.keys(serverCols).map(ck => keyMap[ck.toLowerCase()]).filter(Boolean);

    // 2) Chưa có → cột theo NGỮ CẢNH (loại dữ liệu); chỉ giữ cột thực sự có trong dữ liệu.
    const pref = _KIND_COLS[data.kind];
    if (!columns.length && pref) columns = pref.map(p => keyMap[p.toLowerCase()]).filter(Boolean);

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

      {/* Gate KHÔNG check window.Chart — ChartView tự lazy-load Chart.js (chart-loader.js)
          khi mount. Nếu check window.Chart ở đây → component không mount → loader không chạy. */}
      {chart && (
        <div className="asst-block">
          <div className="label">Biểu đồ</div>
          {/* key=dataVer: remount khi panelData đổi → chartType/metric về mặc định đúng cho data MỚI */}
          <ChartView key={dataVer} rows={rows} info={chart} focus={data.focus} kind={data.kind} compare={data.compare} />
        </div>
      )}

      {!isKpi && (rows && rows.length > 0 ? (
        <div className="asst-block">
          <div className="label">Bảng dữ liệu</div>
          <div className="asst-table-wrap">
            <table className="asst-table">
              <thead>
                <tr>{columns.map(c => <th key={c}>{labelOf(c)}</th>)}</tr>
              </thead>
              <tbody>
                {rows.slice(0, showAllRows ? rows.length : 50).map((r, ri) => (
                  <tr key={ri}>{columns.map(c => (
                    <td key={c}><span className="asst-cell-val">{_fmtCell(c, r[c])}</span>{_growthBadge(c, r)}</td>
                  ))}</tr>
                ))}
              </tbody>
            </table>
          </div>
          {rows.length > 50 && (
            <button type="button" className="asst-rowmore" onClick={() => setShowAllRows(v => !v)}>
              {showAllRows ? '▲ Thu gọn' : `▼ Xem tất cả ${rows.length} dòng (đang hiện 50)`}
            </button>
          )}
        </div>
      ) : (!rawEmpty && (
        // Raw có nội dung nhưng không tabular (object lạ) → cho xem JSON gốc.
        // Raw RỖNG → không render gì (stats phía trên là đủ).
        <details className="asst-raw">
          <summary>Xem dữ liệu gốc</summary>
          <pre className="asst-json">{JSON.stringify(data.raw, null, 2)}</pre>
        </details>
      )))}

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

  // ── Speech-to-Text state (ghi âm / upload audio → transcript) ───────────────
  // recState: 'idle' | 'recording' | 'uploading'
  const [recState, setRecState] = _aS('idle');
  const [recElapsed, setRecElapsed] = _aS(0);   // giây từ khi bắt đầu record
  const mediaRef = _aR(null);                    // {recorder, stream, chunks}
  const recTimerRef = _aR(null);
  const fileInputRef = _aR(null);

  // Helper: upload blob/file → /speech/transcribe → fill input
  const transcribeBlob = async (blob, fileName) => {
    if (!blob || blob.size === 0) { pushToast('Audio rỗng', 'warn'); return; }
    setRecState('uploading');
    try {
      const fd = new FormData();
      fd.append('file', blob, fileName || 'recording.webm');
      // v9: server đọc OpenAI key từ appsettings — FE không gửi apiKey.
      const r = await window.tourkitAuth.authedFetch('/api/v1/speech/transcribe', {
        method: 'POST', body: fd,
      });
      const j = await r.json();
      if (!r.ok || j.error) throw new Error(j.error || 'HTTP ' + r.status);
      // Append vào input (giữ text cũ user đã gõ + thêm transcript)
      const t = (j.text || '').trim();
      if (!t) { pushToast('Không nhận diện được tiếng', 'warn'); return; }
      setInput(prev => prev ? (prev.trim() + ' ' + t) : t);
      pushToast(`✓ Nhận diện ${j.durationSec}s · ${t.length}ch (${j.language || 'vi'})`);
    } catch (e) {
      pushToast('Lỗi transcribe: ' + e.message, 'error');
    } finally {
      setRecState('idle');
      setRecElapsed(0);
    }
  };

  const startRecording = async () => {
    if (!navigator.mediaDevices?.getUserMedia) {
      pushToast('Trình duyệt không hỗ trợ ghi âm', 'error'); return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      // Prefer webm/opus (Chrome/Edge/Firefox); fallback default
      const mime = MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
        ? 'audio/webm;codecs=opus' : 'audio/webm';
      const recorder = new MediaRecorder(stream, { mimeType: mime });
      const chunks = [];
      recorder.ondataavailable = (e) => { if (e.data?.size > 0) chunks.push(e.data); };
      recorder.onstop = async () => {
        stream.getTracks().forEach(t => t.stop());
        const blob = new Blob(chunks, { type: mime });
        await transcribeBlob(blob, 'recording.webm');
      };
      mediaRef.current = { recorder, stream, chunks };
      recorder.start(250);    // emit chunks mỗi 250ms để giảm rủi ro mất data nếu lỗi
      setRecState('recording');
      setRecElapsed(0);
      // Timer
      const t0 = Date.now();
      recTimerRef.current = setInterval(() => {
        setRecElapsed(Math.floor((Date.now() - t0) / 1000));
        // Soft cap 5 phút — tránh user quên dừng → Whisper sẽ reject >25MB
        if ((Date.now() - t0) > 5 * 60_000) stopRecording();
      }, 250);
    } catch (e) {
      // Phân loại lỗi getUserMedia chuẩn DOMException name (https://w3c.github.io/mediacapture-main/#dom-mediadeviceshandlers)
      let msg;
      switch (e.name) {
        case 'NotFoundError':                 // mic vật lý không tồn tại / OS chặn truy cập app
        case 'DevicesNotFoundError':
          msg = 'Không tìm thấy mic. Kiểm tra: (1) Windows Settings > Privacy > Microphone: BẬT, (2) cắm mic / bật mic laptop, (3) đóng Teams/Zoom đang chiếm mic. Hoặc bấm 📎 upload file audio thay vì ghi âm.';
          break;
        case 'NotAllowedError':               // user/browser chặn quyền
        case 'PermissionDeniedError':
          msg = 'Bị chặn quyền mic. Click 🔒 cạnh URL → Microphone → Allow → reload trang.';
          break;
        case 'NotReadableError':              // mic bị app khác chiếm (Windows exclusive mode)
        case 'TrackStartError':
          msg = 'Mic đang bị app khác dùng (Teams/Zoom/Discord). Đóng app đó rồi thử lại.';
          break;
        case 'OverconstrainedError':
        case 'ConstraintNotSatisfiedError':
          msg = 'Mic không hỗ trợ định dạng yêu cầu. Thử mic khác.';
          break;
        case 'SecurityError':                 // không phải HTTPS / localhost
          msg = 'Mic chỉ work trên HTTPS hoặc localhost. URL hiện tại không secure.';
          break;
        case 'AbortError':                    // user/system hủy giữa chừng
          msg = 'Ghi âm bị hủy. Thử lại.';
          break;
        default:
          msg = 'Lỗi mic: ' + (e.message || e.name || 'unknown') + '. Có thể dùng 📎 upload file audio thay thế.';
      }
      pushToast(msg, 'error');
    }
  };

  const stopRecording = () => {
    if (recTimerRef.current) { clearInterval(recTimerRef.current); recTimerRef.current = null; }
    const m = mediaRef.current;
    if (m?.recorder?.state === 'recording') {
      try { m.recorder.stop(); } catch {}
    }
    mediaRef.current = null;
  };

  const cancelRecording = () => {
    if (recTimerRef.current) { clearInterval(recTimerRef.current); recTimerRef.current = null; }
    const m = mediaRef.current;
    if (m?.recorder?.state === 'recording') {
      // Replace onstop để KHÔNG transcribe khi cancel
      m.recorder.onstop = () => m.stream.getTracks().forEach(t => t.stop());
      try { m.recorder.stop(); } catch {}
    }
    mediaRef.current = null;
    setRecState('idle');
    setRecElapsed(0);
  };

  const handleFileUpload = (e) => {
    const f = e.target.files?.[0];
    e.target.value = '';   // reset để chọn cùng file lại được
    if (!f) return;
    if (!/^audio\//i.test(f.type) && !/\.(mp3|m4a|wav|webm|ogg|flac|mp4)$/i.test(f.name)) {
      pushToast('File phải là audio (mp3/m4a/wav/webm/ogg/flac)', 'error'); return;
    }
    if (f.size > 25 * 1024 * 1024) {
      pushToast('File quá 25MB (Whisper max)', 'error'); return;
    }
    transcribeBlob(f, f.name);
  };

  const fmtElapsed = (s) => `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
  const [stage, setStage] = _aS(null);   // 'planning'|'fetching'|'analyzing' khi đang stream
  const [panelData, setPanelData] = _aS(null);
  // Action tools (Task 12): AI đề xuất 1 hành động ghi (cần user Xác nhận) hoặc cần làm rõ trước.
  const [pendingProposal, setPendingProposal] = _aS(null);
  const [pendingClarify, setPendingClarify] = _aS(null);
  // Đang gọi /action/resolve (chọn clarify) hoặc /action/execute (xác nhận) — hiện loading ở panel
  // để user không tưởng bị treo (2 endpoint này có thể mất vài giây: re-resolve + AI chấm/đánh giá).
  const [actionBusy, setActionBusy] = _aS(false);
  // Debug toggle: hiện "Cách vận hành" dưới mỗi reply. Persist localStorage để giữ qua reload.
  const [debug, setDebug] = _aS(() => localStorage.getItem('tourkit_chat_debug') === '1');
  _aE(() => { localStorage.setItem('tourkit_chat_debug', debug ? '1' : '0'); }, [debug]);
  const scrollRef = _aR(null);

  _aE(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages, loading, pendingProposal, pendingClarify]);

  async function send(textArg) {
    const text = (typeof textArg === 'string' ? textArg : input).trim();
    if (!text || loading || !sessionId) return;
    const next = [...messages, { role: 'user', content: text }];
    const asstIdx = next.length;   // vị trí message assistant sẽ thêm
    setMessages([...next, { role: 'assistant', content: '', tool: null, streaming: true }]);
    setInput('');
    setLoading(true);
    setStage('planning');
    // Câu hỏi mới → bỏ thẻ action còn treo + XÓA số liệu cũ ở panel để TRỰC QUAN HÓA phản ánh
    // đúng lượt hiện tại (không giữ chart cũ như "Dòng tiền & Lợi nhuận" khi lượt này chỉ là text/hỏi lại).
    setPendingProposal(null);
    setPendingClarify(null);
    setPanelData(null);

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
          debug: debug
        })
      });
      if (resp.status === 401) { pushToast('Phiên hết hạn — đăng nhập lại', 'error'); logout(); return; }
      if (!resp.ok || !resp.body) {
        const t = await resp.text().catch(() => '');
        throw new Error(t.slice(0, 200) || ('HTTP ' + resp.status));
      }

      let dataSet = false;   // panel data set 1 lần (stage analyzing) → done KHÔNG set lại → chart không vẽ lại
      await window.tourkitUtil.readSSE(resp, o => {
        // Action tools (Task 12): 3 event kèm sau LUÔN là {done:true} trơn (không reply/data/toolName) —
        // tự đóng turn ở đây, không dựa vào nhánh o.done bên dưới.
        if (o.kind === 'action-result') {
          const result = o.result || {};
          patch(a => ({ ...a, content: result.message || '', streaming: false }));
          if (result.data) { setPanelData(result.data); dataSet = true; }
          setStage(null);
          return;
        }
        if (o.kind === 'action-proposal') {
          // Không patch content bubble — thẻ action-proposal (bên dưới) đã hiện summary/title,
          // patch nữa sẽ hiện trùng 2 lần. Bỏ bubble assistant placeholder rỗng luôn.
          const proposal = o.proposal || {};
          setMessages(m => m.slice(0, asstIdx));
          setPendingProposal(proposal);
          setStage(null);
          return;
        }
        if (o.kind === 'action-clarify') {
          // Tương tự action-proposal — câu hỏi đã hiện trong thẻ ActionClarifyList, không cần bubble riêng.
          const clarify = o.clarify || {};
          setMessages(m => m.slice(0, asstIdx));
          setPendingClarify(clarify);
          setStage(null);
          return;
        }
        if (o.error) { patch(a => ({ ...a, content: '⚠️ ' + o.error, error: true, streaming: false })); setStage(null); return; }
        if (o.stage) { setStage(o.stage); if (o.data) { setPanelData(o.data); dataSet = true; } if (o.tool) patch(a => ({ ...a, tool: o.tool })); return; }
        if (o.delta) { setStage(null); patch(a => ({ ...a, content: a.content + o.delta })); return; }
        if (o.done) {
          if (o.reply) patch(a => ({ ...a, content: o.reply }));
          if (o.toolName) patch(a => ({ ...a, tool: o.toolName }));
          if (o.data && !dataSet) setPanelData(o.data);   // chỉ set nếu chưa có (luồng cache-hit)
          if (o.trace) patch(a => ({ ...a, trace: o.trace }));   // trace event đính cùng done (cache-hit path)
        }
        if (o.trace && !o.done) patch(a => ({ ...a, trace: o.trace }));   // trace event riêng (sau analysis stream)
      });
    } catch (e) {
      patch(a => ({ ...a, content: '⚠️ ' + e.message, error: true }));
    } finally {
      patch(a => ({ ...a, streaming: false }));
      setLoading(false);
      setStage(null);
    }
  }

  // Xác nhận 1 action-proposal (Task 12): POST params gốc + field user đã sửa → thực thi thật.
  async function confirmAction(editedVals) {
    if (!pendingProposal) return;
    const proposal = pendingProposal;
    setActionBusy(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/assistant/action/execute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          actionId: proposal.actionId,
          action: proposal.action,
          params: { ...(proposal.params || {}), ...editedVals }
        })
      });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { pushToast(j.error || 'Thực thi hành động lỗi', 'error'); return; }
      // Giữ card lại KHÔNG cần — thực thi xong thì đóng, thêm message kết quả mới.
      setPendingProposal(null);
      setMessages(m => [...m, { role: 'assistant', content: j.message || 'Đã thực hiện.', streaming: false }]);
      if (j.data) setPanelData(j.data);
    } catch (e) {
      pushToast('Lỗi thực thi: ' + e.message, 'error');
      // Lỗi mạng/parse → giữ nguyên card để user thử lại, không setPendingProposal(null).
    } finally {
      setActionBusy(false);
    }
  }

  // Chọn 1 lựa chọn làm rõ (nhiều bản ghi trùng tên, vd 3 nhân viên "Nguyễn Văn A"): gửi id THẬT đã
  // chọn tới /assistant/action/resolve — backend inject id vào params gốc rồi rebuild lại proposal/result
  // KHÔNG re-resolve theo tên (gửi lại label như trước đây sẽ vẫn mơ hồ y hệt → lặp vô hạn khi trùng tên).
  async function onClarifyChoose(chosenId, chosenLabel, chosenHint) {
    const clarify = pendingClarify;
    if (!clarify) return;
    setPendingClarify(null);
    setActionBusy(true);
    try {
      const r = await window.tourkitAuth.authedFetch('/api/v1/assistant/action/resolve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          actionId: clarify.actionId, action: clarify.action,
          params: clarify.params || {}, field: clarify.field, chosenId,
          chosenLabel, chosenHint   // mang tên + SĐT của bản ghi đã chọn theo (vd lịch hẹn cần SĐT khách)
        })
      });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { pushToast(j.error || 'Không xử lý được lựa chọn', 'error'); return; }
      if (j.kind === 'action-proposal') {
        setPendingProposal(j.proposal);
      } else if (j.kind === 'action-clarify') {
        setPendingClarify(j.clarify);
      } else if (j.kind === 'action-result') {
        const result = j.result || {};
        setMessages(m => [...m, { role: 'assistant', content: result.message || 'Đã thực hiện.', streaming: false }]);
        if (result.data) setPanelData(result.data);
      }
    } catch (e) {
      pushToast('Lỗi xử lý lựa chọn: ' + e.message, 'error');
    } finally {
      setActionBusy(false);
    }
  }

  // ─── Suggestions: 4 quick + collapsible toàn bộ 17 tool theo nhóm ─────────
  // Quick chips (luôn hiện) = 4 câu hỏi đại diện cho 4 nhóm phổ biến nhất
  const suggestions = [
    { q: 'Doanh thu tháng này',              icon: 'dollar' },
    { q: 'Top khách hàng',                    icon: 'star' },
    { q: 'Cơ cấu nguồn khách Marketing năm nay', icon: 'chart' },
    { q: 'Tour sắp khởi hành',                icon: 'plane' },
  ];

  // Toàn bộ gợi ý phân theo 7 nhóm — tương ứng 17 tool của AI catalog.
  // Icon CHỈ dùng tên có trong wwwroot/lib/icons.jsx (verified): arrowLeft/arrowRight/
  // bed/bell/book/bus/calendar/camera/chart/check/checkCircle/chevron*/clock/close/copy/
  // dollar/download/drag/edit/eye/grip/info/list/mail/maximize/minus/more/paper/phone/
  // pin/plane/plus/qr/refresh/save/search/share/shield/sliders/sparkle/star/trash/trend/
  // user/users/utensils/warning/zap. KHÔNG dùng tên ngoài list này → SVG sẽ trống.
  const allSuggestions = [
    { group: '💰 Tài chính', items: [
      { q: 'Doanh thu tháng này',                                  icon: 'dollar' },
      { q: 'So sánh doanh thu tháng này với tháng trước',          icon: 'trend' },
      { q: 'Chi tiết tài chính tháng 6/2026 (12 chỉ số)',          icon: 'chart' },
      { q: 'Dòng tiền 30 ngày qua theo ngày',                      icon: 'list' },
    ]},
    { group: '👥 Khách hàng', items: [
      { q: 'Top 10 khách hàng tháng này',                          icon: 'star' },
      { q: 'Khách hàng chưa chăm sóc 30 ngày',                     icon: 'warning' },
      { q: 'Khách sinh nhật tháng này',                            icon: 'sparkle' },
      { q: 'Lịch hẹn CSKH tuần này',                               icon: 'calendar' },
    ]},
    { group: '📊 Marketing', items: [
      { q: 'Cơ cấu nguồn khách năm 2026',                          icon: 'chart' },
      { q: 'Nguồn khách marketing tháng 5/2026',                   icon: 'share' },
    ]},
    { group: '🧳 Sản phẩm Tour', items: [
      { q: 'Tour sắp khởi hành',                                   icon: 'plane' },
      { q: 'Danh sách tour FIT sắp chạy',                          icon: 'bus' },
      { q: 'Tour Visa tháng này',                                  icon: 'paper' },
      { q: 'Tour thị trường Nội địa Miền Nam',                     icon: 'pin' },
    ]},
    { group: '💼 Bán hàng', items: [
      { q: 'Cơ hội bán hàng đang chờ xử lý',                       icon: 'mail' },
      { q: 'Top seller doanh số cao nhất tháng này',               icon: 'star' },
      { q: 'Lead từ Pancake tuần này',                             icon: 'zap' },
    ]},
    { group: '🏢 Hiệu suất', items: [
      { q: 'Hiệu suất nhân viên tháng này',                        icon: 'users' },
      { q: 'Tỉ lệ chốt đơn theo nhân viên tháng này',              icon: 'trend' },
      { q: 'Chi nhánh nào doanh số cao nhất quý 2/2026?',          icon: 'shield' },
      { q: 'Dòng sản phẩm nào lãi nhất tháng này?',                icon: 'sparkle' },
      { q: 'Thị trường nào doanh thu cao nhất năm nay?',           icon: 'pin' },
    ]},
    { group: '✅ Quản lý', items: [
      { q: 'Công việc cần làm hôm nay',                            icon: 'check' },
      { q: 'Phiếu chi chờ duyệt',                                  icon: 'paper' },
      { q: 'Thông báo cần xử lý',                                  icon: 'bell' },
    ]},
  ];
  const [expandedSuggest, setExpandedSuggest] = _aS(false);

  const panelTitle = pendingProposal ? pendingProposal.title
    : pendingClarify ? 'Cần làm rõ trước khi thực hiện'
    : panelData ? (panelData.title || 'Cơ cấu số liệu') : null;

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
                <img className="asst-avatar" src="/lib/trav-ai.png" alt="TRAV-AI"
                  onError={e => { e.target.src = '/lib/masco-ai.png'; }} />
                <div className="asst-greet-bubble">
                  <p><b>Xin chào!</b> Tôi là <b>TRAV-AI</b> — Trợ lý số liệu của bạn.</p>
                  <p>Tôi đã nạp toàn bộ dữ liệu vận hành du lịch năm 2026:
                    <b> Tài chính, Hiệu suất chi nhánh, Doanh số theo Sản phẩm, Thị trường</b> và <b>Nguồn khách Marketing</b>.</p>
                  <p>Bạn muốn tôi đọc và trực quan hóa báo cáo nào? Bấm gợi ý hoặc gõ câu hỏi bên dưới.</p>
                </div>
              </div>
            )}
            {messages.map((m, i) => (
              <div key={i} className={`asst-msg ${m.role} ${m.error ? 'error' : ''}`}>
                {m.role === 'assistant' && (
                  <img className="asst-avatar" src="/lib/trav-ai.png" alt="TRAV-AI"
                    onError={e => { e.target.src = '/lib/masco-ai.png'; }} />
                )}
                <div className="asst-msg-body">
                  {m.role === 'assistant' && m.tool && m.tool !== 'none' &&
                    <span className="asst-tool-tag">{m.tool}</span>}
                  <div className="asst-bubble">
                    {m.content
                      ? m.content
                      : (m.streaming ? <TypingDots stage={stage} /> : '')}
                  </div>
                  {m.trace && <TraceView trace={m.trace} />}
                </div>
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
            {/* Hidden file input — kích bằng paperclip button */}
            <input ref={fileInputRef} type="file" accept="audio/*,.mp3,.m4a,.wav,.webm,.ogg,.flac,.mp4"
              style={{display: 'none'}} onChange={handleFileUpload} />

            {recState === 'recording' ? (
              <>
                {/* Recording UI: 2 nút Cancel/Stop + timer pill thay thế input */}
                <button className="asst-rec-cancel" onClick={cancelRecording} title="Hủy ghi âm">
                  <Icon name="close" size={16} />
                </button>
                <div className="asst-rec-bar">
                  <span className="asst-rec-pulse" />
                  <span className="asst-rec-time">{fmtElapsed(recElapsed)}</span>
                  <span className="asst-rec-hint">Đang ghi… nói rõ vào mic</span>
                </div>
                <button className="asst-rec-stop" onClick={stopRecording} title="Dừng ghi + transcribe">
                  <Icon name="check" size={16} stroke={2.4} />
                </button>
              </>
            ) : (
              <>
                <button className="asst-icon-btn" onClick={() => fileInputRef.current?.click()}
                  disabled={loading || recState === 'uploading'}
                  title="Upload audio file (mp3/wav/m4a... ≤25MB)">
                  <Icon name="paperclip" size={16} stroke={2.2} />
                </button>
                <button className="asst-icon-btn" onClick={startRecording}
                  disabled={loading || recState === 'uploading'}
                  title="Ghi âm (mic browser) — bấm để bắt đầu"
                  aria-label="Ghi âm">
                  {recState === 'uploading'
                    ? <span className="asst-spinner" />
                    : <Icon name="mic" size={16} stroke={2.2} />}
                </button>
                <input
                  className="asst-input"
                  placeholder={recState === 'uploading'
                    ? 'Đang nhận diện audio…'
                    : 'Hỏi AI phân tích báo cáo du lịch… (nhấn Enter để gửi · 🎤 ghi âm · 📎 upload)'}
                  value={input}
                  onChange={e => setInput(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') send(); }}
                  disabled={loading || recState === 'uploading'}
                />
                <button className="asst-send" onClick={send} disabled={loading || recState === 'uploading' || !input.trim()}>
                  <Icon name="arrowRight" size={16} stroke={2.4} />
                </button>
              </>
            )}
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
            {(panelData || pendingProposal || pendingClarify) && <div className="asst-slate-ic"><Icon name="chart" size={18} /></div>}
          </div>
          <DataPanel
            data={panelData} onAsk={(q) => send(q)}
            proposal={pendingProposal} clarify={pendingClarify}
            actionBusy={actionBusy}
            onConfirmProposal={confirmAction}
            onCancelProposal={() => setPendingProposal(null)}
            onChooseClarify={onClarifyChoose}
          />
        </section>
      </div>
    </main>
  );
}

window.AssistantPage = AssistantPage;
