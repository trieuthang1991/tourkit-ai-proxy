// components/chon-nguoi.jsx — ô chọn MỘT người có ô tìm kiếm (window.ChonNguoi).
//
// VÌ SAO KHÔNG DÙNG <select> THƯỜNG. Đo trên staging 08/09/2026: công ty đang có **108 nhân
// viên**, tên dài nhất 57 ký tự, và ô cũ bị chặn ở `max-width: 170px` — tức khoảng 20 ký tự
// hiện ra. Người trực muốn giao việc cho một người cụ thể phải cuộn qua tối đa 108 dòng trong
// một hộp thả xuống của trình duyệt, đọc những cái tên bị cắt cụt. Chọn theo trí nhớ thứ tự,
// không phải theo tên.
//
// BA CHI TIẾT DỄ BỎ QUA, đều lấy từ dữ liệu thật chứ không phải phòng xa:
//
//   1. Tìm phải BỎ DẤU. Người gõ "duyen" phải ra "Kim Duyên" — không ai gõ đủ dấu khi đang vội.
//   2. Tên có ký tự Unicode trang trí. Trong 108 người có "𝐁𝐓𝐎𝐔𝐑 & 𝐌𝐄𝐃𝐈𝐀" viết bằng khối
//      Mathematical Bold. `normalize('NFD')` KHÔNG đụng tới khối này, nên gõ "btour" sẽ không
//      ra gì. Phải dùng `NFKD` — nó mới hạ 𝐁 về B. Bẫy này chỉ lộ ra khi thử bằng danh sách
//      thật; danh sách bịa toàn tên thường thì không bao giờ thấy.
//   3. Có tên TRÙNG NHAU ("test phân quyền edit 23" xuất hiện hai lần). Hai dòng giống hệt thì
//      người chọn không biết bấm dòng nào — nên dòng trùng mới kèm thêm mã số; những dòng còn
//      lại để trống cho đỡ nhiễu, vì mã nhân viên không có nghĩa gì với người dùng.
//
// Dùng ở: khối "Phụ trách" trong hồ sơ khách (hộp thư chat). Cần thêm chỗ khác thì dùng lại,
// đừng chép — đây là control dùng chung, không phải mảnh riêng của hộp thư.
(function () {
  'use strict';

  const { useState, useRef, useEffect, useMemo } = React;

  /**
   * Chuẩn hoá một chuỗi để so khớp khi tìm: bỏ dấu, hạ chữ trang trí về chữ thường, gộp hoa/thường.
   *
   * NFKD (không phải NFD) là chỗ khác biệt — xem chú thích 2 ở đầu file.
   */
  function chuanHoa(s) {
    return String(s || '')
      .normalize('NFKD')
      .replace(/[̀-ͯ]/g, '')   // dấu thanh + dấu mũ đã tách rời sau khi chuẩn hoá
      .replace(/đ/g, 'd').replace(/Đ/g, 'D')
      .toLowerCase()
      .trim();
  }

  /** Hai chữ đầu của tên — làm nhãn tròn thay ảnh, vì CRM không trả ảnh nhân viên. */
  function chuDau(ten) {
    const tu = String(ten || '?').trim().split(/\s+/).filter(Boolean);
    if (tu.length === 0) return '?';
    if (tu.length === 1) return tu[0].slice(0, 2).toUpperCase();
    return (tu[0][0] + tu[tu.length - 1][0]).toUpperCase();
  }

  /**
   * @param {Array<{id:number,name:string}>} danhSach  người chọn được
   * @param {number|null} giaTri      mã người đang chọn
   * @param {(id:number)=>void} onChon  gọi khi người dùng chọn MỘT dòng
   * @param {string} nhan             chữ hiện khi chưa chọn ai
   * @param {boolean} khoa            khoá thao tác (đang gửi lệnh lên máy chủ)
   */
  function ChonNguoi({ danhSach, giaTri, onChon, nhan = 'Chọn người…', khoa = false, autoMo = false }) {
    const [mo, setMo] = useState(false);
    const [tim, setTim] = useState('');
    const [dang, setDang] = useState(0);       // dòng đang được bàn phím trỏ tới
    const boc = useRef(null);
    const oTim = useRef(null);
    const dsRef = useRef(null);

    const ds = danhSach || [];
    const dangChon = ds.find(nv => nv.id === giaTri) || null;

    // Tên nào xuất hiện nhiều hơn một lần thì dòng của nó mới cần thêm mã số để phân biệt.
    const tenTrung = useMemo(() => {
      const dem = new Map();
      ds.forEach(nv => dem.set(nv.name, (dem.get(nv.name) || 0) + 1));
      return new Set([...dem.entries()].filter(([, n]) => n > 1).map(([t]) => t));
    }, [ds]);

    // Lọc theo từ khoá đã bỏ dấu. Tách từ và bắt buộc khớp ĐỦ mọi từ: gõ "duyen sale" phải ra
    // "Kim Duyên Sale" mà không phụ thuộc thứ tự người gõ.
    const locDuoc = useMemo(() => {
      const q = chuanHoa(tim);
      if (!q) return ds;
      const tu = q.split(/\s+/);
      return ds.filter(nv => {
        const t = chuanHoa(nv.name);
        return tu.every(x => t.includes(x));
      });
    }, [ds, tim]);

    // Mở ra thì con trỏ bàn phím về đầu danh sách, và ô tìm nhận focus ngay — không bắt người
    // dùng bấm thêm một lần nữa mới gõ được.
    useEffect(() => {
      if (!mo) return;
      setDang(0);
      const t = setTimeout(() => oTim.current && oTim.current.focus(), 0);
      return () => clearTimeout(t);
    }, [mo]);

    useEffect(() => { if (autoMo) setMo(true); }, [autoMo]);

    // Bấm ra ngoài là đóng. Không có nó thì hộp dính lại cho tới khi bấm đúng nút — kiểu bực
    // mình nhỏ mà gặp mỗi lần.
    useEffect(() => {
      if (!mo) return;
      const ngoai = e => { if (boc.current && !boc.current.contains(e.target)) setMo(false); };
      document.addEventListener('mousedown', ngoai);
      return () => document.removeEventListener('mousedown', ngoai);
    }, [mo]);

    // Cuộn dòng đang trỏ vào tầm nhìn — đi bằng mũi tên qua 108 dòng mà khung không cuộn theo
    // thì con trỏ biến mất khỏi màn hình ngay sau vài lần bấm.
    useEffect(() => {
      if (!mo || !dsRef.current) return;
      const el = dsRef.current.querySelector('[data-dang="1"]');
      if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest' });
    }, [dang, mo]);

    function chon(nv) {
      setMo(false); setTim('');
      if (nv && nv.id !== giaTri) onChon(nv.id);
    }

    function phim(e) {
      if (e.key === 'Escape') { e.preventDefault(); setMo(false); return; }
      if (e.key === 'ArrowDown') { e.preventDefault(); setDang(i => Math.min(i + 1, locDuoc.length - 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setDang(i => Math.max(i - 1, 0)); return; }
      if (e.key === 'Enter') { e.preventDefault(); if (locDuoc[dang]) chon(locDuoc[dang]); }
    }

    return (
      <div className="cn-boc" ref={boc}>
        <button type="button" className={'cn-nut' + (mo ? ' mo' : '')} disabled={khoa}
                onClick={() => setMo(x => !x)}
                aria-haspopup="listbox" aria-expanded={mo}>
          {dangChon
            ? <><span className="cn-tron" aria-hidden="true">{chuDau(dangChon.name)}</span>
                <span className="cn-ten">{dangChon.name}</span></>
            : <span className="cn-ten trong">{nhan}</span>}
          <window.Icon name="chevronDown" size={13} />
        </button>

        {mo && (
          <div className="cn-hop" role="listbox" aria-label="Danh sách nhân viên">
            <div className="cn-tim">
              <window.Icon name="search" size={13} />
              <input ref={oTim} value={tim} onChange={e => { setTim(e.target.value); setDang(0); }}
                     onKeyDown={phim} placeholder="Gõ tên để tìm…" aria-label="Tìm nhân viên" />
            </div>

            <div className="cn-ds" ref={dsRef}>
              {locDuoc.length === 0 && <div className="cn-trong">Không thấy ai khớp “{tim}”.</div>}
              {locDuoc.map((nv, i) => (
                <button key={nv.id} type="button" role="option" aria-selected={nv.id === giaTri}
                        data-dang={i === dang ? '1' : '0'}
                        className={'cn-dong' + (i === dang ? ' dang' : '') + (nv.id === giaTri ? ' chon' : '')}
                        onMouseEnter={() => setDang(i)}
                        onClick={() => chon(nv)}>
                  <span className="cn-tron" aria-hidden="true">{chuDau(nv.name)}</span>
                  <span className="cn-ten">{nv.name}</span>
                  {/* Chỉ dòng có tên trùng mới cần mã — xem chú thích 3 ở đầu file. */}
                  {tenTrung.has(nv.name) && <span className="cn-ma">#{nv.id}</span>}
                  {nv.id === giaTri && <window.Icon name="check" size={13} />}
                </button>
              ))}
            </div>

            {/* Đếm để người dùng biết mình đang nhìn bao nhiêu trong bao nhiêu — 108 dòng mà
                không có số thì gõ xong không biết đã lọc được gì. */}
            <div className="cn-chan">
              {tim ? locDuoc.length + '/' + ds.length + ' người' : ds.length + ' người'}
            </div>
          </div>
        )}
      </div>
    );
  }

  window.ChonNguoi = ChonNguoi;
  window.ChonNguoiUtil = { chuanHoa, chuDau };
})();
