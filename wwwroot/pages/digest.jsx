// pages/digest.jsx — Trang "Bản tin AI": đăng ký nhận bản tin sáng.
//
// Mỗi người TỰ khai nơi nhận của mình (email/Telegram/Zalo) và giờ muốn nhận. Phần tài khoản GỬI
// ĐI của công ty (OA Zalo) nằm riêng ở cuối trang vì đó là cấu hình cấp công ty, cần quyền.
//
// Dùng lại design system chung (wga-* + Icon) + helper chung (authedFetch) — không tự chế.
'use strict';

const { useState: dS, useEffect: dE, useCallback: dCB } = React;

// Đủ 24 giờ, KHÔNG bó hẹp về "giờ hành chính".
//
// Bản đầu chỉ cho 5h–20h cho gọn, và nó tạo ra lỗi thật ngay lần xem đầu tiên: một đăng ký đã lưu
// 21:00 khiến ô chọn không có mục nào khớp → trình duyệt hiện mục đầu (05:00). Giao diện NÓI SAI giờ
// đã lưu, mà người dùng không có cách nào biết. Backend nhận 0–23 nên danh sách phải phủ hết 0–23;
// vả lại 21h là lựa chọn hợp lý (đọc bản tin tối trước cho sáng mai).
const HOURS = Array.from({ length: 24 }, (_, i) => i);

const BRIEF_INFO = {
  'sale-brief': {
    icon: 'phone',
    title: 'Bản tin sáng cho nhân viên bán hàng',
    desc: 'Trả lời đúng một câu: sáng nay gọi ai trước. Gồm cơ hội cần gọi, lịch hẹn, việc cần làm, báo giá khách chưa phản hồi.',
    cost: 'Không tốn lượt AI',
  },
  'ceo-brief': {
    icon: 'trend',
    title: 'Bản tin điều hành (giám đốc)',
    desc: 'Doanh thu – chi phí – lợi nhuận so cùng kỳ tháng trước, kèm biến động chính và top nhân viên bán hàng.',
    cost: 'Khoảng 1 lượt AI mỗi lần gửi',
  },
};

const EMPTY_SUB = {
  enabled: false, sendHourLocal: 7,
  channelInApp: true,
  channelEmail: false, email: '',
  channelTelegram: false, telegramChatId: '',
  channelZalo: false, zaloUserId: '',
};

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

