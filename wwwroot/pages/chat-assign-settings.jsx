// pages/chat-assign-settings.jsx — cấu hình chia hội thoại cho nhân viên.
//
// ⚠️ ĐÂY KHÔNG CÒN LÀ MỘT TRANG. Trước 08/09/2026 nó là route riêng `/chat-assign-settings` kèm
// một mục menu bên trái. Bỏ đi vì tách riêng không đúng với việc: đây là cài đặt CỦA hộp thư
// chat, không phải một nơi để đi tới — mở nó ra là để sửa rồi quay lại hộp thư ngay. Một mục
// menu ngang hàng với "Hộp thư chat" khiến người dùng phải rời màn đang làm, mất chỗ đang đọc,
// rồi tự tìm đường về. Nay nó là khối nhúng trong hộp hội thoại, mở từ nút "Phân công" cạnh
// "Kết nối kênh".
//
// HAI chế độ, cố ý. Ảnh mẫu của sản phẩm khác có bốn (thêm "theo nhóm" và "tuỳ chọn tài khoản")
// — chưa làm vì khái niệm Nhóm chưa tồn tại trong hộp thư chat, thêm sau không phải đập đi làm lại.
//
// ĐỘI TRỰC CHỈ HIỆN Ở CHẾ ĐỘ XOAY VÒNG. Nó là vòng quay chia việc — trả lời đúng một câu,
// "tới lượt ai". Ở chế độ thủ công không có lượt nào, nên bày ra chỉ khiến người ta tưởng phải
// điền mới dùng được. Giấu được là nhờ máy chủ đã thôi dùng đội trực làm RÀO QUYỀN ở chế độ
// thủ công (cùng ngày 08/09/2026) — xem chú thích tại chỗ trong ChatInboxEndpoints.
//
// MỘT cảnh báo hậu quả phải hiện NGAY TRÊN nút Lưu, không phải đọc lỗi sau khi đã lưu: bật xoay
// vòng mà vòng quay còn rỗng → mọi hội thoại rơi về hàng chờ, trông y hệt chế độ thủ công, không
// ai đoán được nguyên nhân. Máy chủ cũng chặn (400) nhưng phải thấy TRƯỚC khi bấm.
//
// Cả danh sách nhân viên lẫn đội trực hiện tại đều tới trong MỘT lượt gọi
// GET /api/v1/chat/assign-settings (staffs + memberIds) — không gõ tay gì cả.
(function () {
  'use strict';

  const { useState, useEffect, useMemo } = React;
  const authedFetch = (...a) => window.tourkitAuth.authedFetch(...a);
  const Icon = window.Icon;

  const CHE_DO = [
    { id: 1, ten: 'Phân công thủ công',
      mo: 'Máy không gán. Người mở được hội thoại thì tự nhận hoặc giao cho người khác.' },
    { id: 2, ten: 'Chia xoay vòng',
      mo: 'Hội thoại nào chưa có người phụ trách thì gán lần lượt cho đội trực.' }
  ];

  // Khung "hộp cảnh báo" dùng chung cho cả ba chỗ cần cảnh báo — cùng một kiểu hộp vàng nhạt như
  // banner cảnh báo đã dùng ở trang AI Import NCC, không bịa màu mới.
  function HopCanhBao({ children }) {
    return (
      <div className="ci-pc-canhbao">
        {Icon && <span aria-hidden="true"><Icon name="warning" size={16} /></span>}
        <div>{children}</div>
      </div>
    );
  }

  /**
   * Khối cấu hình — nhúng thẳng vào thân hộp hội thoại của hộp thư chat.
   *
   * @param {(msg:string,kind:string)=>void} pushToast
   * @param {()=>void} onLuuXong  hộp thư gọi lại để nạp lại cấu hình sau khi lưu
   */
  function ChatAssignSettingsForm({ pushToast, onLuuXong }) {
    const [loading, setLoading] = useState(true);
    const [dangLuu, setDangLuu] = useState(false);
    const [mode, setMode] = useState(1);
    const [scopeOwnOnly, setScopeOwnOnly] = useState(false);
    const [autoAssignOnReply, setAutoAssignOnReply] = useState(false);
    const [memberIds, setMemberIds] = useState([]);
    const [staffs, setStaffs] = useState([]);

    // pushToast không phải chỗ gọi nào cũng truyền — rơi về alert để không bao giờ nuốt lỗi.
    const bao = (msg, kind) => (pushToast ? pushToast(msg, kind) : alert(msg));

    async function load() {
      setLoading(true);
      try {
        const r = await authedFetch('/api/v1/chat/assign-settings');
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const data = await r.json();
        setMode(data.mode === 2 ? 2 : 1);
        setScopeOwnOnly(!!data.scopeOwnOnly);
        setAutoAssignOnReply(!!data.autoAssignOnReply);
        setMemberIds(Array.isArray(data.memberIds) ? data.memberIds : []);
        setStaffs(Array.isArray(data.staffs) ? data.staffs : []);
      } catch (e) {
        bao('Không tải được cấu hình phân công: ' + e.message, 'error');
      } finally {
        setLoading(false);
      }
    }
    useEffect(() => { load(); }, []);

    // Tick/bỏ tick một người — đội trực chỉ là một danh sách số, thêm hoặc bớt là xong.
    const bat = (id, on) => setMemberIds(ds =>
      on ? (ds.includes(id) ? ds : [...ds, id]) : ds.filter(x => x !== id));

    // Người ĐANG trong vòng quay, theo đúng thứ tự đã lưu.
    //
    // Mã nào không tra được tên (người đã nghỉ nên CRM thôi trả về) vẫn phải hiện thành một thẻ
    // — dạng "#12". Bỏ đi thì nó biến mất khỏi màn hình nhưng VẪN nằm trong vòng quay, và vòng
    // quay sẽ gán hội thoại cho một người không còn đi làm mà không ai gỡ ra được.
    const doiTruc = useMemo(
      () => memberIds.map(id => staffs.find(nv => nv.id === id) || { id, name: '#' + id }),
      [memberIds, staffs]);
    const conLai = useMemo(
      () => staffs.filter(nv => !memberIds.includes(nv.id)), [memberIds, staffs]);

    // Một lượt ghi duy nhất — chế độ và đội trực nằm chung một dòng CSDL, nên không có khoảnh
    // khắc nào chế độ đã là xoay vòng mà đội trực còn rỗng.
    async function luu() {
      setDangLuu(true);
      try {
        const r = await authedFetch('/api/v1/chat/assign-settings', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ mode, scopeOwnOnly, autoAssignOnReply, memberIds })
        });
        if (!r.ok) {
          const err = await r.json().catch(() => ({}));
          throw new Error(err.error || ('Lưu không xong (HTTP ' + r.status + ')'));
        }
        bao('Đã lưu cấu hình phân công.', 'success');
        if (onLuuXong) onLuuXong();
      } catch (e) {
        bao(e.message, 'error');
      } finally {
        setDangLuu(false);
      }
    }

    if (loading) return <div className="ci-pc-dangtai">Đang tải…</div>;

    return (
      <div className="ci-pc">
        {/* 1 · Chế độ. Hai thẻ này là một LỰA CHỌN (chọn A thì mất B), không phải hai cái nút bấm
            độc lập — nên có chấm tròn kiểu radio. Bản trước chỉ đổi viền với nền: trông y như hai
            nút, và người dùng không đoán được bấm cái thứ hai thì cái thứ nhất có tắt đi không. */}
        <section className="ci-pc-muc">
          <h4>Chế độ phân công</h4>
          <div className="ci-pc-chedo" role="radiogroup" aria-label="Chế độ phân công">
            {CHE_DO.map(c => (
              <button key={c.id} type="button" onClick={() => setMode(c.id)}
                      role="radio" aria-checked={mode === c.id}
                      className={'ci-pc-the' + (mode === c.id ? ' on' : '')}>
                <span className="ci-pc-radio" aria-hidden="true" />
                <b>{c.ten}</b>
                <span className="ci-pc-mo">{c.mo}</span>
              </button>
            ))}
          </div>
        </section>

        {/* 2 · Đội trực — CHỈ hiện ở chế độ xoay vòng.
            Đội trực là VÒNG QUAY: nó trả lời đúng một câu, "tới lượt ai". Ở chế độ thủ công
            không có lượt nào cả, nên bày nó ra chỉ khiến người ta tưởng phải điền mới dùng được.

            ⚠️ Giấu được là nhờ máy chủ đã thôi dùng đội trực làm RÀO QUYỀN ở chế độ thủ công
            (cùng ngày 08/09/2026). Nếu chỉ giấu giao diện mà giữ rào đó thì nhân viên thường bị
            chặn bởi một danh sách không ai còn thấy: lỗi bảo "nhờ quản trị thêm người vào Cấu
            hình phân công", mà quản trị mở ra thì không có khối nào để thêm. */}
        {mode === 2 && (
          <section className="ci-pc-muc">
            <h4>Đội trực chat</h4>
            <p className="ci-pc-phu">
              Hội thoại mới chưa có người phụ trách sẽ được gán lần lượt cho những người dưới đây.
            </p>

            {staffs.length === 0 ? (
              <HopCanhBao>
                Không lấy được danh sách nhân viên từ CRM. Không phải công ty chưa có ai — lượt gọi
                đang hỏng. Tải lại trang; còn lỗi thì báo để xem log máy chủ.
              </HopCanhBao>
            ) : (
              <>
                {/* Hiện NGƯỜI ĐÃ CHỌN, không bày cả danh sách công ty.
                    Bản trước là lưới ô tick đổ hết nhân viên ra — công ty vài trăm người thì phải
                    dò mắt qua toàn bộ để biết ai đang trong vòng quay, mà đó lại là câu hỏi duy
                    nhất khối này cần trả lời. Nay đọc thẳng: mấy cái thẻ này là vòng quay. */}
                <div className="ci-pc-doi">
                  {doiTruc.length === 0 && (
                    <span className="ci-pc-doi-trong">Chưa có ai trong vòng quay</span>
                  )}
                  {doiTruc.map(nv => (
                    <span key={nv.id} className="ci-pc-chip">
                      {nv.name}
                      <button type="button" onClick={() => bat(nv.id, false)}
                              title={'Bỏ ' + nv.name} aria-label={'Bỏ ' + nv.name}>
                        <Icon name="close" size={11} />
                      </button>
                    </span>
                  ))}
                </div>

                {/* Thêm người bằng ĐÚNG ô chọn đã dùng ở khối "Phụ trách" trên hộp thư — cùng một
                    việc (tìm một người trong danh sách dài) thì cùng một cách làm, không dựng
                    control thứ hai để người dùng phải học lại. */}
                <div className="ci-pc-them">
                  {window.ChonNguoi ? (
                    <window.ChonNguoi danhSach={conLai} giaTri={null} khoa={dangLuu}
                                      nhan="Thêm người vào vòng quay…"
                                      onChon={id => bat(id, true)} />
                  ) : (
                    <span className="ci-pc-phu">Ô chọn người chưa nạp được.</span>
                  )}
                  <span className="ci-pc-dem">
                    <b>{memberIds.length}</b> / {staffs.length} người
                  </span>
                  {memberIds.length > 0 && (
                    <button type="button" className="ci-nut nho" onClick={() => setMemberIds([])}>
                      Bỏ hết
                    </button>
                  )}
                </div>
              </>
            )}
          </section>
        )}

        {/* ⚠️ MỤC "Quyền xem & tự nhận" ĐÃ GỠ khỏi màn này (08/09/2026, chủ dự án chốt): quyền
            xem là chuyện của CRM, không phải của hộp thư chat — hai nơi cùng khai một luật thì
            sớm muộn lệch nhau, và lúc đó không ai biết nơi nào đang thắng.

            Hai giá trị `scopeOwnOnly` và `autoAssignOnReply` VẪN được nạp lên và gửi lại NGUYÊN
            VẸN khi lưu. Bỏ khỏi phần gửi thì lần lưu đầu tiên sẽ âm thầm đặt chúng về false —
            màn hình không còn hiện chúng nữa nên chẳng ai thấy cấu hình vừa bị xoá. Máy chủ vẫn
            đọc và thi hành hai giá trị này như cũ. */}

        {/* Cảnh báo hậu quả — hiện NGAY TRÊN nút Lưu, để đọc được TRƯỚC khi bấm, không phải phát
            hiện sau khi cả đội mất hộp thư. */}
        {mode === 2 && memberIds.length === 0 && (
          <HopCanhBao>
            <b>Chưa chọn ai vào đội trực.</b> Bật xoay vòng lúc này thì mọi hội thoại rơi về hàng
            chờ, trông y hệt chế độ thủ công — không ai đoán được nguyên nhân.
          </HopCanhBao>
        )}

        {/* Thanh lưu DÍNH ĐÁY. Danh sách 108 người làm thân hộp cuộn được, mà nút Lưu nằm cuối
            luồng thì nó trôi khỏi tầm nhìn đúng lúc người ta vừa tick xong — phải cuộn ngược
            xuống mới lưu được, và ai không cuộn thì đóng hộp mà tưởng đã lưu. */}
        <div className="ci-pc-luu">
          <span className="ci-pc-luu-tom">
            {mode === 2
              ? (memberIds.length > 0
                  ? 'Xoay vòng cho ' + memberIds.length + ' người trong đội trực'
                  : 'Xoay vòng — chưa có ai trong đội trực')
              : 'Thủ công — người trực tự nhận hoặc giao cho nhau'}
          </span>
          <button className="ci-nut chinh" disabled={dangLuu} onClick={luu}>
            {dangLuu ? 'Đang lưu…' : 'Lưu cài đặt'}
          </button>
        </div>
      </div>
    );
  }

  window.ChatAssignSettingsForm = ChatAssignSettingsForm;
})();
