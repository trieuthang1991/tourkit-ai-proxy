// pages/digest.jsx — Khối "Bản tin của tôi".
//
// KHÔNG phải một trang riêng: 2 khối này nhúng vào thẻ tác vụ ở trang Tự động hoá. Lý do (chốt
// 12/08): đăng ký nhận bản tin CHÍNH LÀ cấu hình của tác vụ sale-brief/ceo-brief — tách ra trang
// riêng thì người dùng phải nhớ 2 nơi cho cùng một việc.
//
// Phân vai quyền, giống hệt cách hộp thư (mail-auto-sync) đang làm:
//   • "Bản tin của tôi" — nơi nhận của CHÍNH mình → KHÔNG cần quyền gì.
//   • Lịch chạy (bật/tắt, tần suất) — cấp công ty → cần quyền xem cấu hình.
'use strict';

const { useState: dS, useEffect: dE, useCallback: dCB } = React;

// Đủ 24 giờ, KHÔNG bó hẹp về "giờ hành chính".
//
// Bản đầu chỉ cho 5h–20h cho gọn, và nó tạo ra lỗi thật ngay lần xem đầu tiên: một đăng ký đã lưu
// 21:00 khiến ô chọn không có mục nào khớp → trình duyệt hiện mục đầu (05:00). Giao diện NÓI SAI giờ
// đã lưu, mà người dùng không có cách nào biết. Backend nhận 0–23 nên danh sách phải phủ hết 0–23;
// vả lại 21h là lựa chọn hợp lý (đọc bản tin tối trước cho sáng mai).
const HOURS = Array.from({ length: 24 }, (_, i) => i);

const EMPTY_SUB = {
  enabled: false, sendHourLocal: 7,
  channelInApp: true,
  channelEmail: false, email: '',
  channelTelegram: false, telegramChatId: '',
  channelZalo: false, zaloPhone: '',
};

const BRIEF_TYPES = ['sale-brief', 'ceo-brief'];

// Công tắc DÙNG CHUNG với trang Tự động hoá (.workflows-toggle), không phải ô tick mặc định của
// trình duyệt. Trước đây khối này dùng <input type="checkbox"> trần nên cùng một màn hình có hai
// kiểu bật/tắt khác hẳn nhau — bên trên là công tắc cam, bên dưới là ô vuông xanh của hệ điều hành.
function Sw({ checked, disabled, onChange }) {
  return (
    <label className={'workflows-toggle' + (disabled ? ' is-disabled' : '')}>
      <input type="checkbox" checked={!!checked} disabled={disabled}
        onChange={e => onChange(e.target.checked)} />
      <span className="workflows-toggle-track" />
    </label>
  );
}

async function dApi(path, opts = {}) {
  const r = await window.tourkitAuth.authedFetch(path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
  });
  let body = null;
  try { body = await r.json(); } catch {}
  if (!r.ok) throw new Error((body && body.error) || `HTTP ${r.status}`);
  return body;
}

// Zalo gửi bằng ZNS — nhắn theo SỐ ĐIỆN THOẠI, không phải theo Zalo user id. Chặn ngay tại đây vì
// số sai thì Zalo mới từ chối lúc gửi, mà lúc đó chỉ admin nhìn thấy còn người đăng ký thì không.
// Chỉ nhận số di động: Zalo là ứng dụng di động, gõ số bàn vào chỉ tổ gửi hỏng mà không hiểu vì sao.
function isVnMobile(raw) {
  let d = String(raw || '').replace(/\D/g, '');
  if (d.startsWith('84') && d.length >= 11) d = '0' + d.slice(2);
  return /^0[35789]\d{8}$/.test(d);
}

// Tóm tắt 1 dòng cho dòng list thu gọn của thẻ tác vụ — để không phải mở ra mới biết mình có nhận không.
function digestSummary(sub) {
  if (!sub || !sub.enabled) return 'Bạn chưa bật nhận bản tin này';
  const ch = [];
  if (sub.channelInApp) ch.push('trong app');
  if (sub.channelEmail) ch.push('email');
  if (sub.channelTelegram) ch.push('telegram');
  if (sub.channelZalo) ch.push('zalo');
  return `Bạn nhận lúc ${String(sub.sendHourLocal).padStart(2, '0')}:00 · ${ch.join(', ') || 'chưa chọn kênh'}`;
}