// ─── Một thẻ đăng ký ────────────────────────────────────────────────────────────
function BriefCard({ briefType, initial, onSaved, pushToast }) {
  const Icon = window.Icon;
  const info = BRIEF_INFO[briefType];
  const [f, setF] = dS({ ...EMPTY_SUB, ...(initial || {}) });
  const [saving, setSaving] = dS(false);
  const [testing, setTesting] = dS(false);
  const [testResult, setTestResult] = dS(null);
  const [tgCode, setTgCode] = dS(null);
  const [dirty, setDirty] = dS(false);

  // Server là nguồn đúng: sau khi tải lại danh sách thì đồng bộ về, TRỪ khi người dùng đang sửa
  // dở — ghi đè lúc đó là xoá mất thao tác của họ ngay trước mắt.
  dE(() => { if (!dirty) setF({ ...EMPTY_SUB, ...(initial || {}) }); }, [initial]);

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
      pushToast && pushToast('Đã lưu đăng ký');
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
      // Bản tin thử vào Bảng tin ngay → nhắc chuông cập nhật, khỏi đợi hết chu kỳ.
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
    <section className={'digest-card' + (f.enabled ? ' is-on' : '')}>
      <header className="digest-card-head">
        <div className="digest-card-ico"><Icon name={info.icon} size={17} /></div>
        <div className="digest-card-titles">
          <h2>{info.title}</h2>
          <p>{info.desc}</p>
          <span className="digest-cost"><Icon name="sparkle" size={11} /> {info.cost}</span>
        </div>
        <label className="digest-switch" title={f.enabled ? 'Đang bật' : 'Đang tắt'}>
          <input type="checkbox" checked={!!f.enabled} onChange={e => set({ enabled: e.target.checked })} />
          <span>{f.enabled ? 'Bật' : 'Tắt'}</span>
        </label>
      </header>

      <div className="digest-row">
        <div className="digest-label">Giờ nhận <span className="digest-hint">(giờ Việt Nam)</span></div>
        <select className="workflows-select" value={f.sendHourLocal}
          onChange={e => set({ sendHourLocal: parseInt(e.target.value, 10) })}>
          {HOURS.map(h => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
        </select>
      </div>

      <div className="digest-channels">
        <div className="digest-label">Nơi nhận</div>

        <label className="digest-ch">
          <input type="checkbox" checked={!!f.channelInApp} onChange={e => set({ channelInApp: e.target.checked })} />
          <span className="digest-ch-name"><Icon name="bell" size={13} /> Trong app</span>
          <span className="digest-ch-note">Luôn xem lại được ở trang Bảng tin — nên để bật</span>
        </label>

        <label className="digest-ch">
          <input type="checkbox" checked={!!f.channelEmail} onChange={e => set({ channelEmail: e.target.checked })} />
          <span className="digest-ch-name"><Icon name="mail" size={13} /> Email</span>
          <input className="digest-input" type="email" placeholder="ban@congty.vn"
            value={f.email || ''} onChange={e => set({ email: e.target.value })}
            disabled={!f.channelEmail} />
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
            {tgCode.code && <>Nhắn đúng dòng <b>{tgCode.code}</b> cho bot{tgCode.botUsername ? <> <b>@{tgCode.botUsername}</b></> : null}, rồi bấm lại “Tự phát hiện”.</>}
            {!tgCode.code && <>{tgCode.hint}</>}
          </div>
        )}

        <label className="digest-ch">
          <input type="checkbox" checked={!!f.channelZalo} onChange={e => set({ channelZalo: e.target.checked })} />
          <span className="digest-ch-name"><Icon name="user" size={13} /> Zalo</span>
          <input className="digest-input" placeholder="user id Zalo"
            value={f.zaloUserId || ''} onChange={e => set({ zaloUserId: e.target.value })}
            disabled={!f.channelZalo} />
        </label>
        {f.channelZalo && (
          // Giới hạn của Zalo OA, không phải lỗi hệ thống: chỉ nhắn được cho người đã nhắn OA
          // trong 48 giờ. Không nói trước thì người dùng tưởng kênh hỏng.
          <div className="digest-ch-warn">
            <Icon name="info" size={12} /> Zalo chỉ nhận được nếu bạn đã nhắn cho OA của công ty trong 48 giờ gần nhất.
          </div>
        )}
      </div>

      {problem && <div className="digest-problem"><Icon name="warning" size={13} /> {problem}</div>}

      <footer className="digest-card-foot">
        <button className="wga-btn primary" onClick={save} disabled={saving || !!problem}>
          <Icon name="save" size={14} /> {saving ? 'Đang lưu…' : 'Lưu'}
        </button>
        <button className="wga-btn ghost" onClick={sendTest} disabled={testing || dirty}>
          <Icon name="send" size={14} /> {testing ? 'Đang gửi…' : 'Gửi thử'}
        </button>
        {dirty && <span className="digest-dirty">Có thay đổi chưa lưu</span>}
        {testResult && (
          <span className={'digest-test' + (testResult.ok ? ' is-ok' : ' is-bad')}>
            {testResult.ok
              ? <>Đã gửi qua: {testResult.sentChannels}</>
              : <>Không gửi được — {testResult.summary}</>}
          </span>
        )}
      </footer>
      {testResult && testResult.summary && testResult.ok && (
        <div className="digest-test-detail">Chi tiết từng kênh: {testResult.summary}</div>
      )}
    </section>
  );
}

