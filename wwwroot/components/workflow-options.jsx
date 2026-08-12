// components/workflow-options.jsx — NGUỒN DUY NHẤT cho schema + ô nhập cấu hình workflow.
//
// Trước đây toàn bộ nằm trong pages/workflows.jsx. Tách ra để trang sơ đồ
// (pages/flow-preview.jsx) sửa được ĐÚNG bộ cấu hình đó, thay vì dựng bộ ô nhập thứ hai —
// hai bộ form cho cùng một cấu hình là kiểu lỗi âm thầm khó tìm (sửa bên này quên bên kia).
//
// Nội dung chuyển sang NGUYÊN VĂN, không đổi hành vi. Thêm workflow/option mới vẫn chỉ cần
// thêm 1 entry vào WORKFLOW_OPTIONS — cả 2 trang tự render.
//
// ⚠️ File này PHẢI được nạp TRƯỚC pages/workflows.jsx và pages/flow-preview.jsx
//    (thứ tự trong index.html và bundle-entry.js).

(function () {
  const { useState: uS, useEffect: uE } = React;

  // Tần suất chạy (giá trị = SỐ PHÚT lưu xuống backend).
  const INTERVAL_OPTIONS = [
    { value: 5,    label: 'Mỗi 5 phút' },
    { value: 10,   label: 'Mỗi 10 phút' },
    { value: 15,   label: 'Mỗi 15 phút' },
    { value: 30,   label: 'Mỗi 30 phút' },
    { value: 60,   label: 'Mỗi 1 giờ' },
    { value: 180,  label: 'Mỗi 3 giờ' },
    { value: 360,  label: 'Mỗi 6 giờ' },
    { value: 720,  label: 'Mỗi 12 giờ' },
    { value: 1440, label: 'Mỗi ngày' },
  ];

  const MAIL_CATEGORIES = [
    { value: 'hoi_dat_tour', label: 'Hỏi/đặt tour' },
    { value: 'xin_bao_gia',  label: 'Xin báo giá' },
    { value: 'xac_nhan',     label: 'Xác nhận' },
    { value: 'khieu_nai',    label: 'Khiếu nại' },
  ];
  const MAIL_TONES = [
    { value: 'lich_su',    label: 'Lịch sự' },
    { value: 'than_thien', label: 'Thân thiện' },
    { value: 'dam_phan',   label: 'Đàm phán' },
    { value: 'xin_loi',    label: 'Xin lỗi' },
  ];

  // Schema option ĐỘNG per-workflow (client). Thêm workflow/option mới = thêm 1 entry — UI tự render.
  // type: 'bool'→toggle | 'select'→dropdown | 'multi'→chip nhiều lựa chọn. showIf: chỉ hiện khi option kia bật.
  // Khớp shape OptionsJson backend (mail-auto-sync: {autoReply, replyMode, replyCategories[], replyTone}).
  const WORKFLOW_OPTIONS = {
    'mail-auto-sync': [
      { key: 'autoReply', type: 'bool', label: 'Tự động trả lời',
        hint: 'AI tự soạn/gửi trả lời cho email mới theo cấu hình dưới đây.' },
      { key: 'replyMode', type: 'select', label: 'Chế độ', showIf: 'autoReply', default: 'draft',
        options: [
          { value: 'draft', label: 'Soạn sẵn (người duyệt rồi gửi)' },
          { value: 'send',  label: 'Gửi thẳng tự động' },
        ],
        hint: 'Soạn sẵn = an toàn (nháp chờ NV duyệt). Gửi thẳng = AI gửi luôn cho khách.' },
      { key: 'replyCategories', type: 'multi', label: 'Áp dụng nhóm', showIf: 'autoReply',
        default: ['hoi_dat_tour', 'xin_bao_gia', 'xac_nhan'],
        options: MAIL_CATEGORIES,
        hint: 'Chỉ auto-reply email thuộc nhóm đã chọn. Khiếu nại nên để người xử lý.' },
      { key: 'replyTone', type: 'select', label: 'Giọng văn', showIf: 'autoReply', default: 'lich_su',
        options: MAIL_TONES },
    ],
    'deal-auto-review': [
      { key: 'statuses', type: 'multi', dynamic: 'dealStatuses', label: 'Trạng thái áp dụng', default: [], required: true,
        hint: 'Chọn ít nhất 1 trạng thái deal mà workflow sẽ xử lý.' },
      { key: 'createdWithinDays', type: 'number', label: 'Chỉ deal tạo trong (ngày)', default: 30, min: 1, max: 365,
        hint: 'Chỉ xử lý deal được tạo trong khoảng ngày gần đây này. Deal cũ hơn được bỏ qua.' },
      // ── ② Chấm điểm cơ hội (AI) — công tắc chính autoReview ──
      { key: 'autoReview', type: 'bool', label: 'AI tự chấm điểm cơ hội mới', default: true,
        hint: 'Bật để AI tự cho điểm khả năng chốt từng cơ hội mới. Tắt hẳn phần chấm điểm (vẫn có thể bật riêng cảnh báo nguội bên dưới).' },
      { key: 'reviewMax', type: 'number', label: 'Tối đa cơ hội chấm mỗi lượt', showIf: 'autoReview', default: 20, min: 1, max: 100,
        hint: 'Mỗi lượt chạy chấm tối đa bao nhiêu cơ hội (gồm cả chấm mới lẫn chấm lại).' },
      { key: 'reReview', type: 'bool', label: 'Chấm lại cơ hội cũ định kỳ', showIf: 'autoReview', default: true,
        hint: 'Bật để định kỳ chấm lại cơ hội đã chấm (theo chu kỳ bên dưới) khi nội dung có thay đổi. Tắt = chỉ chấm cơ hội mới, không chấm lại.' },
      { key: 'reReviewDays', type: 'number', label: 'Chấm lại sau mỗi (ngày)', showIf: ['autoReview', 'reReview'], default: 10, min: 1, max: 365,
        hint: 'Cơ hội đã chấm được xét chấm lại sau tối thiểu bao nhiêu ngày (và chỉ khi nội dung đổi). 7 ≈ mỗi tuần, 10 = mặc định, 30 ≈ mỗi tháng.' },
      { key: 'maxAutoReviews', type: 'number', label: 'Tối đa số lần chấm lại / cơ hội', showIf: ['autoReview', 'reReview'], default: 5, min: 1, max: 50,
        hint: 'Mỗi cơ hội được chấm lại tối đa bao nhiêu lần, tránh chấm đi chấm lại mãi một cơ hội.' },
      // ── ③ Cảnh báo cơ hội nguội — công tắc chính alertCooling ──
      { key: 'alertCooling', type: 'bool', label: 'Gửi cảnh báo cơ hội nguội', default: true,
        hint: 'Bật để tự phát hiện cơ hội đang mở nhưng lâu không chăm sóc ("nguội") và gửi email nhắc nhân viên phụ trách. Tắt = bỏ hẳn phần cảnh báo (không quét, không gửi).' },
      { key: 'coolingStatuses', type: 'multi', dynamic: 'dealStatuses', label: 'Trạng thái tính "nguội"', showIf: 'alertCooling', default: [],
        hint: 'Chỉ cảnh báo nguội cho cơ hội ở các trạng thái này. Để trống = mọi trạng thái đang mở (tự loại trừ đã chốt/hủy). Cũng áp cho badge "nguội" trên trang Cơ hội.' },
      // Mặc định 3 ngày (trước là 7) — PHẢI khớp DealAutoReviewOptions.Parse bên C#,
      // lệch nhau thì giao diện hiện một đằng, hệ thống chạy một nẻo.
      { key: 'coolingDays', type: 'number', label: 'Coi là "nguội" sau (ngày)', showIf: 'alertCooling', default: 3, min: 1, max: 90,
        hint: 'Cơ hội đang mở mà quá số ngày này không ai chăm sóc thì coi là "nguội" và được đưa vào cảnh báo.' },
      { key: 'minWinRateToNotify', type: 'number', label: 'Chỉ cảnh báo khi % chốt từ', showIf: 'alertCooling', default: 0, min: 0, max: 100,
        hint: 'Chỉ cảnh báo những cơ hội có khả năng chốt từ mức % này trở lên. Để 0 = cảnh báo mọi cơ hội nguội.' },
      { key: 'maxNotifications', type: 'number', label: 'Tối đa số lần cảnh báo / cơ hội', showIf: 'alertCooling', default: 3, min: 1, max: 20,
        hint: 'Mỗi cơ hội chỉ gửi cảnh báo tối đa bao nhiêu lần, tránh làm phiền nhân viên.' },
      { key: 'notifyMinGapHours', type: 'number', label: 'Nhắc lại cùng 1 cơ hội sau ít nhất (giờ)', showIf: 'alertCooling', default: 24, min: 1, max: 720,
        hint: 'Sau khi đã cảnh báo một cơ hội, phải chờ đủ số giờ này mới được nhắc lại. Ví dụ 24 = mỗi cơ hội tối đa 1 lần/ngày.' },
    ],
    'customer-auto-review': [
      { key: 'createdWithinDays', type: 'number', label: 'Chỉ khách tạo trong (ngày)', default: 30, min: 1, max: 365,
        hint: 'Chỉ review khách được tạo trong khoảng ngày gần đây này. Khách cũ hơn được bỏ qua ở lần review đầu.' },
      { key: 'reReview', type: 'bool', label: 'Tự động review lại định kỳ', default: true,
        hint: 'Bật để định kỳ chấm lại những khách đã review (theo chu kỳ bên dưới). Tắt = chỉ review khách mới.' },
      { key: 'reReviewDays', type: 'number', label: 'Chấm lại sau mỗi (ngày)', showIf: 'reReview', default: 30, min: 1, max: 365,
        hint: 'Bao lâu thì chấm lại một khách kể từ lần review trước. 30 ngày ≈ mỗi tháng, 90 ≈ mỗi quý, 7 = mỗi tuần.' },
    ],
  };

  // Nhóm option theo chức năng (cho tiêu đề mục trong card) — gọn, dễ quét. key option → tên nhóm.
  const OPTION_GROUPS = {
    'mail-auto-sync': {
      autoReply: 'Tự động trả lời', replyMode: 'Tự động trả lời',
      replyCategories: 'Tự động trả lời', replyTone: 'Tự động trả lời',
    },
    'deal-auto-review': {
      statuses: '① Phạm vi xử lý', createdWithinDays: '① Phạm vi xử lý',
      autoReview: '② Chấm điểm cơ hội (AI)', reviewMax: '② Chấm điểm cơ hội (AI)',
      reReview: '② Chấm điểm cơ hội (AI)', reReviewDays: '② Chấm điểm cơ hội (AI)', maxAutoReviews: '② Chấm điểm cơ hội (AI)',
      alertCooling: '③ Cảnh báo cơ hội nguội', coolingStatuses: '③ Cảnh báo cơ hội nguội', coolingDays: '③ Cảnh báo cơ hội nguội',
      minWinRateToNotify: '③ Cảnh báo cơ hội nguội', maxNotifications: '③ Cảnh báo cơ hội nguội', notifyMinGapHours: '③ Cảnh báo cơ hội nguội',
    },
    'customer-auto-review': {
      createdWithinDays: 'Phạm vi',
      reReview: 'Chu kỳ review lại', reReviewDays: 'Chu kỳ review lại',
    },
  };

  // showIf: string (1 key) HOẶC mảng key (AND — chỉ hiện khi TẤT CẢ key bật). Hỗ trợ toggle lồng
  // (vd option con của "chấm lại" chỉ hiện khi vừa bật "chấm điểm" vừa bật "chấm lại").
  function optVisible(opt, options) {
    if (!opt.showIf) return true;
    const keys = Array.isArray(opt.showIf) ? opt.showIf : [opt.showIf];
    return keys.every(k => !!options[k]);
  }

  // Gom default từ schema → {key: default} để pre-fill options khi user mới bật (tránh gửi mảng rỗng).
  function optionDefaults(type) {
    const out = {};
    (WORKFLOW_OPTIONS[type] || []).forEach(o => { if (o.default !== undefined) out[o.key] = o.default; });
    return out;
  }

  // Field bắt buộc đang trống? (chỉ tính field đang hiện + có dữ liệu để chọn)
  function optEmpty(opt, options, dynOptions) {
    const v = options[opt.key];
    if (opt.type === 'multi') {
      const opts = opt.dynamic ? ((dynOptions || {})[opt.dynamic] || []) : (opt.options || []);
      return opts.length > 0 && (!Array.isArray(v) || v.length === 0);   // chỉ bắt buộc khi đã có list để chọn
    }
    if (opt.type === 'numbers') return !Array.isArray(v) || v.length === 0;
    return v == null || v === '';
  }

  // ─── MultiSelect (select2-style) — chip + dropdown checklist cho options động ──────

  function MultiSelectDropdown({ options, value, onChange, placeholder, loading }) {
    const [open, setOpen] = uS(false);
    const ref = React.useRef(null);
    uE(() => {
      if (!open) return;
      const h = e => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
      document.addEventListener('mousedown', h);
      return () => document.removeEventListener('mousedown', h);
    }, [open]);
    const sel = Array.isArray(value) ? value : [];
    const labelOf = v => { const o = options.find(x => x.value === v); return o ? o.label : v; };
    const toggle = v => onChange(sel.includes(v) ? sel.filter(x => x !== v) : [...sel, v]);
    return (
      <div className="wf-ms" ref={ref}>
        <div className={'wf-ms-control' + (open ? ' open' : '')} onClick={() => setOpen(o => !o)}>
          <div className="wf-ms-tags">
            {sel.length === 0
              ? <span className="wf-ms-ph">{loading ? 'Đang tải…' : (placeholder || 'Tất cả')}</span>
              : sel.map(v => (
                <span className="wf-ms-tag" key={v}>
                  {labelOf(v)}
                  <span className="wf-ms-x" onClick={e => { e.stopPropagation(); toggle(v); }}>×</span>
                </span>
              ))}
          </div>
          <span className="wf-ms-chev"><Icon name={open ? 'chevronUp' : 'chevronDown'} size={14} /></span>
        </div>
        {open && (
          <div className="wf-ms-menu">
            {options.length === 0
              ? <div className="wf-ms-empty">{loading ? 'Đang tải…' : 'Chưa lấy được trạng thái — kiểm tra kết nối CRM / tài khoản.'}</div>
              : options.map(o => (
                <label key={o.value} className={'wf-ms-item' + (sel.includes(o.value) ? ' on' : '')}>
                  <input type="checkbox" checked={sel.includes(o.value)} onChange={() => toggle(o.value)} />
                  <span>{o.label}</span>
                </label>
              ))}
          </div>
        )}
      </div>
    );
  }

  // ─── Ô nhập của 1 option, theo type (bool/select/multi/number/numbers) ─────────
  // Trước là hàm renderControl() nằm trong WorkflowCard (đóng gói options/setOptions/dynOptions).
  // Nay nhận qua props để trang nào cũng dùng được. Markup + class GIỮ NGUYÊN.

  function OptionControl({ opt, options, setOptions, dynOptions, dynLoading }) {
    dynOptions = dynOptions || {};
    dynLoading = dynLoading || {};

    if (opt.type === 'bool') return (
      <div className="workflows-toggle-wrap">
        <label className="workflows-toggle">
          <input type="checkbox" checked={!!options[opt.key]}
            onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.checked }))} />
          <span className="workflows-toggle-track" />
        </label>
        <span className="workflows-toggle-label">{options[opt.key] ? 'Bật' : 'Tắt'}</span>
      </div>
    );
    if (opt.type === 'select') return (
      <select className="workflows-select workflows-opt-input"
        value={options[opt.key] || opt.options[0].value}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value }))}>
        {opt.options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    );
    if (opt.type === 'multi') {
      // Options ĐỘNG (vd trạng thái deal từ CRM) → multi-select dropdown (select2-style).
      if (opt.dynamic) return (
        <MultiSelectDropdown
          options={dynOptions[opt.dynamic] || []}
          value={options[opt.key]}
          loading={!!dynLoading[opt.dynamic]}
          placeholder="Tất cả trạng thái"
          onChange={vals => setOptions(o => ({ ...o, [opt.key]: vals }))} />
      );
      // Options TĨNH (vd nhóm mail) → chips.
      return (
        <div className="workflows-chips">
          {opt.options.map(o => {
            const arr = Array.isArray(options[opt.key]) ? options[opt.key] : [];
            const on = arr.includes(o.value);
            return (
              <label key={o.value} className={'workflows-chip' + (on ? ' on' : '')}>
                <input type="checkbox" checked={on} style={{ display: 'none' }}
                  onChange={() => setOptions(prev => {
                    const cur = Array.isArray(prev[opt.key]) ? prev[opt.key] : [];
                    return { ...prev, [opt.key]: on ? cur.filter(x => x !== o.value) : [...cur, o.value] };
                  })} />
                {o.label}
              </label>
            );
          })}
        </div>
      );
    }
    if (opt.type === 'number') return (
      <input type="number" className="workflows-select workflows-opt-num"
        min={opt.min} max={opt.max}
        value={options[opt.key] ?? opt.default ?? 0}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value === '' ? 0 : Number(e.target.value) }))} />
    );
    if (opt.type === 'numbers') return (
      <input type="text" className="workflows-select workflows-opt-input"
        placeholder="để trống = tất cả"
        value={(Array.isArray(options[opt.key]) ? options[opt.key] : []).join(', ')}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value.split(',').map(x => parseInt(x.trim(), 10)).filter(n => !isNaN(n) && n > 0) }))} />
    );
    return null;
  }

  window.tourkitWorkflowOptions = {
    INTERVAL_OPTIONS, MAIL_CATEGORIES, MAIL_TONES,
    WORKFLOW_OPTIONS, OPTION_GROUPS,
    optVisible, optionDefaults, optEmpty,
    MultiSelectDropdown, OptionControl,
  };
})();
