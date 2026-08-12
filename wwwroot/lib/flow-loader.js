// lib/flow-loader.js — Lazy-load React Flow (@xyflow/react 12.11.3, MIT, self-host) CHỈ khi cần.
// Cùng lối với lib/chart-loader.js: 183KB JS + 18KB CSS, chỉ trang "Sơ đồ luồng" dùng
// → KHÔNG nạp ở index.html cho mọi trang.
//
// API: window.ensureReactFlow() → Promise<window.ReactFlow>. Gọi nhiều lần an toàn (memoize).
//
// LƯU Ý bản UMD: nó cần 3 global — React, ReactDOM và `jsxRuntime` (react/jsx-runtime).
// React UMD 18 KHÔNG kèm jsx-runtime, nên phải tự shim. Shim dưới đây map jsx/jsxs về
// React.createElement và GIỮ NGUYÊN children trong props (không truyền children thành đối số
// thứ 3) — nếu truyền kiểu đó, mảng con sẽ bị React cảnh báo thiếu "key" hàng loạt.

(function () {
  'use strict';
  var JS  = 'lib/vendor/reactflow-12.11.3.umd.js';
  var CSS = 'lib/vendor/reactflow-12.11.3.css';
  var promise = null;

  // Shim react/jsx-runtime → React.createElement (chỉ cài khi chưa có).
  function installJsxRuntime() {
    if (window.jsxRuntime || !window.React) return;
    var React = window.React;
    function make(type, props, key) {
      var p = {};
      for (var k in props) if (Object.prototype.hasOwnProperty.call(props, k)) p[k] = props[k];
      if (key !== undefined && key !== null) p.key = key;
      return React.createElement(type, p);   // children ở lại trong props — đúng ngữ nghĩa runtime tự động
    }
    window.jsxRuntime = { jsx: make, jsxs: make, jsxDEV: make, Fragment: React.Fragment };
  }

  function loadCss() {
    if (document.querySelector('link[data-reactflow]')) return;
    var l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = CSS;
    l.setAttribute('data-reactflow', '1');
    document.head.appendChild(l);
  }

  window.ensureReactFlow = function () {
    if (window.ReactFlow && window.ReactFlow.ReactFlow) return Promise.resolve(window.ReactFlow);
    if (promise) return promise;

    promise = new Promise(function (resolve, reject) {
      installJsxRuntime();
      if (!window.React || !window.ReactDOM) {
        reject(new Error('React/ReactDOM chưa sẵn sàng — flow-loader phải chạy sau react UMD'));
        return;
      }
      loadCss();
      var s = document.createElement('script');
      s.src = JS;
      s.async = true;
      s.onload = function () {
        if (window.ReactFlow && window.ReactFlow.ReactFlow) resolve(window.ReactFlow);
        else reject(new Error('React Flow tải xong nhưng window.ReactFlow không hợp lệ'));
      };
      s.onerror = function () {
        promise = null;   // cho retry lần sau
        reject(new Error('Không tải được React Flow: ' + JS));
      };
      document.head.appendChild(s);
    });
    return promise;
  };
})();
