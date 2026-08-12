// pages/digest.jsx — Khối "Bản tin của tôi" + cấu hình OA Zalo.
//
// KHÔNG phải một trang riêng: 2 khối này nhúng vào thẻ tác vụ ở trang Tự động hoá. Lý do (chốt
// 12/08): đăng ký nhận bản tin CHÍNH LÀ cấu hình của tác vụ sale-brief/ceo-brief — tách ra trang
// riêng thì người dùng phải nhớ 2 nơi cho cùng một việc.
//
// Phân vai quyền, giống hệt cách hộp thư (mail-auto-sync) đang làm:
//   • "Bản tin của tôi" — nơi nhận của CHÍNH mình → KHÔNG cần quyền gì.
//   • Lịch chạy (bật/tắt, tần suất) + OA Zalo — cấp công ty → cần quyền xem cấu hình.
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
  channelZalo: false, zaloUserId: '',
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
    const on = [f.channelInApp, f.channelEmail, f.channelTelegram, f.channelZalo].filter(Boolean).length;
    if (on === 0) return 'Chọn ít nhất 1 kênh nhận.';
    if (f.channelEmail && !String(f.email || '').trim()) return 'Nhập email nhận.';
    if (f.channelTelegram && !String(f.telegramChatId || '').trim()) return 'Nhập chat id Telegram (hoặc bấm Tự phát hiện).';
    if (f.channelZalo && !String(f.zaloUserId || '').trim()) return 'Nhập user id Zalo.';
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
      // Bản tin thử vào Bảng tin ngay → nhắc chuông + tab Bảng tin cập nhật, khỏi đợi hết chu kỳ.
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

      <div className="digest-row">
        <div className="digest-label">Giờ nhận <span className="digest-hint">(giờ Việt Nam)</span></div>
        <select className="workflows-select" value={f.sendHourLocal}
          onChange={e => set({ sendHourLocal: parseInt(e.target.value, 10) })}>
          {HOURS.map(h => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
        </select>
      </div>

      <div className="digest-label" style={{ marginBottom: 2 }}>Nơi nhận</div>

      <label className="digest-ch">
        <input type="checkbox" checked={!!f.channelInApp} onChange={e => set({ channelInApp: e.target.checked })} />
        <span className="digest-ch-name"><Icon name="bell" size={13} /> Trong app</span>
        <span className="digest-ch-note">Luôn xem lại được ở tab Bảng tin — nên để bật</span>
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
        <input className="digest-input" placeholder="user id Zalo"
          value={f.zaloUserId || ''} onChange={e => set({ zaloUserId: e.target.value })} disabled={!f.channelZalo} />
      </label>
      {f.channelZalo && (
        // Giới hạn của Zalo OA, không phải lỗi hệ thống: chỉ nhắn được cho người đã nhắn OA trong
        // 48 giờ. Không nói trước thì người dùng tưởng kênh hỏng.
        <div className="digest-ch-warn">
          <Icon name="info" size={12} /> Zalo chỉ nhận được nếu bạn đã nhắn cho OA của công ty trong 48 giờ gần nhất.
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
            {testResult.ok ? <>Đã gửi qua: {testResult.sentChannels}</> : <>Không gửi được — {testResult.summary}</>}
          </span>
        )}
      </div>
      {testResult && testResult.ok && testResult.summary && (
        <div className="digest-test-detail">Chi tiết từng kênh: {testResult.summary}</div>
      )}
    </div>
  );
}

// ─── Cấu hình OA Zalo của công ty (cần quyền xem cấu hình) ──────────────────────
function DigestZaloBox({ pushToast }) {
  const Icon = window.Icon;
  const [state, setState] = dS(null);
  const [oaId, setOaId] = dS('');
  const [token, setToken] = dS('');
  const [busy, setBusy] = dS(false);
  const [open, setOpen] = dS(false);

  const load = dCB(async () => {
    try { setState(await dApi('/api/v1/digest/zalo-config')); } catch { setState({ configured: false }); }
  }, []);
  dE(() => { load(); }, [load]);

  const save = async () => {
    setBusy(true);
    try {
      await dApi('/api/v1/digest/zalo-config', { method: 'PUT', body: JSON.stringify({ oaId, accessToken: token }) });
      setToken(''); pushToast && pushToast('Đã lưu cấu hình OA Zalo'); load();
    } catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setBusy(false); }
  };

  const remove = async () => {
    setBusy(true);
    try { await dApi('/api/v1/digest/zalo-config', { method: 'DELETE' }); pushToast && pushToast('Đã xoá cấu hình'); load(); }
    catch (e) { pushToast && pushToast('Lỗi: ' + e.message); }
    finally { setBusy(false); }
  };

  return (
    <div className="digest-oa">
      <div className="digest-oa-head" onClick={() => setOpen(v => !v)} role="button" tabIndex={0}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(v => !v); } }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <Icon name="shield" size={15} /> <b>OA Zalo của công ty</b>
        </span>
        <span className={'digest-oa-state' + (state && state.configured ? ' is-on' : '')}>
          {state == null ? '…' : (state.configured ? 'Đã cấu hình' : 'Chưa cấu hình')}
        </span>
        <Icon name={open ? 'chevronUp' : 'chevronDown'} size={15} />
      </div>

      {open && (
        <div className="digest-oa-body">
          {/* Vì sao CHỈ Zalo phải tự khai: nó tốn tiền và hạn mức tính theo từng OA nên không thể
              dùng tài khoản chung. Telegram và email miễn phí → hệ thống cấp sẵn. */}
          <p className="workflows-opt-hint" style={{ marginTop: 0 }}>
            Chỉ cần khai nếu công ty muốn gửi bản tin qua Zalo. Zalo tốn tiền và hạn mức tính riêng theo
            từng OA nên không dùng chung được. Telegram và email thì hệ thống lo, bạn không phải khai gì.
          </p>

          {state && state.configured && (
            <div className="digest-row"><div className="digest-label">OA Id hiện tại</div><code>{state.oaId}</code></div>
          )}
          <div className="digest-row">
            <div className="digest-label">OA Id</div>
            <input className="digest-input" value={oaId} onChange={e => setOaId(e.target.value)} placeholder="vd 1234567890" />
          </div>
          <div className="digest-row">
            <div className="digest-label">Access Token</div>
            {/* Không bao giờ hiện lại token đã lưu (server cũng không trả) — nhập là ghi đè. */}
            <input className="digest-input" type="password" value={token} onChange={e => setToken(e.target.value)}
              placeholder={state && state.configured ? 'Nhập token mới để thay' : 'dán access token OA'} />
          </div>
          <div className="workflows-actions">
            <button className="wga-btn primary" onClick={save} disabled={busy || !oaId.trim() || !token.trim()}>
              <Icon name="save" size={14} /> Lưu
            </button>
            {state && state.configured && (
              <button className="wga-btn ghost" onClick={remove} disabled={busy}>
                <Icon name="trash" size={14} /> Xoá cấu hình
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

window.DigestSubBlock = DigestSubBlock;
window.DigestZaloBox = DigestZaloBox;
window.digestSummary = digestSummary;
window.DIGEST_BRIEF_TYPES = BRIEF_TYPES;
