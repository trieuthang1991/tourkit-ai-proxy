// components/table-scroll.jsx — Bọc bảng RỘNG: thanh cuộn ngang DÍNH đáy viewport (position: sticky)
// đồng bộ với scroll của bảng → kéo ngang được MỌI LÚC, không phải cuộn xuống tận cuối bảng.
//
// Vấn đề: <div overflow-x:auto> để native scrollbar ở ĐÁY bảng → bảng 100+ dòng phải cuộn xuống
// cuối mới chạm thanh kéo ngang. Component này thêm 1 thanh cuộn "proxy" sticky bottom:0 (ẩn native
// của body) → luôn thấy + kéo ngang ngay cả khi đang xem giữa bảng.
//
// Usage:  <window.TKTableScroll><table>…</table></window.TKTableScroll>
//   - className/style: áp cho khung ngoài (mặc định đã có viền + bo góc + nền như card data-list).
//
// Tự ẩn thanh proxy khi bảng KHÔNG tràn ngang (scrollWidth <= clientWidth).

(function () {
  'use strict';
  const { useRef, useEffect, useState, useCallback } = React;

  function TKTableScroll({ children, className, style }) {
    const bodyRef = useRef(null);   // khung cuộn thật (overflow-x), native scrollbar bị ẩn
    const barRef  = useRef(null);   // thanh proxy sticky bottom
    const [scrollW, setScrollW] = useState(0);
    const [overflow, setOverflow] = useState(false);
    const lock = useRef(false);     // chống vòng lặp khi sync 2 chiều

    const measure = useCallback(() => {
      const el = bodyRef.current; if (!el) return;
      const sw = el.scrollWidth, cw = el.clientWidth;
      setScrollW(sw);
      setOverflow(sw - cw > 1);
    }, []);

    // Đo lại khi: mount, đổi children (data/cột), resize khung hoặc cửa sổ.
    useEffect(() => {
      measure();
      const el = bodyRef.current; if (!el) return;
      let ro;
      if (window.ResizeObserver) {
        ro = new ResizeObserver(measure);
        ro.observe(el);
        if (el.firstElementChild) ro.observe(el.firstElementChild);
      }
      window.addEventListener('resize', measure);
      return () => { if (ro) ro.disconnect(); window.removeEventListener('resize', measure); };
    }, [measure, children]);

    // Đồng bộ scrollLeft 2 chiều (lock tránh body→bar→body lặp).
    const syncFromBody = () => {
      if (lock.current) return; lock.current = true;
      if (barRef.current && bodyRef.current) barRef.current.scrollLeft = bodyRef.current.scrollLeft;
      lock.current = false;
    };
    const syncFromBar = () => {
      if (lock.current) return; lock.current = true;
      if (barRef.current && bodyRef.current) bodyRef.current.scrollLeft = barRef.current.scrollLeft;
      lock.current = false;
    };

    return (
      <div className={'tk-tscroll' + (className ? ' ' + className : '')} style={style}>
        <div className="tk-tscroll-body" ref={bodyRef} onScroll={syncFromBody}>
          {children}
        </div>
        {overflow && (
          <div className="tk-tscroll-bar" ref={barRef} onScroll={syncFromBar} aria-hidden="true">
            <div style={{ width: scrollW, height: 1 }} />
          </div>
        )}
      </div>
    );
  }

  window.TKTableScroll = TKTableScroll;
})();
