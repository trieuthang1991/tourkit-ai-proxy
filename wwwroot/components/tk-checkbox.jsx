// components/tk-checkbox.jsx
// Checkbox tùy biến — refined B2B, brand orange. Replaces native <input type="checkbox">.
//
// Usage:
//   <window.TKCheckbox checked={x} onChange={setX} />
//   <window.TKCheckbox checked={all} indeterminate={some && !all} onChange={toggleAll} />
//   <window.TKCheckbox checked={x} onChange={setX} label="Bỏ qua cache" />
//   <window.TKCheckbox size="sm" ... />     // size: 'sm' (14px) | 'md' (18px, default)
//
// Note: native <input> giữ a11y (Tab focus, Space toggle, screen-reader announce);
// SVG icon vẽ trực tiếp trong label — không phụ thuộc font/data-URI.

(function () {
  'use strict';
  const { useRef, useEffect } = React;

  function TKCheckbox({ checked, indeterminate, onChange, disabled, size, label, ariaLabel, onClick }) {
    const ref = useRef(null);

    // Native indeterminate flag chỉ set được qua JS — useEffect đồng bộ với prop
    useEffect(() => {
      if (ref.current) ref.current.indeterminate = !!indeterminate;
    }, [indeterminate]);

    const cls = [
      'tk-check',
      size === 'sm' ? 'tk-check-sm' : '',
      checked ? 'is-checked' : '',
      indeterminate ? 'is-indeterminate' : '',
      disabled ? 'is-disabled' : '',
      label ? 'has-label' : '',
    ].filter(Boolean).join(' ');

    return (
      <label className={cls} onClick={onClick}>
        <input
          ref={ref}
          type="checkbox"
          checked={!!checked}
          disabled={disabled}
          onChange={(e) => onChange && onChange(e.target.checked, e)}
          aria-label={ariaLabel}
        />
        <span className="tk-check-box" aria-hidden="true">
          {/* Checkmark — vẽ inline SVG, stroke-dasharray để animate draw */}
          <svg className="tk-check-tick" viewBox="0 0 16 16" width="12" height="12">
            <path d="M3.5 8.5L6.8 11.7L12.7 5" fill="none" stroke="currentColor"
              strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {/* Indeterminate dash — bar ngang, hiện thay tick khi indeterminate=true */}
          <span className="tk-check-dash" />
        </span>
        {label && <span className="tk-check-label">{label}</span>}
      </label>
    );
  }

  window.TKCheckbox = TKCheckbox;
})();
