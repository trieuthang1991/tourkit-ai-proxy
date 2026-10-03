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
   * @param {boolean} giuMo           CHỌN NHIỀU: bấm xong KHÔNG đóng, giữ nguyên chữ đang tìm
   * @param {Array<number>} daChon    mã những người đã chọn — để đánh dấu trong danh sách
   *
   * HAI VIỆC KHÁC NHAU dùng chung control này, và chúng ngược nhau ở đúng một điểm:
   *   · giao MỘT hội thoại cho MỘT người  → chọn xong là xong, đóng lại là đúng;
   *   · dựng ĐỘI TRỰC gồm nhiều người     → chọn xong còn chọn tiếp, đóng lại là bắt mở lại.
   * Bản đầu chỉ có vế trên, nên dựng đội tám người là tám lần mở/gõ/bấm/đóng. `giuMo` mở vế
   * dưới mà không đụng gì tới vế trên (mặc định tắt).
   */
  function ChonNguoi({ danhSach, giaTri, onChon, nhan = 'Chọn người…', khoa = false, autoMo = false,
                       giuMo = false, daChon = null }) {
    const [mo, setMo] = useState(false);
    const [tim, setTim] = useState('');
    const [dang, setDang] = useState(0);       // dòng đang được bàn phím trỏ tới
    const boc = useRef(null);
    const oTim = useRef(null);
    const dsRef = useRef(null);
    const hopRef = useRef(null);
    // Toạ độ tuyệt đối của hộp, tính từ nút bấm. Chỉ dùng khi hộp đã mở.
    const [viTri, setViTri] = useState(null);

    const ds = danhSach || [];
    const dangChon = ds.find(nv => nv.id === giaTri) || null;
    // Ở chế độ chọn nhiều thì "đang chọn" là cả một tập, không phải một giá trị.
    const tapDaChon = useMemo(() => new Set(daChon || []), [daChon]);
    const daLay = nv => nv.id === giaTri || tapDaChon.has(nv.id);

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

    // Đo chỗ đặt hộp, và đo LẠI mỗi khi có gì đó cuộn hoặc đổi kích thước.
    //
    // Hộp nằm ở lớp nổi (portal ra <body>) nên nó KHÔNG tự đi theo nút nữa — đổi lại, nó không
    // còn nằm trong luồng cuộn của cửa sổ cài đặt. Đó chính là điều cần: trước đây mở danh sách
    // ra là cửa sổ dài thêm 250px, người dùng phải cuộn cả cửa sổ mới thấy hết danh sách, và
    // hộp thì bị cắt ở mép dưới.
    //
    // Nghe scroll ở pha BẮT (true): thứ cuộn là thân cửa sổ cài đặt chứ không phải cửa sổ trình
    // duyệt, mà sự kiện cuộn của phần tử con không nổi bọt lên window.
    useEffect(() => {
      if (!mo) return;
      const do_ = () => {
        const n = boc.current;
        if (!n) return;
        const r = n.getBoundingClientRect();
        const cao = 300;                       // ước lượng chiều cao tối đa của hộp
        const duoi = window.innerHeight - r.bottom;
        // Không đủ chỗ bên dưới thì lật lên trên — nhưng chỉ khi bên trên rộng rãi hơn thật.
        const tren = duoi < cao && r.top > duoi;
        setViTri({ trai: r.left, rong: r.width, tren, y: tren ? r.top : r.bottom });
      };
      do_();
      window.addEventListener('scroll', do_, true);
      window.addEventListener('resize', do_);
      return () => {
        window.removeEventListener('scroll', do_, true);
        window.removeEventListener('resize', do_);
      };
    }, [mo]);

    // Bấm ra ngoài là đóng. Không có nó thì hộp dính lại cho tới khi bấm đúng nút — kiểu bực
    // mình nhỏ mà gặp mỗi lần.
    //
    // Phải xét CẢ hộp: nó không còn là con của `boc` sau khi ra lớp nổi, nên chỉ hỏi `boc` thì
    // bấm vào chính danh sách cũng bị tính là bấm ra ngoài.
    useEffect(() => {
      if (!mo) return;
      const ngoai = e => {
        const trongNut = boc.current && boc.current.contains(e.target);
        const trongHop = hopRef.current && hopRef.current.contains(e.target);
        if (!trongNut && !trongHop) setMo(false);
      };
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
      if (!nv) return;
      if (giuMo) {
        // Giữ nguyên hộp VÀ chữ đang tìm: gõ "sale" rồi thêm liền ba người trong cùng một lượt
        // tìm là việc thật hay gặp. Xoá chữ đi thì lần nào cũng phải gõ lại.
        onChon(nv.id);
        if (oTim.current) oTim.current.focus();
        return;
      }
      setMo(false); setTim('');
      if (nv.id !== giaTri) onChon(nv.id);
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

        {mo && viTri && ReactDOM.createPortal((
          /* Hộp dựng ở LỚP NỔI (portal ra <body>), không dựng lồng trong nút.
             Lồng trong nút thì nó là con của thân cửa sổ cài đặt — vốn cuộn được — nên mở danh
             sách ra là cửa sổ dài thêm chừng 250px, hộp bị cắt ở mép dưới, và muốn xem hết
             danh sách phải cuộn cả cửa sổ. Ra lớp nổi thì nó phủ lên trên, không đụng gì tới
             chiều cao cửa sổ.

             z-index phải trên nền cửa sổ cài đặt (60) — xem .cn-hop.noi trong styles.css. */
          <div ref={hopRef} className="cn-hop noi" role="listbox" aria-label="Danh sách nhân viên"
               style={{
                 left: viTri.trai, width: viTri.rong,
                 ...(viTri.tren ? { bottom: window.innerHeight - viTri.y + 5 }
                                : { top: viTri.y + 5 }),
               }}>
            <div className="cn-tim">
              <window.Icon name="search" size={13} />
              <input ref={oTim} value={tim} onChange={e => { setTim(e.target.value); setDang(0); }}
                     onKeyDown={phim} placeholder="Gõ tên để tìm…" aria-label="Tìm nhân viên" />
            </div>

            <div className="cn-ds" ref={dsRef}>
              {locDuoc.length === 0 && <div className="cn-trong">Không thấy ai khớp “{tim}”.</div>}
              {locDuoc.map((nv, i) => (
                <button key={nv.id} type="button" role="option" aria-selected={daLay(nv)}
                        data-dang={i === dang ? '1' : '0'}
                        className={'cn-dong' + (i === dang ? ' dang' : '') + (daLay(nv) ? ' chon' : '')}
                        onMouseEnter={() => setDang(i)}
                        onClick={() => chon(nv)}>
                  <span className="cn-tron" aria-hidden="true">{chuDau(nv.name)}</span>
                  <span className="cn-ten">{nv.name}</span>
                  {/* Chỉ dòng có tên trùng mới cần mã — xem chú thích 3 ở đầu file. */}
                  {tenTrung.has(nv.name) && <span className="cn-ma">#{nv.id}</span>}
                  {daLay(nv) && <window.Icon name="check" size={13} />}
                </button>
              ))}
            </div>

            {/* Đếm để người dùng biết mình đang nhìn bao nhiêu trong bao nhiêu — 108 dòng mà
                không có số thì gõ xong không biết đã lọc được gì. */}
            <div className="cn-chan">
              {tim ? locDuoc.length + '/' + ds.length + ' người' : ds.length + ' người'}
            </div>
          </div>
        ), document.body)}
      </div>
    );
  }

  window.ChonNguoi = ChonNguoi;
  window.ChonNguoiUtil = { chuanHoa, chuDau };
})();
