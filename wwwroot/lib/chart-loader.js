// lib/chart-loader.js — Lazy-load Chart.js (UMD self-host) CHỈ khi cần.
// Chart.js ~201KB nhưng chỉ trang "Trợ lý số liệu" (assistant.jsx ChartView) dùng.
// Trước đây eager-load ở index.html → mọi trang (landing/NCC/khách hàng…) tải dư 201KB
// + block render. Giờ chỉ tải khi ChartView mount lần đầu.
//
// API: window.ensureChart() → Promise<typeof window.Chart>. Gọi nhiều lần an toàn
// (memoize promise → chỉ inject <script> 1 lần). Resolve khi window.Chart sẵn sàng.

(function () {
  'use strict';
  var SRC = 'lib/vendor/chart-4.4.6.umd.min.js';
  var promise = null;

  window.ensureChart = function () {
    // Đã có sẵn (vd dev mode lỡ load, hoặc lần gọi trước xong) → resolve ngay.
    if (window.Chart) return Promise.resolve(window.Chart);
    if (promise) return promise;

    promise = new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = SRC;
      s.async = true;
      s.onload = function () {
        if (window.Chart) resolve(window.Chart);
        else reject(new Error('Chart.js load xong nhưng window.Chart không có'));
      };
      s.onerror = function () {
        promise = null;  // cho phép retry lần sau
        reject(new Error('Không tải được Chart.js: ' + SRC));
      };
      document.head.appendChild(s);
    });
    return promise;
  };
})();
