// pages/chat-assign-settings.jsx — Cấu hình chia hội thoại cho nhân viên (route /chat-assign-settings).
//
// HAI chế độ, cố ý. Ảnh mẫu của sản phẩm khác có bốn (thêm "theo nhóm" và "tuỳ chọn tài khoản")
// — chưa làm vì khái niệm Nhóm chưa tồn tại trong hộp thư chat, thêm sau không phải đập đi làm lại.
//
// HAI cảnh báo hậu quả phải hiện NGAY TRÊN nút Lưu, không phải đọc lỗi sau khi đã lưu:
//   1. Bật xoay vòng mà chưa tick ai vào đội trực → mọi hội thoại rơi về hàng chờ, trông y hệt
//      chế độ thủ công, không ai đoán được nguyên nhân. Máy chủ cũng chặn (400) nhưng người dùng
//      phải thấy trước khi bấm, không phải sau khi bấm mới biết.
//   2. Bật giới hạn quyền xem → nhân viên chỉ còn thấy hội thoại đã giao cho mình; hội thoại chưa
//      giao cho ai sẽ KHÔNG hiện với họ — chỉ quản trị viên nhìn thấy và giao xuống.
//
// Cả danh sách nhân viên lẫn đội trực hiện tại đều tới trong MỘT lượt gọi
// GET /api/v1/chat/assign-settings (staffs + memberIds) — không gõ tay gì cả.
(function () {
  'use strict';

  const { useState, useEffect } = React;
  const authedFetch = (...a) => window.tourkitAuth.authedFetch(...a);
  const Icon = window.Icon;
  const PageHero = window.PageShell && window.PageShell.PageHero;
  const TKCheckbox = window.TKCheckbox;

  const CHE_DO = [
    { id: 1, ten: 'Phân công thủ công',
      mo: 'Máy không gán. Người mở được hội thoại thì tự nhận hoặc giao cho người khác.' },
    { id: 2, ten: 'Chia xoay vòng',
      mo: 'Hội thoại nào chưa có người phụ trách thì gán lần lượt cho đội trực.' }
  ];

  // Khung "hộp cảnh báo" dùng chung cho cả ba chỗ cần cảnh báo trong trang này — cùng một kiểu
  // hộp vàng nhạt như banner cảnh báo đã dùng ở trang AI Import NCC, không bịa màu mới.
  const HOP_CANH_BAO = {
    display: 'flex', alignItems: 'flex-start', gap: 10, padding: '12px 14px',
    background: 'rgba(245,158,11,0.08)', border: '1px solid rgba(245,158,11,0.35)',
    borderRadius: 'var(--radius)', fontSize: 12.5, lineHeight: 1.55, color: '#92400e',
  };

  function HopCanhBao({ children }) {
    return (
      <div style={HOP_CANH_BAO}>
        {Icon && (
          <span style={{ flexShrink: 0, marginTop: 1, color: '#d97706' }}>
            <Icon name="warning" size={16} />
          </span>
        )}
        <div>{children}</div>
      </div>
    );
  }

  function ChatAssignSettingsPage({ pushToast }) {
    const [loading, setLoading] = useState(true);
    const [dangLuu, setDangLuu] = useState(false);
    const [mode, setMode] = useState(1);
    const [scopeOwnOnly, setScopeOwnOnly] = useState(false);
    const [autoAssignOnReply, setAutoAssignOnReply] = useState(false);
    const [memberIds, setMemberIds] = useState([]);
    const [staffs, setStaffs] = useState([]);

    // pushToast không phải page nào cũng nhận (phòng khi route gọi thiếu prop) — rơi về alert
    // để không bao giờ nuốt lỗi trong im lặng.
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
      } catch (e) {
        bao(e.message, 'error');
      } finally {
        setDangLuu(false);
      }
    }

    if (loading) {
      return (
        <main className="page" style={{ padding: '18px 28px 60px', width: '100%' }}>
          <div style={{ color: 'var(--text-3)', padding: 40, textAlign: 'center' }}>Đang tải…</div>
        </main>
      );
    }

    return (
      <main className="page" style={{ padding: '18px 28px 60px', width: '100%', maxWidth: 760 }}>
        {PageHero ? (
          <PageHero icon="share" title="Chia hội thoại cho nhân viên"
            sub="Chọn cách hộp thư chat gán hội thoại mới cho từng người, và ai được xem hội thoại nào." />
        ) : (
          <div className="page-title-block">
            <h2 className="page-title">Chia hội thoại cho nhân viên</h2>
          </div>
        )}

        {/* 1 · Chế độ */}
        <section className="card" style={{ marginTop: 18 }}>
          <h3 style={{ marginTop: 0, marginBottom: 14 }}>Chế độ phân công</h3>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: 12 }}>
            {CHE_DO.map(c => (
              <button key={c.id} type="button" onClick={() => setMode(c.id)}
                style={{
                  textAlign: 'left', padding: 16, borderRadius: 'var(--radius-md)', cursor: 'pointer',
                  border: '2px solid ' + (mode === c.id ? 'var(--primary)' : 'var(--border)'),
                  background: mode === c.id ? 'var(--primary-soft)' : 'var(--surface)',
                }}>
                <b style={{ display: 'block', marginBottom: 4, color: 'var(--text)', fontSize: 14 }}>
                  {c.ten}{mode === c.id && <span style={{ marginLeft: 8, fontSize: 11, fontWeight: 700, color: 'var(--primary)' }}>✓ ĐANG CHỌN</span>}
                </b>
                <span style={{ fontSize: 12.5, color: 'var(--text-3)', lineHeight: 1.5 }}>{c.mo}</span>
              </button>
            ))}
          </div>
        </section>

        {/* 2 · Đội trực chat. Hiện cả khi đang ở chế độ thủ công: ô chọn người phụ trách trên hộp
            thư cũng đổ ra danh sách này, nên nó không phải chuyện riêng của xoay vòng. */}
        <section className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginTop: 0, marginBottom: 4 }}>Đội trực chat</h3>
          <p style={{ fontSize: 12.5, color: 'var(--text-3)', marginTop: 0, marginBottom: 14 }}>
            Vừa là vòng quay chia việc (chế độ xoay vòng), vừa là danh sách hiện ở ô chọn người
            phụ trách trên đầu khung chat.
          </p>
          {staffs.length === 0 ? (
            <HopCanhBao>
              Không lấy được danh sách nhân viên từ CRM. Không phải công ty chưa có ai — lượt gọi
              đang hỏng. Tải lại trang; còn lỗi thì báo để xem log máy chủ.
            </HopCanhBao>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: 6 }}>
              {staffs.map(nv => (
                TKCheckbox ? (
                  <TKCheckbox key={nv.id} checked={memberIds.includes(nv.id)}
                    onChange={on => bat(nv.id, on)} label={nv.name} />
                ) : (
                  <label key={nv.id} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 4px', cursor: 'pointer' }}>
                    <input type="checkbox" checked={memberIds.includes(nv.id)}
                      onChange={e => bat(nv.id, e.target.checked)} />
                    <span style={{ fontSize: 13.5 }}>{nv.name}</span>
                  </label>
                )
              ))}
            </div>
          )}
        </section>

        {/* 3 · Hai công tắc — công tắc cam dùng chung toàn app (workflows-toggle), không phải ô
            tick vuông của trình duyệt, để không lẫn với ô tick chọn người ở khối trên. */}
        <section className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginTop: 0, marginBottom: 14 }}>Quyền xem &amp; tự nhận</h3>
          <div className="workflows-toggle-wrap" style={{ marginBottom: 14 }}>
            <label className={'workflows-toggle' + (dangLuu ? ' is-disabled' : '')}>
              <input type="checkbox" checked={scopeOwnOnly} disabled={dangLuu}
                onChange={e => setScopeOwnOnly(e.target.checked)} />
              <span className="workflows-toggle-track" />
            </label>
            <span className="workflows-toggle-label">Nhân viên chỉ xem được hội thoại đã giao cho mình</span>
          </div>
          <div className="workflows-toggle-wrap">
            <label className={'workflows-toggle' + (dangLuu ? ' is-disabled' : '')}>
              <input type="checkbox" checked={autoAssignOnReply} disabled={dangLuu}
                onChange={e => setAutoAssignOnReply(e.target.checked)} />
              <span className="workflows-toggle-track" />
            </label>
            <span className="workflows-toggle-label">Ai trả lời tin đầu tiên thì thành người phụ trách</span>
          </div>
        </section>

        {/* Cảnh báo hậu quả — hiện NGAY TRÊN nút Lưu, để người dùng đọc được TRƯỚC khi bấm, không
            phải phát hiện sau khi cả đội mất hộp thư. */}
        {mode === 2 && memberIds.length === 0 && (
          <div style={{ marginTop: 16 }}>
            <HopCanhBao>
              <b>Chưa chọn ai vào đội trực.</b> Bật xoay vòng lúc này thì mọi hội thoại rơi về hàng
              chờ, trông y hệt chế độ thủ công — không ai đoán được nguyên nhân.
            </HopCanhBao>
          </div>
        )}
        {scopeOwnOnly && (
          <div style={{ marginTop: 16 }}>
            <HopCanhBao>
              Bật mục này thì nhân viên <b>chỉ còn thấy hội thoại đã giao cho mình</b>. Hội thoại
              chưa giao cho ai sẽ không hiện với họ — chỉ quản trị viên nhìn thấy và giao xuống.
            </HopCanhBao>
          </div>
        )}

        <div style={{ marginTop: 20 }}>
          <button className="btn btn-primary" disabled={dangLuu} onClick={luu}>
            {dangLuu ? 'Đang lưu…' : 'Lưu cài đặt'}
          </button>
        </div>
      </main>
    );
  }

  window.ChatAssignSettingsPage = ChatAssignSettingsPage;
})();