// Liệt kê kênh đang bật, cho dòng trỏ ngược trong thẻ bản tin. "Trong app" luôn có nên luôn kể.
function channelSummary(sub) {
  const ch = ['trong app'];
  if (sub && sub.channelEmail) ch.push('email');
  if (sub && sub.channelTelegram) ch.push('Telegram');
  if (sub && sub.channelZalo) ch.push('Zalo');
  return ch.join(', ');
}

// ─── Khối gập: đóng sẵn, và lúc đóng vẫn PHẢI trả lời được "tôi xong chưa?" ─────
//
// Hai khối cấu hình này chỉ khai một lần rồi gần như không đụng lại, nhưng trước đây luôn mở toang
// ở đầu trang — nghĩa là mọi người, mọi lần vào, đều phải cuộn qua 10 ô nhập đã điền xong từ lâu.
//
// Điều kiện để được phép đóng sẵn: dòng tiêu đề phải NÓI RA tình trạng, không chỉ là cái tên. Vì
// cả 3 kiểu hỏng của cụm bản tin đều IM LẶNG (chưa khai / bật kênh mà bỏ trống nơi nhận / gửi
// hỏng) — đóng một khối chưa khai xong mà ngoài chỉ ghi mỗi cái tên thì đúng là đem giấu lỗi đi.
// Nên `state` ở đây không phải huy hiệu trang trí: nó là lý do khối này được phép đóng.
//
// Dùng <button> thật cho dòng tiêu đề → có Tab/Enter/Space và viền focus sẵn, không phải tự chế.
function Fold({ icon, title, state, defaultOpen = false, className, children }) {
  const Icon = window.Icon;
  const [open, setOpen] = dS(!!defaultOpen);
  return (
    <div className={'digest-fold' + (open ? ' is-open' : '') + (className ? ' ' + className : '')}>
      <button type="button" className="digest-fold-head" aria-expanded={open}
        onClick={() => setOpen(v => !v)}>
        <Icon name={icon} size={15} />
        <span className="digest-fold-title">{title}</span>
        {state && <span className={'digest-fold-state is-' + state.tone}>{state.text}</span>}
        <span className="digest-fold-chev"><Icon name={open ? 'chevronUp' : 'chevronDown'} size={15} /></span>
      </button>
      {open && <div className="digest-fold-body">{children}</div>}
    </div>
  );
}

// Tình trạng nơi nhận, đọc được khi khối đang đóng.
// "Chưa khai" là SAI ở đây — trong app luôn bật nên không bao giờ có chuyện không nhận được gì.
// Trạng thái đáng giá nhất là cái ở giữa: bật một kênh nhưng bỏ trống địa chỉ. Đó chính là kiểu
// hỏng lặng lẽ mà người dùng chỉ phát hiện ra khi sáng hôm sau không thấy gì tới.
function channelState(sub) {
  const s = sub || {};
  const on = [];
  if (s.channelEmail) on.push({ n: 'Email', ok: !!String(s.email || '').trim() });
  if (s.channelTelegram) on.push({ n: 'Telegram', ok: !!String(s.telegramChatId || '').trim() });
  if (s.channelZalo) on.push({ n: 'Zalo', ok: isVnMobile(s.zaloPhone) });
  if (!on.length) return { tone: 'none', text: 'Chỉ nhận trong app' };
  const missing = on.filter(x => !x.ok).map(x => x.n);
  if (missing.length) return { tone: 'warn', text: 'Thiếu nơi nhận: ' + missing.join(', ') };
  return { tone: 'ok', text: on.map(x => x.n).join(' · ') };
}

