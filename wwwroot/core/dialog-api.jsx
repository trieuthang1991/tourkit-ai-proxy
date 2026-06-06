// core/dialog-api.jsx — API toàn cục thay window.confirm/alert/prompt
// Render qua Dialog có sẵn (React Portal vào body), Promise-based:
//   await window.appConfirm("Xóa hồ sơ này?")     → boolean
//   await window.appAlert("Đã gửi thành công")    → void
//   await window.appPrompt("Tên mới?", "ABC")     → string | null

(function () {
  'use strict';
  const { useState, useEffect } = React;

  // ─── Host component — gắn 1 lần vào body ─────────────────────────────────
  function DialogHost() {
    const [queue, setQueue] = useState([]);   // [{ id, kind, props, resolve }]
    const cur = queue[0];

    // Expose API ra window 1 lần khi mount
    useEffect(() => {
      const open = (kind, props) => new Promise(resolve => {
        const id = Math.random().toString(36).slice(2);
        setQueue(q => [...q, { id, kind, props, resolve }]);
      });
      const close = (val) => {
        if (cur) { cur.resolve(val); setQueue(q => q.slice(1)); }
      };

      window.appAlert   = (message, opts = {}) =>
        open('alert', { title: opts.title || 'Thông báo', eyebrow: opts.eyebrow || '', message, icon: opts.icon || 'info', confirmLabel: opts.confirmLabel || 'OK' });
      window.appConfirm = (message, opts = {}) =>
        open('confirm', { title: opts.title || 'Xác nhận', eyebrow: opts.eyebrow || 'XÁC NHẬN', message, icon: opts.icon || 'warning', confirmLabel: opts.confirmLabel || 'Đồng ý', cancelLabel: opts.cancelLabel || 'Hủy', danger: !!opts.danger });
      window.appPrompt  = (message, defaultValue = '', opts = {}) =>
        open('prompt', { title: opts.title || 'Nhập thông tin', eyebrow: opts.eyebrow || '', message, defaultValue, icon: opts.icon || 'edit', placeholder: opts.placeholder || '' });
    }, [cur]);

    if (!cur) return null;

    if (cur.kind === 'alert') {
      return <window.ConfirmDialog open={true}
        title={cur.props.title} eyebrow={cur.props.eyebrow} message={cur.props.message}
        confirmLabel={cur.props.confirmLabel} cancelLabel={null}
        onConfirm={() => { cur.resolve(true); setQueue(q => q.slice(1)); }}
        onClose={() => { cur.resolve(true); setQueue(q => q.slice(1)); }} />;
    }
    if (cur.kind === 'confirm') {
      return <window.ConfirmDialog open={true}
        title={cur.props.title} eyebrow={cur.props.eyebrow} message={cur.props.message}
        confirmLabel={cur.props.confirmLabel} cancelLabel={cur.props.cancelLabel}
        danger={cur.props.danger}
        onConfirm={() => { cur.resolve(true); setQueue(q => q.slice(1)); }}
        onClose={() => { cur.resolve(false); setQueue(q => q.slice(1)); }} />;
    }
    if (cur.kind === 'prompt') {
      return <window.PromptDialog open={true}
        title={cur.props.title} eyebrow={cur.props.eyebrow}
        placeholder={cur.props.placeholder} initialValue={cur.props.defaultValue}
        onSubmit={(v) => { cur.resolve(v); setQueue(q => q.slice(1)); }}
        onClose={() => { cur.resolve(null); setQueue(q => q.slice(1)); }} />;
    }
    return null;
  }

  // Mount DialogHost 1 lần khi DOM ready
  function ensureMounted() {
    if (document.getElementById('app-dialog-host')) return;
    const node = document.createElement('div');
    node.id = 'app-dialog-host';
    document.body.appendChild(node);
    if (window.ReactDOM?.createRoot) {
      ReactDOM.createRoot(node).render(<DialogHost />);
    } else {
      ReactDOM.render(<DialogHost />, node);
    }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', ensureMounted);
  } else {
    ensureMounted();
  }
})();
