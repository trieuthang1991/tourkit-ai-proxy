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

// ─── Khối đăng ký của một loại bản tin ──────────────────────────────────────────
// scheduleOn: công ty đã bật LỊCH gửi của tác vụ này chưa. Cần biết vì đăng ký của một người và
// lịch của công ty là hai công tắc khác nhau — bật một cái mà thiếu cái kia thì bản tin KHÔNG tới,
// và không có gì trên màn hình nói cho họ hay (đã thấy đúng ca này khi xem thật).
function DigestSubBlock({ briefType, sub, onSaved, pushToast, scheduleOn = true }) {
  const Icon = window.Icon;
  const [f, setF] = dS({ ...EMPTY_SUB, ...(sub || {}) });
  const [saving, setSaving] = dS(false);
  const [testing, setTesting] = dS(false);
  const [testResult, setTestResult] = dS(null);
  const [tgCode, setTgCode] = dS(null);
  const [dirty, setDirty] = dS(false);

  // Server là nguồn đúng: sau khi tải lại thì đồng bộ về, TRỪ khi người dùng đang sửa dở —
  // ghi đè lúc đó là xoá mất thao tác của họ ngay trước mắt.
  dE(() => { if (!dirty) setF({ ...EMPTY_SUB, ...(sub || {}) }); }, [sub]);

  const set = (patch) => { setF(prev => ({ ...prev, ...patch })); setDirty(true); };

  // Kiểm ngay trên máy để nói sớm; server vẫn kiểm lại (không tin client).
  const problem = (() => {
    if (!f.enabled) return null;
    // KHÔNG còn đòi "ít nhất 1 kênh": trong app luôn bật, nên không chọn kênh ngoài nào vẫn nhận được.
    if (f.channelEmail && !String(f.email || '').trim()) return 'Nhập email nhận.';
    if (f.channelTelegram && !String(f.telegramChatId || '').trim()) return 'Nhập chat id Telegram (hoặc bấm Tự phát hiện).';
    if (f.channelZalo && !isVnMobile(f.zaloPhone)) return 'Nhập số điện thoại Zalo (10 số, bắt đầu bằng 0).';
    return null;
  })();

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

  const detectTelegram = async () => {
    try {
      const d = await dApi('/api/v1/digest/telegram/detect', { method: 'POST' });
      setTgCode(d);
      if (d.chatId) { set({ telegramChatId: String(d.chatId).replace(/"/g, '') }); pushToast && pushToast('Đã tìm ra chat id'); }
    } catch (e) {
      // Endpoint trả 502/503 kèm gợi ý khi chưa cấu hình bot — vẫn hiện hướng dẫn, không chỉ báo lỗi.
      setTgCode({ hint: e.message });
    }
  };

  return (
    <div className="workflows-optgroup digest-block">
      <div className="workflows-optgroup-title">Bản tin của tôi</div>

      <div className="digest-ch">
        <input type="checkbox" checked={!!f.enabled} onChange={e => set({ enabled: e.target.checked })}
          id={'dg-on-' + briefType} />
        <label className="digest-ch-name" htmlFor={'dg-on-' + briefType}>Nhận bản tin này</label>
        <span className="digest-ch-note">Chỉ áp dụng cho riêng bạn, không ảnh hưởng người khác</span>
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

      <div className="digest-label" style={{ marginBottom: 2 }}>Nơi nhận</div>

      {/* Khoá bật: "trong app" không phải kênh gửi mà là KHO LƯU — bản tin luôn được ghi vào Bảng
          tin lúc dựng, để còn xem/nghe lại kể cả khi mọi kênh ngoài hỏng. Server cũng ép bật. */}
      <label className="digest-ch">
        <input type="checkbox" checked readOnly disabled />
        <span className="digest-ch-name"><Icon name="bell" size={13} /> Trong app</span>
        <span className="digest-ch-note">Luôn bật — bản tin luôn được lưu ở tab Bảng tin để xem/nghe lại</span>
      </label>

      <label className="digest-ch">
        <input type="checkbox" checked={!!f.channelEmail} onChange={e => set({ channelEmail: e.target.checked })} />
        <span className="digest-ch-name"><Icon name="mail" size={13} /> Email</span>
        <input className="digest-input" type="email" placeholder="ban@congty.vn"
          value={f.email || ''} onChange={e => set({ email: e.target.value })} disabled={!f.channelEmail} />
      </label>

      <label className="digest-ch">
        <input type="checkbox" checked={!!f.channelTelegram} onChange={e => set({ channelTelegram: e.target.checked })} />
        <span className="digest-ch-name"><Icon name="send" size={13} /> Telegram</span>
        <input className="digest-input" placeholder="chat id"
          value={f.telegramChatId || ''} onChange={e => set({ telegramChatId: e.target.value })}
          disabled={!f.channelTelegram} />
        <button type="button" className="wga-btn ghost digest-detect"
          onClick={(e) => { e.preventDefault(); detectTelegram(); }} disabled={!f.channelTelegram}>
          Tự phát hiện
        </button>
      </label>
      {tgCode && (
        <div className="digest-tg-hint">
          {tgCode.code
            ? <>Nhắn đúng dòng <b>{tgCode.code}</b> cho bot{tgCode.botUsername ? <> <b>@{tgCode.botUsername}</b></> : null}, rồi bấm lại “Tự phát hiện”.</>
            : <>{tgCode.hint}</>}
        </div>
      )}

      <label className="digest-ch">
        <input type="checkbox" checked={!!f.channelZalo} onChange={e => set({ channelZalo: e.target.checked })} />
        <span className="digest-ch-name"><Icon name="user" size={13} /> Zalo</span>
        <input className="digest-input" placeholder="Số điện thoại Zalo, vd 0912345678"
          value={f.zaloPhone || ''} onChange={e => set({ zaloPhone: e.target.value })} disabled={!f.channelZalo} />
      </label>
      {f.channelZalo && (
        // Đặc điểm của ZNS, nói trước để khỏi tưởng kênh hỏng: tin Zalo chỉ là lời nhắc ngắn kèm
        // đường dẫn — nội dung đầy đủ nằm ở tab Bảng tin. ZNS gửi theo mẫu đã đăng ký với Zalo nên
        // không chở được cả bản tin dài.
        <div className="digest-ch-warn">
          <Icon name="info" size={12} /> Tin Zalo là lời nhắc ngắn; bản tin đầy đủ đọc ở tab Bảng tin.
          Số phải là số đang dùng Zalo.
        </div>
      )}

      {problem && <div className="digest-problem"><Icon name="warning" size={13} /> {problem}</div>}

      {f.enabled && !scheduleOn && (
        <div className="digest-ch-warn digest-warn-sched">
          <Icon name="warning" size={12} />
          <span>
            Bạn đã bật nhận, nhưng <b>công ty chưa bật lịch gửi</b> bản tin này nên sẽ chưa có gì được
            gửi. Nhờ người quản trị bật ở mục <b>Lịch chạy</b> (cần quyền xem cấu hình).
          </span>
        </div>
      )}

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

// Khối khai OA Zalo của từng công ty đã GỠ (14/08): Zalo nay gửi bằng ZNS qua OA của bên cung
// cấp dịch vụ, khai một lần ở config hệ thống. Trước đây bắt mỗi công ty tự khai vì tin Zalo tính
// tiền theo từng OA; nay bên mình chịu chi phí nên gom về một mối, công ty không phải khai gì.

window.digestSummary = digestSummary;
window.DIGEST_BRIEF_TYPES = BRIEF_TYPES;