// ─── "Nơi nhận của tôi" — khai MỘT LẦN, mọi thông báo dùng chung ────────────────
//
// Trước đây ô email/Telegram/Zalo nằm bên trong thẻ bản tin, nên nó trông như "nơi nhận của riêng
// bản tin sáng". Thực tế dữ liệu vốn đã dùng chung: mỗi người CHỈ MỘT dòng trong DigestSubscriptions
// (khoá chính TenantId+Username), địa chỉ nằm trên chính dòng đó. Đưa khối này lên đầu mục "Theo
// người dùng" là nói đúng bản chất, và tránh chuyện thêm mỗi loại cảnh báo lại thêm một ô email nữa.
//
// Lưu qua endpoint RIÊNG (PUT /digest/my-channels) — không đụng loại bản tin và giờ nhận.
function MyChannelsBlock({ sub, onSaved, pushToast }) {
  const Icon = window.Icon;
  const [f, setF] = dS({ ...EMPTY_SUB, ...(sub || {}) });
  const [saving, setSaving] = dS(false);
  const [dirty, setDirty] = dS(false);
  const [tgCode, setTgCode] = dS(null);

  dE(() => { if (!dirty) setF({ ...EMPTY_SUB, ...(sub || {}) }); }, [sub]);
  const set = (patch) => { setF(prev => ({ ...prev, ...patch })); setDirty(true); };

  const problem = (() => {
    if (f.channelEmail && !String(f.email || '').trim()) return 'Nhập email nhận.';
    if (f.channelTelegram && !String(f.telegramChatId || '').trim()) return 'Nhập chat id Telegram (hoặc bấm Tự phát hiện).';
    if (f.channelZalo && !isVnMobile(f.zaloPhone)) return 'Nhập số điện thoại Zalo (10 số, bắt đầu bằng 0).';
    return null;
  })();

  const save = async () => {
    if (problem) { pushToast && pushToast(problem); return; }
    setSaving(true);
    try {
      await dApi('/api/v1/digest/my-channels', {
        method: 'PUT',
        body: JSON.stringify({
          channelEmail: !!f.channelEmail, email: f.email,
          channelTelegram: !!f.channelTelegram, telegramChatId: f.telegramChatId,
          channelZalo: !!f.channelZalo, zaloPhone: f.zaloPhone,
        }),
      });
      setDirty(false);
      pushToast && pushToast('Đã lưu nơi nhận của bạn');
      onSaved && onSaved();
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setSaving(false); }
  };

  const detectTelegram = async () => {
    try {
      const d = await dApi('/api/v1/digest/telegram/detect', { method: 'POST' });
      setTgCode(d);
      if (d.chatId) { set({ telegramChatId: String(d.chatId).replace(/"/g, '') }); pushToast && pushToast('Đã tìm ra chat id'); }
    } catch (e) {
      setTgCode({ hint: e.message });
    }
  };

  return (
    <Fold icon="send" title="Nơi nhận của tôi" state={channelState(sub)}
      className="digest-block digest-channels">
      <div className="digest-role-note">
        <Icon name="info" size={12} /> Khai <b>một lần</b> ở đây, dùng cho <b>tất cả</b> thông báo bạn
        bật bên dưới — bản tin sáng, cảnh báo, và những thứ thêm sau này.
      </div>

      {/* Khoá bật: "trong app" không phải kênh gửi mà là KHO LƯU — thông báo luôn được ghi vào Bảng
          tin, để còn xem/nghe lại kể cả khi mọi kênh ngoài hỏng. Server cũng ép bật. */}
      <div className="digest-ch">
        <Sw checked disabled onChange={() => {}} />
        <span className="digest-ch-name"><Icon name="bell" size={13} /> Trong app</span>
        <span className="digest-ch-note">Luôn bật — luôn lưu ở tab Bảng tin để xem/nghe lại</span>
      </div>

      <div className="digest-ch">
        <Sw checked={!!f.channelEmail} onChange={v => set({ channelEmail: v })} />
        <span className="digest-ch-name"><Icon name="mail" size={13} /> Email</span>
        <input className="digest-input" type="email" placeholder="ban@congty.vn"
          value={f.email || ''} onChange={e => set({ email: e.target.value })} disabled={!f.channelEmail} />
      </div>

      <div className="digest-ch">
        <Sw checked={!!f.channelTelegram} onChange={v => set({ channelTelegram: v })} />
        <span className="digest-ch-name"><Icon name="send" size={13} /> Telegram</span>
        <input className="digest-input" placeholder="vd 6234567890"
          value={f.telegramChatId || ''} onChange={e => set({ telegramChatId: e.target.value })}
          disabled={!f.channelTelegram} />
        <button type="button" className="wga-btn ghost digest-detect"
          onClick={(e) => { e.preventDefault(); detectTelegram(); }} disabled={!f.channelTelegram}>
          Tự phát hiện
        </button>
      </div>
      {/* Ô này từng chỉ ghi "chat id" và người dùng hỏi thẳng "điền gì vào đây" — đúng là không đủ:
          không nói đó là CON SỐ, không nói lấy ở đâu, và không nói bước Start (thiếu Start thì
          Telegram trả "chat not found" và dòng bị bỏ qua IM LẶNG, không lỗi nào hiện ra). */}
      {f.channelTelegram && (
        <div className="digest-ch-warn">
          <Icon name="info" size={12} /> Đây là <b>một dãy số</b> Telegram cấp cho cuộc trò chuyện
          giữa bạn và bot — không phải <i>@tên</i> hay số điện thoại. Trước tiên hãy mở bot và bấm
          <b> Bắt đầu</b> (bot không được phép nhắn trước cho người chưa mở hội thoại), rồi bấm
          <b> Tự phát hiện</b>. Muốn tự điền thì nhắn cho <b>@userinfobot</b> để lấy số của bạn.
        </div>
      )}
      {tgCode && (
        <div className="digest-tg-hint">
          {tgCode.code
            ? <>Nhắn đúng dòng <b>{tgCode.code}</b> cho bot{tgCode.botUsername ? <> <b>@{tgCode.botUsername}</b></> : null}, rồi bấm lại “Tự phát hiện”.</>
            : <>{tgCode.hint}</>}
        </div>
      )}

      <div className="digest-ch">
        <Sw checked={!!f.channelZalo} onChange={v => set({ channelZalo: v })} />
        <span className="digest-ch-name"><Icon name="user" size={13} /> Zalo</span>
        <input className="digest-input" placeholder="Số điện thoại Zalo, vd 0912345678"
          value={f.zaloPhone || ''} onChange={e => set({ zaloPhone: e.target.value })} disabled={!f.channelZalo} />
      </div>
      {f.channelZalo && (
        <div className="digest-ch-warn">
          <Icon name="info" size={12} /> Tin Zalo là lời nhắc ngắn; nội dung đầy đủ đọc ở tab Bảng tin.
          Số phải là số đang dùng Zalo.
        </div>
      )}

      {problem && <div className="digest-problem"><Icon name="warning" size={13} /> {problem}</div>}

      <div className="workflows-actions digest-actions">
        <button className="wga-btn primary" onClick={save} disabled={saving || !!problem}>
          <Icon name="save" size={14} /> {saving ? 'Đang lưu…' : 'Lưu nơi nhận'}
        </button>
        {dirty && <span className="digest-dirty">Có thay đổi chưa lưu</span>}
      </div>
    </Fold>
  );
}

// ─── Khối đăng ký của một loại bản tin ──────────────────────────────────────────
// companyReady: công ty đã khai luật chung của loại bản tin này chưa (đã có ai bấm Lưu cấu hình
// ở mục "Theo tổ chức"). Chưa khai thì server từ chối bật nhận (409) — nên khoá ngay ô tick,
// đừng để người dùng bấm xong mới nhận lỗi.
//
// Chuyện "công ty đã bật lịch gửi chưa" KHÔNG còn hỏi ở đây: bật nhận là hệ thống tự bật lịch,
// và nếu vì lý do nào đó lịch vẫn tắt thì dòng phán quyết ở đầu thẻ nói + có sẵn nút bật.
function DigestSubBlock({ briefType, sub, onSaved, pushToast, companyReady = true }) {
  const Icon = window.Icon;
  const [f, setF] = dS({ ...EMPTY_SUB, ...(sub || {}) });
  const [saving, setSaving] = dS(false);
  const [testing, setTesting] = dS(false);
  const [testResult, setTestResult] = dS(null);
  const [dirty, setDirty] = dS(false);

  // Server là nguồn đúng: sau khi tải lại thì đồng bộ về, TRỪ khi người dùng đang sửa dở —
  // ghi đè lúc đó là xoá mất thao tác của họ ngay trước mắt.
  dE(() => { if (!dirty) setF({ ...EMPTY_SUB, ...(sub || {}) }); }, [sub]);

  const set = (patch) => { setF(prev => ({ ...prev, ...patch })); setDirty(true); };

  // Kênh nhận nay khai ở khối "Nơi nhận của tôi" nên thẻ này không kiểm chúng nữa — kiểm ở đây
  // sẽ khoá nút Lưu vì một ô nằm ở màn hình khác, người dùng không hiểu phải sửa gì.
  const problem = null;

  const save = async () => {
    if (problem) { pushToast && pushToast(problem); return; }
    setSaving(true);
    try {
      await dApi(`/api/v1/digest/subscriptions/${briefType}`, { method: 'PUT', body: JSON.stringify(f) });
      setDirty(false);
      pushToast && pushToast('Đã lưu: ' + digestSummary(f));
      onSaved && onSaved();
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setSaving(false); }
  };

  const sendTest = async () => {
    if (dirty) { pushToast && pushToast('Bấm Lưu trước khi Gửi thử — server gửi theo bản đã lưu.'); return; }
    setTesting(true); setTestResult(null);
    try {
      const d = await dApi(`/api/v1/digest/subscriptions/${briefType}/test`, { method: 'POST' });
      setTestResult(d);
      // Bản tin thử vào Bảng tin NGAY (kênh ngoài thì qua hàng đợi, ~1 phút) → nhắc chuông + tab
      // Bảng tin cập nhật liền, khỏi đợi hết chu kỳ.
      window.dispatchEvent(new CustomEvent('tourkit:insights'));
    } catch (e) { setTestResult({ ok: false, summary: e.message }); }
    finally { setTesting(false); }
  };

  // detectTelegram đã chuyển sang MyChannelsBlock cùng với ô nhập chat id.

  return (
    <div className="workflows-optgroup digest-block">
      <div className="workflows-optgroup-title">Bản tin của tôi</div>

      <div className="digest-ch">
        <Sw checked={!!f.enabled} disabled={!companyReady}
          onChange={v => set({ enabled: v })} />
        <span className="digest-ch-name">Nhận bản tin này</span>
        <span className="digest-ch-note">
          {companyReady
            ? 'Chỉ áp dụng cho riêng bạn, không ảnh hưởng người khác'
            : 'Chưa bật được — công ty chưa cấu hình bản tin này'}
        </span>
      </div>

      <div className="digest-role-note">
        <Icon name="info" size={12} /> Mỗi người chỉ nhận <b>một</b> loại bản tin theo vai trò — bật loại
        này sẽ tự tắt loại kia.
      </div>

      <div className="digest-row">
        <div className="digest-label">Giờ nhận <span className="digest-hint">(giờ Việt Nam)</span></div>
        <select className="workflows-select" value={f.sendHourLocal}
          onChange={e => set({ sendHourLocal: parseInt(e.target.value, 10) })}>
          {HOURS.map(h => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
        </select>
      </div>

      {/* Kênh nhận ĐÃ CHUYỂN LÊN khối "Nơi nhận của tôi" ở đầu mục Theo người dùng (17/08): địa
          chỉ nhận là hồ sơ của NGƯỜI, dùng chung cho mọi thông báo — để trong thẻ này thì mỗi loại
          cảnh báo thêm sau lại phải khai email thêm một lần nữa. */}
      <div className="digest-ch-ref">
        <Icon name="info" size={12} /> Gửi tới: {channelSummary(f)}.
        Đổi ở khối <b>Nơi nhận của tôi</b> phía trên.
      </div>

      {problem && <div className="digest-problem"><Icon name="warning" size={13} /> {problem}</div>}

      {/* Cảnh báo "công ty chưa bật lịch gửi" ĐÃ CHUYỂN LÊN dòng phán quyết ở đầu thẻ (workflows.jsx):
          ở đó nó nằm ngay cạnh nút bật, và nhìn thấy được cả khi thẻ đang đóng. Để lại bản thứ hai ở
          đây thì cùng một chuyện nói hai lần trên một màn hình, mà bản này còn chỉ sai đường —
          mục "Lịch chạy" nay nằm ở phần Theo tổ chức, không còn trong thẻ này. */}

      <div className="workflows-actions digest-actions">
        <button className="wga-btn primary" onClick={save} disabled={saving || !!problem}>
          <Icon name="save" size={14} /> {saving ? 'Đang lưu…' : 'Lưu bản tin của tôi'}
        </button>
        <button className="wga-btn ghost" onClick={sendTest} disabled={testing || dirty || !sub}>
          <Icon name="send" size={14} /> {testing ? 'Đang gửi…' : 'Gửi thử'}
        </button>
        {dirty && <span className="digest-dirty">Có thay đổi chưa lưu</span>}
        {testResult && (
          <span className={'digest-test' + (testResult.ok ? ' is-ok' : ' is-bad')}>
            {testResult.ok ? <>Đã xếp gửi: {testResult.sentChannels}</> : <>Không gửi được — {testResult.summary}</>}
          </span>
        )}
      </div>
      {testResult && testResult.ok && testResult.summary && (
        <div className="digest-test-detail">{testResult.summary}</div>
      )}
    </div>
  );
}

// ─── Zalo OA của công ty (khôi phục 17/08) ──────────────────────────────────────
//
// Bản 14/08 gỡ khối này để dùng OA chung. Đi gặp khách hàng 17/08 thì KHÔNG công ty nào chịu: tin
// ZNS hiện tên OA người gửi, dùng OA của bên cung cấp dịch vụ nghĩa là khách của họ nhận tin mang
// tên một công ty khác. Nên OA riêng là đường chính; ai dùng OA của bên cung cấp thì nhập khoá
// được cấp. KHÔNG có lựa chọn "để trống cho hệ thống tự lo".
//
// Mã mẫu ZNS khai theo TỪNG chức năng vì Zalo duyệt mẫu theo nội dung — bản tin sáng và nhắc thu
// tiền là hai mẫu khác nhau.
const ZALO_FEATURE_LABELS = {
  'sale-brief': 'Bản tin sáng (nhân viên bán hàng)',
  'ceo-brief': 'Bản tin điều hành (giám đốc)',
  'payment-alert': 'Nhắc thu tiền trước khởi hành',
};

// Tình trạng OA, đọc được khi khối đang đóng.
// Trạng thái giữa ("khai xong khoá nhưng thiếu mã mẫu") là trạng thái đáng nói nhất: nó trông y hệt
// đã xong — khoá đủ, không lỗi nào hiện — mà chức năng thiếu mã mẫu thì im lặng không gửi.
// Đếm chứ không liệt kê: nhãn chức năng dài tới 30 ký tự, kể ra ba cái là tràn cả dòng tiêu đề.
function zaloState(st, features) {
  if (!st) return null;
  if (!st.configured) return { tone: 'none', text: 'Chưa khai — kênh Zalo chưa gửi được' };
  const miss = (features || []).filter(k => !String((st.templates || {})[k] || '').trim()).length;
  if (miss) return { tone: 'warn', text: `Thiếu ${miss} mã mẫu ZNS` };
  return { tone: 'ok', text: 'Đã khai xong' };
}

function ZaloOaConfig({ pushToast }) {
  const Icon = window.Icon;
  const [st, setSt] = dS(null);          // trạng thái từ server
  // KHÔNG còn `mode`. Trước đây có ô chọn "OA riêng / OA nhà cung cấp", nhưng cả hai đòi ĐÚNG bộ
  // bốn thông tin như nhau — khác nhau duy nhất ở chỗ giá trị do công ty tự đăng ký hay do bên cung
  // cấp đưa sẵn. Không dòng code nào ở proxy lẫn worker rẽ nhánh theo nó (đã tra: chỉ lưu rồi đọc
  // ra để in chữ). Bắt người khai chọn một thứ không đổi hành vi gì chỉ tổ làm họ dừng lại phân vân
  // "chọn sai thì sao". Cột `mode` giữ nguyên trong DB cho dòng cũ, server tự mặc định 'own'.
  const [f, setF] = dS({ oaId: '', appId: '', secretKey: '', refreshTokenSeed: '', templates: {} });
  const [saving, setSaving] = dS(false);
  const [dirty, setDirty] = dS(false);
  const [err, setErr] = dS(null);

  const load = dCB(async () => {
    try {
      const d = await dApi('/api/v1/digest/zalo-config');
      setSt(d);
      if (!dirty) setF({
        oaId: d.oaId || '', appId: d.appId || '',
        secretKey: '', refreshTokenSeed: '', templates: d.templates || {},
      });
      setErr(null);
    } catch (e) {
      // 403 = không có quyền cấu hình hệ thống → ẩn hẳn khối, đừng trưng ô nhập rồi báo lỗi lúc lưu.
      setErr(e.message);
    }
  }, [dirty]);

  dE(() => { load(); }, []);

  const set = (patch) => { setF(prev => ({ ...prev, ...patch })); setDirty(true); };
  const setTpl = (k, v) => setF(prev => { const t = { ...prev.templates, [k]: v }; setDirty(true); return { ...prev, templates: t }; });

  if (err && /quyền/i.test(err)) return null;

  const problem = (() => {
    if (!String(f.oaId || '').trim()) return 'Nhập OA ID.';
    if (!String(f.appId || '').trim()) return 'Nhập App ID.';
    if (!String(f.secretKey || '').trim() && !(st && st.hasSecret)) return 'Nhập Secret Key.';
    if (!String(f.refreshTokenSeed || '').trim() && !(st && st.hasRefreshToken)) return 'Nhập Refresh Token.';
    return null;
  })();

  const save = async () => {
    if (problem) { pushToast && pushToast(problem); return; }
    setSaving(true);
    try {
      await dApi('/api/v1/digest/zalo-config', { method: 'PUT', body: JSON.stringify(f) });
      setDirty(false);
      // Xoá bí mật khỏi bộ nhớ giao diện ngay sau khi lưu — không giữ lại trong state.
      setF(prev => ({ ...prev, secretKey: '', refreshTokenSeed: '' }));
      pushToast && pushToast('Đã lưu cấu hình Zalo của công ty');
      load();
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setSaving(false); }
  };

  const remove = async () => {
    setSaving(true);
    try {
      await dApi('/api/v1/digest/zalo-config', { method: 'DELETE' });
      setDirty(false);
      pushToast && pushToast('Đã xoá cấu hình Zalo — kênh Zalo sẽ ngừng gửi');
      load();
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setSaving(false); }
  };

  const features = (st && st.features) || ['sale-brief', 'ceo-brief', 'payment-alert'];

  return (
    <Fold icon="shield" title="Zalo OA của công ty" state={zaloState(st, features)}
      className="digest-block digest-zalo">
      <div className="digest-role-note">
        <Icon name="info" size={12} /> Tin Zalo hiện <b>tên OA người gửi</b>. Chưa khai xong thì kênh
        Zalo không gửi — hệ thống <b>không</b> tự gửi thay bằng OA của đơn vị khác.
      </div>

      {/* Bốn thông tin dưới đây lấy cùng một chỗ nên hướng dẫn để chung một khối, ngay TRƯỚC các ô
          nhập. Đánh số là đúng ở đây: đây thật sự là một chuỗi thao tác, làm sai thứ tự thì bước
          sau không có gì để lấy — chứ không phải đánh số cho ra vẻ có trình tự. */}
      <details className="digest-guide">
        <summary>Lấy bốn thông tin này ở đâu?</summary>
        <ol>
          <li>Mở <b>developers.zalo.me</b> → <b>Ứng dụng</b> → chọn (hoặc tạo mới) ứng dụng của công ty.</li>
          <li>Vào <b>Official Account</b> → liên kết OA của công ty. <b>OA ID</b> hiện ở đây.</li>
          <li>Sang <b>Thông tin ứng dụng</b> → chép <b>App ID</b> và <b>Secret Key</b>.</li>
          <li>Vào <b>Công cụ</b> → <b>Official Account Access Token</b> → bấm cấp quyền cho OA.
            Zalo trả về hai chuỗi; chép chuỗi <b>Refresh Token</b>.</li>
        </ol>
        <p>
          Dùng OA do bên cung cấp dịch vụ đưa sẵn thì bỏ qua bốn bước trên — họ gửi cho bạn đúng bốn
          giá trị này, dán vào là xong.
        </p>
      </details>

      <div className="digest-row">
        <div className="digest-label">OA ID</div>
        <input className="digest-input" value={f.oaId}
          onChange={e => set({ oaId: e.target.value })} placeholder="vd 1234567890123456789" />
      </div>
      <div className="digest-row">
        <div className="digest-label">App ID</div>
        <input className="digest-input" value={f.appId}
          onChange={e => set({ appId: e.target.value })} placeholder="vd 987654321098765432" />
      </div>
      <div className="digest-row">
        <div className="digest-label">Secret Key</div>
        <input className="digest-input" type="password" value={f.secretKey}
          onChange={e => set({ secretKey: e.target.value })}
          placeholder={st && st.hasSecret ? '•••••• (để trống nếu không đổi)' : 'dán Secret Key'} />
      </div>

      {/* Nhãn để ĐÚNG chữ Zalo in trên màn hình của họ ("Refresh Token"), không dịch, không thêm
          chữ "lần đầu". Người khai đang cầm một trang Zalo mở sẵn và tìm bằng mắt — đặt tên khác
          đi là bắt họ tự đoán xem hai thứ có phải một không. Chuyện "chỉ nhập một lần" là hành vi
          của hệ thống, nói ở dòng giải thích bên dưới, không nhét vào nhãn. */}
      <div className="digest-row">
        <div className="digest-label">Refresh Token</div>
        <input className="digest-input" type="password" value={f.refreshTokenSeed}
          onChange={e => set({ refreshTokenSeed: e.target.value })}
          placeholder={st && st.hasRefreshToken ? '•••••• (để trống nếu không đổi)'
            : 'dán Refresh Token lấy ở bước 4'} />
      </div>
      <div className="digest-ch-warn">
        <Icon name="info" size={12} /> Zalo cấp <b>hai</b> chuỗi. Chuỗi Access Token chỉ sống vài giờ
        nên hệ thống không nhận; nó nhận <b>Refresh Token</b> để tự đổi lấy Access Token mới trước mỗi
        lần gửi. Bạn dán <b>một lần duy nhất</b> — từ đó hệ thống tự xoay vòng.
      </div>

      <div className="digest-label" style={{ margin: '10px 0 2px' }}>Mã mẫu ZNS theo từng chức năng</div>
      <div className="digest-role-note">
        <Icon name="info" size={12} /> Zalo duyệt mẫu theo nội dung nên mỗi chức năng một mẫu riêng.
        Chức năng nào bỏ trống thì Zalo của chức năng đó không gửi được.
      </div>
      {features.map(k => (
        <div className="digest-row" key={k}>
          <div className="digest-label">{ZALO_FEATURE_LABELS[k] || k}</div>
          <input className="digest-input" value={(f.templates && f.templates[k]) || ''}
            onChange={e => setTpl(k, e.target.value)} placeholder="mã mẫu ZNS" />
        </div>
      ))}

      {/* Chỉ báo lỗi khi người dùng đã ĐỘNG vào form. Trước đây `problem` hiện ngay lúc mở khối
          chưa khai — mở ra là thấy ngay vạch đỏ "Nhập OA ID." trong khi chưa ai làm gì sai cả.
          Ô trống chưa phải lỗi; lỗi là khi đã điền mà còn thiếu. Tình trạng "chưa khai" đã nói ở
          dòng tiêu đề rồi, không cần nhắc lại bằng màu đỏ. */}
      {dirty && problem && <div className="digest-problem"><Icon name="warning" size={13} /> {problem}</div>}

      <div className="workflows-actions digest-actions">
        <button className="wga-btn primary" onClick={save} disabled={saving || !!problem}>
          <Icon name="save" size={14} /> {saving ? 'Đang lưu…' : 'Lưu cấu hình Zalo'}
        </button>
        {st && st.configured && (
          <button className="wga-btn ghost" onClick={remove} disabled={saving}>Xoá cấu hình</button>
        )}
        {dirty && <span className="digest-dirty">Có thay đổi chưa lưu</span>}
      </div>
    </Fold>
  );
}

// ⚠️ Xuất ra window là BẮT BUỘC — workflows.jsx nhúng khối này qua `window.DigestSubBlock`.
// Dòng này từng bị xoá nhầm (14/08) khi gỡ khối khai OA Zalo ngay bên dưới, và hậu quả im
// lặng tuyệt đối: khối "Bản tin của tôi" biến mất khỏi thẻ tác vụ — không lỗi, không cảnh báo,
// chỉ là không còn chỗ nào đặt giờ nhận và kênh nhận. Thêm/đổi component ở file này thì kiểm
// lại danh sách xuất bên dưới.
window.DigestSubBlock = DigestSubBlock;
window.MyChannelsBlock = MyChannelsBlock;
window.ZaloOaConfig = ZaloOaConfig;
window.digestSummary = digestSummary;
window.DIGEST_BRIEF_TYPES = BRIEF_TYPES;