// ─── Cấu hình OA Zalo của công ty ───────────────────────────────────────────────
function ZaloOaBox({ pushToast }) {
  const Icon = window.Icon;
  const [state, setState] = dS(null);
  const [oaId, setOaId] = dS('');
  const [token, setToken] = dS('');
  const [busy, setBusy] = dS(false);

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
    <section className="digest-card digest-oa">
      <header className="digest-card-head">
        <div className="digest-card-ico"><Icon name="shield" size={17} /></div>
        <div className="digest-card-titles">
          <h2>OA Zalo của công ty</h2>
          {/* Vì sao chỉ Zalo phải tự khai: nó tốn tiền và hạn mức tính theo từng OA nên không thể
              dùng tài khoản chung. Telegram và email miễn phí → hệ thống cấp sẵn. */}
          <p>
            Chỉ cần khai nếu công ty muốn gửi bản tin qua Zalo. Zalo tốn tiền và hạn mức tính riêng
            theo từng OA nên không dùng chung được. Telegram và email thì hệ thống lo, bạn không phải khai gì.
          </p>
          <span className="digest-cost"><Icon name="info" size={11} /> Cấu hình dùng cho cả công ty</span>
        </div>
        <span className={'digest-oa-state' + (state && state.configured ? ' is-on' : '')}>
          {state == null ? '…' : (state.configured ? 'Đã cấu hình' : 'Chưa cấu hình')}
        </span>
      </header>

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

      <footer className="digest-card-foot">
        <button className="wga-btn primary" onClick={save} disabled={busy || !oaId.trim() || !token.trim()}>
          <Icon name="save" size={14} /> Lưu
        </button>
        {state && state.configured && (
          <button className="wga-btn ghost" onClick={remove} disabled={busy}>
            <Icon name="trash" size={14} /> Xoá cấu hình
          </button>
        )}
      </footer>
    </section>
  );
}

// ─── Trang ──────────────────────────────────────────────────────────────────────
function DigestPage({ pushToast }) {
  const Icon = window.Icon;
  const [subs, setSubs] = dS(null);
  const [note, setNote] = dS('');
  const [err, setErr] = dS('');
  const canConfig = window.tourkitAuth.hasPermission('CH_HT_XEM');

  const load = dCB(async () => {
    setErr('');
    try {
      const d = await dApi('/api/v1/digest/subscriptions');
      setSubs(d.items || []);
      setNote(d.scopeNote || '');
    } catch (e) { setErr(e.message); setSubs([]); }
  }, []);
  dE(() => { load(); }, [load]);

  const subOf = (t) => (subs || []).find(s => s.briefType === t) || null;

  return (
    <main className="page wga digest-page">
      <div className="wga-head">
        <div>
          <div className="wga-eyebrow">Bản tin · Đăng ký</div>
          <h1>Bản tin AI</h1>
          <p className="wga-sub">
            Chọn bản tin muốn nhận, giờ nhận và nơi nhận. Hệ thống tự gửi mỗi ngày, đúng giờ bạn chọn.
          </p>
        </div>
        <button className="wga-btn ghost" onClick={() => window.tourkitRouter.navigate('/insights')}>
          <Icon name="bell" size={14} /> Xem Bảng tin
        </button>
      </div>

      {/* Nói thẳng phạm vi số liệu: đăng ký bản tin điều hành KHÔNG có nghĩa là thấy hết công ty —
          TourKit vẫn cắt theo quyền của tài khoản. Không nói trước thì người dùng nghi số bị thiếu. */}
      {note && <div className="digest-note"><Icon name="info" size={13} /> {note}</div>}

      {err && <div className="insights-err"><Icon name="warning" size={14} /> {err}</div>}

      {subs == null && <div className="insights-empty">Đang tải…</div>}

      {subs != null && (
        <div className="digest-cards">
          {Object.keys(BRIEF_INFO).map(t => (
            <BriefCard key={t} briefType={t} initial={subOf(t)} onSaved={load} pushToast={pushToast} />
          ))}
          {canConfig && <ZaloOaBox pushToast={pushToast} />}
        </div>
      )}
    </main>
  );
}

window.DigestPage = DigestPage;
