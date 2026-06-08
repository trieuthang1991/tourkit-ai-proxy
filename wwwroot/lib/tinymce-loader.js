// wwwroot/lib/tinymce-loader.js
// Lazy load TinyMCE chỉ khi component RichEditor lần đầu mount.
// Tiết kiệm ~5MB runtime memory cho user không soạn mail.
//
// Usage trong React useEffect:
//   await window.loadTinyMCE();
//   window.tinymce.init({ ... });
//
// Idempotent — gọi nhiều lần OK, chỉ inject script 1 lần. Promise được cache.

(function () {
  let _promise = null;

  window.loadTinyMCE = function () {
    if (window.tinymce) return Promise.resolve();
    if (_promise) return _promise;
    _promise = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = 'lib/tinymce/tinymce.min.js';
      s.referrerPolicy = 'origin';
      s.onload = () => resolve();
      s.onerror = () => {
        _promise = null; // cho retry lần sau
        reject(new Error('Không tải được TinyMCE'));
      };
      document.head.appendChild(s);
    });
    return _promise;
  };
})();
