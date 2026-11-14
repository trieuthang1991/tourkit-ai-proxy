// scripts/e2e/mcp-oauth-e2e.mjs
// KIỂM ĐẦU-CUỐI OAuth của /mcp (Claude.ai / ChatGPT) bằng HTTP thật + trang đồng ý qua Playwright.
// Phủ luồng chuẩn (A) và 5 nhóm ca bảo mật trong plan 2026-11-14-mcp-oauth-claude-chatgpt (B–F),
// header chống nhúng khung của trang đồng ý (G) và khoá tay tkmcp_ (H). Mỗi ca in một dòng PASS/FAIL/SKIP.
//
// Cách chạy (máy dev, CHỈ tenant staging — không bao giờ trỏ phiên erp):
//   1. dotnet build TourkitAiProxy.csproj
//   2. Features__Mcp=true Features__McpOAuth=true Mcp__PublicBaseUrl=http://localhost:5092 \
//        dotnet run --no-build --project TourkitAiProxy.csproj --urls http://localhost:5092
//      (cổng riêng — đừng dùng 5080 của người khác; dist/ cũ thì chạy build-frontend.ps1 trước)
//   3. node scripts/e2e/mcp-oauth-e2e.mjs --sid <phiên staging> [--sid2 <phiên staging người thứ hai>]
//        [--base http://localhost:5092] [--db-cmd <exe nhận 1 câu SQL, in JSON>] [--fast]
//      Hoặc biến môi trường: E2E_BASE, E2E_SID, E2E_SID2, E2E_DB_CMD.
//
//   --sid      phiên TRAV-AI (localStorage tourkit_tk_session) — bắt buộc, không viết cứng trong script.
//   --sid2     phiên người thứ hai cùng tenant (ca F2). Thiếu → SKIP.
//   --db-cmd   chương trình chạy SQL trên TourKit_Push (vd harness nhỏ dùng TourkitAiDb + appsettings.json local):
//              nhận ĐÚNG 1 đối số là câu SQL, in mảng JSON các dòng. Cần cho ca ép hết hạn/sai resource (C6, E3–E6)
//              và để XOÁ SẠCH dòng test cuối lượt. Thiếu → các ca đó SKIP, cuối lượt chỉ thu hồi qua API.
//              Mọi câu ghi đều kẹp dòng do CHÍNH lượt chạy tạo (ClientName/Label bắt đầu 'T6 e2e').
//   --fast     ca C7b mà hồi quy (access còn 200 sau thu hồi) thì báo FAIL ngay, không chờ đo thời gian sống ~60 s.
//
// Lưu ý: DCR giới hạn 20 client/giờ/IP; mỗi lượt đăng ký 2 client (xoá khi có --db-cmd nên không dồn).
// Token in ra luôn bị che (giữ tiền tố + 4 ký tự). Thoát mã 1 nếu có ca FAIL.

import { createRequire } from 'module';
import { fileURLToPath } from 'url';
import path from 'path';
import crypto from 'crypto';
import { execFileSync } from 'child_process';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const require = createRequire(path.join(ROOT, 'e2e', 'package.json'));
const { chromium } = require('playwright');

// ── Tham số ────────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
const arg = (name, env) => { const i = argv.indexOf('--' + name); return i >= 0 ? argv[i + 1] : process.env[env]; };
const BASE = (arg('base', 'E2E_BASE') || 'http://localhost:5092').replace(/\/+$/, '');
const SID = arg('sid', 'E2E_SID');
const SID2 = arg('sid2', 'E2E_SID2');
const DB_CMD = arg('db-cmd', 'E2E_DB_CMD');
const FAST = argv.includes('--fast');
if (!SID) { console.error('Thiếu --sid (hoặc E2E_SID): phiên TRAV-AI staging'); process.exit(2); }

const RESOURCE = BASE + '/mcp';
const CB = 'http://localhost:6274/oauth/callback';
const CLAUDE_CB = 'https://claude.ai/api/mcp/auth_callback';
const RUN = 'T6 e2e ' + new Date().toISOString().slice(0, 19);
const PRM = BASE + '/.well-known/oauth-protected-resource';
const EXPECT_WWW = `Bearer resource_metadata="${PRM}", scope="tourkit.read"`;

// ── Ghi kết quả ────────────────────────────────────────────────────────────
const results = [];
const mask = s => String(s).replace(/(tk(?:oat|ort|oac|mcp)_)[0-9a-f]{32}/g, (m, p) => p + m.slice(p.length, p.length + 4) + '…')
  .replace(new RegExp([SID, SID2].filter(Boolean).join('|') || '$^', 'g'), x => x.slice(0, 4) + '…');
const show = o => mask(typeof o === 'string' ? o : JSON.stringify(o));
function record(id, status, title, detail) {
  results.push({ id, status, title });
  console.log(`${status.padEnd(4)} ${id.padEnd(5)} ${title}${detail !== undefined ? '  — ' + show(detail) : ''}`);
}
const check = (id, ok, title, detail) => record(id, ok ? 'PASS' : 'FAIL', title, detail);
const skip = (id, title, why) => record(id, 'SKIP', title, why);
const info = (title, detail) => console.log(`INFO       ${title}  — ${show(detail)}`);

// ── HTTP ───────────────────────────────────────────────────────────────────
const b64u = b => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const sha = s => crypto.createHash('sha256').update(s, 'utf8').digest('hex');
const pkce = () => { const v = b64u(crypto.randomBytes(32)); return { verifier: v, challenge: b64u(crypto.createHash('sha256').update(v).digest()) }; };
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function getJson(p, headers = {}) {
  const r = await fetch(BASE + p, { headers, redirect: 'manual' });
  let body = null; try { body = await r.json(); } catch { /* không phải JSON */ }
  return { status: r.status, headers: r.headers, body };
}

async function mcp(token, method, params = {}, proto) {
  const headers = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' };
  if (token != null) headers.Authorization = 'Bearer ' + token;
  if (proto) headers['MCP-Protocol-Version'] = proto;
  const r = await fetch(RESOURCE, { method: 'POST', headers, body: JSON.stringify({ jsonrpc: '2.0', id: Date.now(), method, params }) });
  const text = await r.text();
  const data = text.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5).trim()).pop();
  let body; try { body = JSON.parse(data ?? text); } catch { body = text; }
  return { status: r.status, www: r.headers.get('www-authenticate'), body };
}
const is401 = r => r.status === 401 && r.www === EXPECT_WWW;
let PROTO;   // protocolVersion từ initialize
const toolsList = t => mcp(t, 'tools/list', {}, PROTO);

async function token(form) {
  const r = await fetch(BASE + '/oauth/token', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(form) });
  let body = null; try { body = await r.json(); } catch { /* */ }
  return { status: r.status, body };
}
const isInvalidGrant = t => t.status === 400 && t.body?.error === 'invalid_grant';

async function register(name, redirectUris) {
  const r = await fetch(BASE + '/oauth/register', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ client_name: name, redirect_uris: redirectUris, token_endpoint_auth_method: 'none', grant_types: ['authorization_code', 'refresh_token'], response_types: ['code'] }) });
  return { status: r.status, body: await r.json().catch(() => null) };
}

function authQuery(clientId, over = {}) {
  const q = { response_type: 'code', client_id: clientId, redirect_uri: CB, code_challenge: over.challenge, code_challenge_method: 'S256',
    state: 'st-' + crypto.randomBytes(4).toString('hex'), scope: 'tourkit.read offline_access', resource: RESOURCE, ...over };
  delete q.challenge;
  for (const k of Object.keys(q)) if (q[k] === undefined) delete q[k];
  return '?' + new URLSearchParams(q).toString();
}

async function decide(kind, query, headers = { 'X-Session-Id': SID }, opts = {}) {
  const r = await fetch(BASE + '/api/v1/mcp/oauth/' + kind + (opts.qs || ''), { method: 'POST',
    headers: { 'Content-Type': opts.contentType || 'application/json', ...headers }, body: JSON.stringify({ query }) });
  return { status: r.status, body: await r.json().catch(() => null) };
}
const authorizeInfo = q => getJson('/api/v1/mcp/oauth/authorize-info' + q);

/** Duyệt qua API (như nút Cho phép) → mã. */
async function approveCode(clientId, { sid = SID, redirect = CB } = {}) {
  const p = pkce();
  const q = authQuery(clientId, { challenge: p.challenge, redirect_uri: redirect });
  const r = await decide('approve', q, { 'X-Session-Id': sid });
  if (r.status !== 200) throw new Error('approve ' + r.status + ' ' + show(r.body));
  return { ...p, code: new URL(r.body.redirectUrl).searchParams.get('code'), redirect };
}
async function newGrant(clientId, sid = SID) {
  const c = await approveCode(clientId, { sid });
  const t = await token({ grant_type: 'authorization_code', code: c.code, code_verifier: c.verifier, redirect_uri: CB, client_id: clientId, resource: RESOURCE });
  if (t.status !== 200) throw new Error('token ' + t.status + ' ' + show(t.body));
  return { access: t.body.access_token, refresh: t.body.refresh_token };
}

const api = (p, opt = {}, sid = SID) => fetch(BASE + p, { ...opt, headers: { 'X-Session-Id': sid, ...(opt.headers || {}) } });
async function connections(sid = SID) { return (await (await api('/api/v1/mcp/oauth/connections', {}, sid)).json()).items || []; }

// ── CSDL (tuỳ chọn) ────────────────────────────────────────────────────────
function sql(statement) {
  if (!DB_CMD) throw new Error('không có --db-cmd');
  // Một dòng: đối số nhiều dòng qua dòng lệnh Windows dễ bị cắt.
  return JSON.parse(execFileSync(DB_CMD, [statement.replace(/\s+/g, ' ')], { encoding: 'utf8' }).trim().split('\n').pop());
}
const mineGrant = `ClientName LIKE N'T6 e2e%'`;
const COUNTS = `SELECT (SELECT COUNT(*) FROM dbo.McpOAuthClients WHERE ClientName LIKE N'T6 e2e%') clients,
 (SELECT COUNT(*) FROM dbo.McpOAuthCodes WHERE ClientId IN (SELECT ClientId FROM dbo.McpOAuthClients WHERE ClientName LIKE N'T6 e2e%')) codes,
 (SELECT COUNT(*) FROM dbo.McpOAuthGrants WHERE ${mineGrant}) grants,
 (SELECT COUNT(*) FROM dbo.McpTokens WHERE Label LIKE N'T6 e2e%') tokens`;
const hex = s => { if (!/^[0-9a-f]{64}$/.test(s)) throw new Error('băm lạ'); return s; };
const grantWhere = access => `AccessHash = '${hex(sha(access))}' AND ${mineGrant}`;

// ═══════════════════════════════════════════════════════════════════════════
async function main() {
  console.log(`MCP OAuth e2e — base ${BASE}, chạy "${RUN}", db ${DB_CMD ? 'có' : 'không'}${FAST ? ', --fast' : ''}`);
  if (DB_CMD) info('CSDL trước lượt (dòng T6 e2e)', sql(COUNTS)[0]);
  const browser = await chromium.launch({ channel: 'chrome' }).catch(() => chromium.launch());
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await ctx.addInitScript(sid => { localStorage.setItem('tourkit_tk_session', sid); localStorage.setItem('tourkit_skip_login_gate', '1'); }, SID);
  let callbackUrl = null, foreignNav = [];
  await ctx.route('http://localhost:6274/**', route => { callbackUrl = route.request().url(); route.fulfill({ status: 200, body: 'ok' }); });
  await ctx.route(/^https:\/\/(claude\.ai|chatgpt\.com|evil\.example|CLAUDE\.AI)\//i, route => { foreignNav.push(route.request().url()); route.fulfill({ status: 200, body: 'foreign' }); });
  const page = await ctx.newPage();

  try {
    // ── A. Luồng chuẩn ─────────────────────────────────────────────────────
    const pr = await getJson('/.well-known/oauth-protected-resource');
    const pr2 = await getJson('/.well-known/oauth-protected-resource/mcp');
    const as = await getJson('/.well-known/oauth-authorization-server');
    const oidc = await getJson('/.well-known/openid-configuration');
    const a = as.body || {};
    const metaOk = pr.status === 200 && pr2.status === 200 && as.status === 200 && oidc.status === 200
      && pr.body.resource === RESOURCE && JSON.stringify(pr.body) === JSON.stringify(pr2.body)
      && (pr.body.authorization_servers || []).includes(BASE)
      && a.issuer === BASE && a.authorization_endpoint === BASE + '/oauth/authorize' && a.token_endpoint === BASE + '/oauth/token'
      && a.registration_endpoint === BASE + '/oauth/register' && JSON.stringify(a.code_challenge_methods_supported) === '["S256"]'
      && (a.token_endpoint_auth_methods_supported || []).includes('none') && a.client_id_metadata_document_supported === true
      && a.authorization_response_iss_parameter_supported === true && JSON.stringify(a) === JSON.stringify(oidc.body);
    check('A1', metaOk, 'metadata (PR gốc + /mcp, AS, openid-configuration) nhất quán', { resource: pr.body?.resource, issuer: a.issuer, pkce: a.code_challenge_methods_supported, cimd: a.client_id_metadata_document_supported, iss: a.authorization_response_iss_parameter_supported });

    const regA = await register(RUN + ' A', [CB, CLAUDE_CB]);
    const regB = await register(RUN + ' B', [CB]);
    const A = regA.body?.client_id, B = regB.body?.client_id;
    check('A2', regA.status === 201 && regB.status === 201 && /^tkdcr_/.test(A) && /^tkdcr_/.test(B), 'DCR 2 client (A: loopback + claude.ai, B: loopback)', { A: regA.status, B: regB.status });
    if (!A || !B) throw new Error('DCR hỏng — dừng (có thể chạm giới hạn 20 client/giờ/IP)');

    const un = await mcp(null, 'tools/list');
    check('A3', is401(un), '/mcp không token → 401 + WWW-Authenticate đúng từng chữ', { status: un.status, www: un.www });

    const p0 = pkce();
    const q0 = authQuery(A, { challenge: p0.challenge, state: 'state-A' });
    await page.goto(BASE + '/oauth/authorize' + q0);
    const btn = page.getByRole('button', { name: 'Cho phép' });
    await btn.waitFor({ timeout: 15000 });
    const t0 = Date.now();
    await btn.click();
    while (!callbackUrl && Date.now() - t0 < 10000) await sleep(100);
    const cb = callbackUrl ? new URL(callbackUrl) : null;
    const code0 = cb?.searchParams.get('code');
    check('A4', cb && cb.origin + cb.pathname === CB && /^tkoac_[0-9a-f]{32}$/.test(code0) && cb.searchParams.get('state') === 'state-A' && cb.searchParams.get('iss') === BASE,
      'trang đồng ý → Cho phép → redirect có code + state + iss', { url: cb && cb.origin + cb.pathname, code: code0, state: cb?.searchParams.get('state'), iss: cb?.searchParams.get('iss') });

    const t1 = await token({ grant_type: 'authorization_code', code: code0, code_verifier: p0.verifier, redirect_uri: CB, client_id: A, resource: RESOURCE });
    check('A5', t1.status === 200 && /^tkoat_/.test(t1.body.access_token) && /^tkort_/.test(t1.body.refresh_token) && t1.body.token_type === 'Bearer' && t1.body.expires_in === 3600,
      'đổi mã → access + refresh', t1.body);
    const A1 = t1.body.access_token, R1 = t1.body.refresh_token;

    const init = await mcp(A1, 'initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'mcp-oauth-e2e', version: '1' } });
    PROTO = init.body?.result?.protocolVersion;
    const list = await toolsList(A1);
    const tools = list.body?.result?.tools || [];
    const call = await mcp(A1, 'tools/call', { name: 'cashflow', arguments: { startDate: '2026-11-01', endDate: '2026-11-14', groupBy: 'day' } }, PROTO);
    const txt = call.body?.result?.content?.[0]?.text || '';
    check('A6', init.status === 200 && PROTO && list.status === 200 && tools.length === 20 && call.status === 200 && !call.body?.result?.isError && txt.length > 0,
      'initialize + tools/list (=20) + tools/call cashflow', { proto: PROTO, tools: tools.length, isError: call.body?.result?.isError, text: txt.slice(0, 80) });

    const t2 = await token({ grant_type: 'refresh_token', refresh_token: R1, client_id: A });
    const A2 = t2.body?.access_token;
    const l2 = await toolsList(A2);
    check('A7', t2.status === 200 && A2 !== A1 && t2.body.refresh_token !== R1 && l2.status === 200, 'refresh xoay vòng → cặp mới, access mới chạy', { status: t2.status, newPair: A2 !== A1 && t2.body?.refresh_token !== R1, mcp: l2.status });

    const mine = (await connections()).filter(x => x.clientName === RUN + ' A' && !x.revokedUtc).sort((x, y) => y.id - x.id)[0];
    const del = await api('/api/v1/mcp/oauth/connections/' + mine?.id, { method: 'DELETE' });
    const tDel = Date.now();
    const after = await toolsList(A2);
    check('A8', del.status === 204 && is401(after), 'DELETE kết nối → /mcp 401 ngay (access đang trong đệm)', { del: del.status, mcp: after.status, ms: Date.now() - tDel, www: after.www });

    // ── B. Focus 1 — redirect ──────────────────────────────────────────────
    const badRedirects = [
      ['B1', 'https://evil.example/cb', 'ngoài danh sách trắng'],
      ['B2', CLAUDE_CB + '/x', 'claude callback + /x'],
      ['B3', 'https://CLAUDE.AI/api/mcp/auth_callback', 'host chữ hoa'],
      ['B4', 'https://claude.ai/API/mcp/auth_callback', 'path chữ hoa'],
      ['B5', CLAUDE_CB + '?a=1', 'claude callback + query'],
      ['B6', CB + '/x', 'loopback + /x'],
      ['B7', CB + '?x=1', 'loopback + query'],
      ['B8', 'https://chatgpt.com/connector_platform_oauth_redirect', 'trong danh sách trắng nhưng KHÁC cái đã đăng ký'],
      ['B9', 'http://claude.ai/api/mcp/auth_callback', 'http thay https'],
    ];
    for (const [id, uri, why] of badRedirects) {
      const p = pkce();
      const q = authQuery(A, { challenge: p.challenge, redirect_uri: uri });
      const ai = await authorizeInfo(q);
      const raw = await fetch(BASE + '/oauth/authorize' + q, { redirect: 'manual' });
      const ap = await decide('approve', q);
      const dn = await decide('deny', q);
      foreignNav = [];
      await page.goto(BASE + '/oauth/authorize' + q);
      await page.locator('.oc-err, button:has-text("Cho phép")').first().waitFor({ timeout: 10000 }).catch(() => {});
      const consentShown = await page.getByRole('button', { name: 'Cho phép' }).count();
      const errText = await page.locator('.oc-err').textContent().catch(() => null);
      const ok = ai.body?.kind === 'error' && !ai.body.url && raw.status === 200 && !raw.headers.get('location')
        && ap.status === 400 && !ap.body?.redirectUrl && dn.status === 400 && !dn.body?.redirectUrl
        && consentShown === 0 && !!errText && foreignNav.length === 0 && page.url().startsWith(BASE);
      check(id, ok, `redirect ${why} → lỗi tại chỗ, không chuyển hướng`, { uri, info: ai.body?.kind, authorize: raw.status, approve: ap.status, deny: dn.status, consent: consentShown, nav: foreignNav.length });
    }
    {
      // Đối chứng: client + redirect đúng, tham số khác sai → được phép báo lỗi VỀ client (có iss).
      const q = authQuery(A, { challenge: pkce().challenge, code_challenge_method: 'plain' });
      const ai = await authorizeInfo(q);
      const u = ai.body?.url ? new URL(ai.body.url) : null;
      check('B10', ai.body?.kind === 'redirect' && u?.origin + u?.pathname === CB && u.searchParams.get('error') === 'invalid_request' && u.searchParams.get('iss') === BASE,
        'đối chứng: PKCE plain với redirect hợp lệ → redirect lỗi về đúng callback kèm iss', ai.body);
      const dupe = await authorizeInfo(q.replace('?', '?redirect_uri=' + encodeURIComponent('https://evil.example/cb') + '&'));
      check('B11', dupe.body?.kind === 'error' && !dupe.body.url, 'redirect_uri lặp (1 tốt + 1 lạ) → lỗi tại chỗ', dupe.body?.kind);
    }

    // ── C. Focus 2 — mã / PKCE / refresh ───────────────────────────────────
    {
      const c = await approveCode(A);
      const f = { grant_type: 'authorization_code', code: c.code, code_verifier: c.verifier, redirect_uri: CB, client_id: A, resource: RESOURCE };
      const first = await token(f), second = await token(f);
      check('C1', first.status === 200 && isInvalidGrant(second), 'mã dùng 2 lần → lần 2 invalid_grant', { first: first.status, second: second.body });
    }
    {
      const c = await approveCode(A);
      const f = { grant_type: 'authorization_code', code: c.code, code_verifier: c.verifier, redirect_uri: CB, resource: RESOURCE };
      const byB = await token({ ...f, client_id: B }), byA = await token({ ...f, client_id: A });
      check('C2', isInvalidGrant(byB) && isInvalidGrant(byA), 'mã của A đổi ở B → invalid_grant (mã bị đốt, A cũng không đổi lại được)', { byB: byB.body, byAafter: byA.body });
    }
    for (const [id, title, mut] of [
      ['C3', 'sai code_verifier', (f, c) => ({ ...f, code_verifier: pkce().verifier })],
      ['C4', 'sai redirect_uri (loopback khác path)', f => ({ ...f, redirect_uri: 'http://localhost:6274/oauth/other' })],
      ['C5', 'sai resource', f => ({ ...f, resource: BASE + '/other' })],
    ]) {
      const c = await approveCode(A);
      const f = { grant_type: 'authorization_code', code: c.code, code_verifier: c.verifier, redirect_uri: CB, client_id: A, resource: RESOURCE };
      const bad = await token(mut(f, c)), retry = await token(f);
      check(id, isInvalidGrant(bad) && isInvalidGrant(retry), `${title} → invalid_grant (và mã đã chết cho lần đúng sau đó)`, { bad: bad.body, retryCorrect: retry.body?.error });
    }
    if (DB_CMD) {
      const c = await approveCode(A);
      const n = sql(`UPDATE dbo.McpOAuthCodes SET ExpiresUtc = DATEADD(minute, -6, SYSUTCDATETIME()) OUTPUT inserted.ClientId
        WHERE CodeHash = '${hex(sha(c.code))}' AND ClientId = '${A}'`).length;
      const t = await token({ grant_type: 'authorization_code', code: c.code, code_verifier: c.verifier, redirect_uri: CB, client_id: A, resource: RESOURCE });
      check('C6', n === 1 && isInvalidGrant(t), 'mã quá 5 phút (ép ExpiresUtc qua CSDL) → invalid_grant', { updated: n, token: t.body });
    } else skip('C6', 'mã quá 5 phút', 'cần --db-cmd; unit test Ma_qua_5_phut_thi_invalid_grant');
    {
      const g = await newGrant(A);
      const r1 = await token({ grant_type: 'refresh_token', refresh_token: g.refresh, client_id: A });
      const nA = r1.body.access_token, nR = r1.body.refresh_token;
      const warm = await toolsList(nA);                       // nạp đệm xác thực cho access mới
      const reuse = await token({ grant_type: 'refresh_token', refresh_token: g.refresh, client_id: A });
      const tReuse = Date.now();
      const nowA = await toolsList(nA);
      const nextR = await token({ grant_type: 'refresh_token', refresh_token: nR, client_id: A });
      let revokedDb = null;
      if (DB_CMD) revokedDb = sql(`SELECT RevokedUtc FROM dbo.McpOAuthGrants WHERE RefreshHash = '${hex(sha(nR))}' AND ${mineGrant}`)[0]?.RevokedUtc ?? null;
      check('C7', r1.status === 200 && warm.status === 200 && isInvalidGrant(reuse) && isInvalidGrant(nextR) && (DB_CMD ? !!revokedDb : true),
        'dùng lại refresh cũ → invalid_grant + cả grant bị thu hồi (refresh mới cũng chết)', { reuse: reuse.body?.error, newRefresh: nextR.body?.error, revokedUtc: revokedDb });
      // Access mới đã nằm trong đệm 60 s: service thu hồi grant phải xoá đệm + ghi tombstone → 401 NGAY (cùng instance).
      if (nowA.status === 401) check('C7b', is401(nowA), 'access mới 401 NGAY sau khi grant bị thu hồi do dùng lại refresh', { ms: Date.now() - tReuse });
      else if (FAST) record('C7b', 'FAIL', 'access mới vẫn 200 ngay sau thu hồi do dùng lại refresh (đệm xác thực chưa bị xoá)', { status: nowA.status, note: '--fast: không đo thời gian sống' });
      else {
        // Hồi quy: đo xem token còn sống bao lâu để chẩn đoán (trước bản sửa là ~61 s).
        let st = nowA.status, ms = 0;
        while (st !== 401 && Date.now() - tReuse < 75000) { await sleep(5000); st = (await toolsList(nA)).status; ms = Date.now() - tReuse; }
        record('C7b', 'FAIL', 'access mới vẫn 200 sau thu hồi do dùng lại refresh — đệm xác thực chưa bị xoá',
          { immediately: nowA.status, became401AfterMs: st === 401 ? ms : null, finalStatus: st });
      }
    }

    // ── D. Focus 3 — SSRF qua CIMD ─────────────────────────────────────────
    for (const [id, cid] of [
      ['D1', 'https://169.254.169.254/x'], ['D2', 'http://claude.ai/x'], ['D3', 'https://evil.example/x'],
      ['D4', 'https://claude.ai.evil.example/x'], ['D5', 'https://claude.ai:8443/x'], ['D6', 'https://user@claude.ai/x'],
      ['D7', 'https://127.0.0.1/x'], ['D8', 'https://localhost/x'], ['D9', 'https://claude.ai/x?y=1'],
    ]) {
      const t = Date.now();
      const ai = await authorizeInfo(authQuery(cid, { challenge: pkce().challenge, redirect_uri: CLAUDE_CB }));
      const ms = Date.now() - t;
      const tk = await token({ grant_type: 'authorization_code', code: 'tkoac_' + '0'.repeat(32), code_verifier: pkce().verifier, redirect_uri: CLAUDE_CB, client_id: cid });
      check(id, ai.body?.kind === 'error' && !ai.body.url && ms < 5000 && tk.status === 400 && tk.body?.error === 'invalid_client',
        `CIMD client_id ${cid} → từ chối, không gọi ra ngoài`, { info: ai.body?.kind, ms, token: tk.body?.error });
    }
    {
      // Host được phép nhưng tài liệu không có → máy chủ có gọi ra claude.ai thật; phải trả lỗi trong ≤ 5 s.
      const t = Date.now();
      const ai = await authorizeInfo(authQuery('https://claude.ai/t6-e2e-khong-ton-tai.json', { challenge: pkce().challenge, redirect_uri: CLAUDE_CB }));
      const ms = Date.now() - t;
      check('D10', ai.body?.kind === 'error' && ms < 6000, 'CIMD host đúng nhưng URL không có tài liệu → lỗi, ≤ 5 s', { info: ai.body?.kind, ms });
    }
    skip('D11', 'CIMD chuyển hướng sang host lạ / tài liệu 10 MB / client_id khác URL',
      'không dựng được tài liệu trên claude.ai từ máy dev → phủ bởi unit test ClientMetadataFetcherTests: Chuyen_huong_bi_tu_choi_khong_di_theo, Phan_hoi_tu_URL_khac_bi_tu_choi, Tai_lieu_qua_64KB_bi_tu_choi_va_ngung_doc, Content_Length_qua_lon_bi_tu_choi, Khong_phai_JSON_hoac_client_id_khac_URL_bi_tu_choi, Treo_qua_han_thi_bo_khong_cho_vo_han');
    // CIMD THẬT của Claude và ChatGPT (máy chủ gọi ra Internet): phải hiện được trang đồng ý, không thì
    // connector thật hỏng ngay bước đầu. Mất mạng → hai ca này FAIL kèm ms, đọc message để phân biệt.
    for (const [id, name, cid, redirect] of [
      ['D12', 'Claude', 'https://claude.ai/oauth/mcp-oauth-client-metadata', CLAUDE_CB],
      ['D13', 'ChatGPT', 'https://chatgpt.com/oauth/client.json', 'https://chatgpt.com/connector_platform_oauth_redirect'],
    ]) {
      const t = Date.now();
      const ai = await authorizeInfo(authQuery(cid, { challenge: pkce().challenge, redirect_uri: redirect }));
      check(id, ai.body?.kind === 'consent', `CIMD thật của ${name} (${cid}) → trang đồng ý`, { kind: ai.body?.kind, clientName: ai.body?.clientName, message: ai.body?.message, ms: Date.now() - t });
    }

    // ── E. Focus 4 — token sai đối tượng ───────────────────────────────────
    {
      const g = await newGrant(A);
      const flipped = g.access.slice(0, -1) + (g.access.endsWith('0') ? '1' : '0');
      const cases = [['E1a', 'tkoat_ rác', 'tkoat_zzzz'], ['E1b', 'tkoat_ thiếu 1 ký tự', g.access.slice(0, -1)], ['E1c', 'tkoat_ chữ hoa', g.access.toUpperCase().replace('TKOAT_', 'tkoat_')],
        ['E1d', 'tkoat_ đúng định dạng nhưng không tồn tại', 'tkoat_' + crypto.randomBytes(16).toString('hex')], ['E2', 'token bị sửa 1 ký tự', flipped],
        ['E1e', 'refresh token dùng làm access', g.refresh]];
      for (const [id, title, tk] of cases) { const r = await toolsList(tk); check(id, is401(r), `${title} → 401 + resource_metadata`, { status: r.status }); }
      const ok = await toolsList(g.access);
      check('E0', ok.status === 200, 'đối chứng: access thật cùng grant vẫn 200', { status: ok.status });
    }
    if (DB_CMD) {
      for (const [id, title, set] of [
        ['E3', 'grant bị thu hồi (RevokedUtc qua CSDL, access chưa vào đệm)', 'RevokedUtc = SYSUTCDATETIME()'],
        ['E4', 'access hết hạn (AccessExpiresUtc lùi 1 phút)', 'AccessExpiresUtc = DATEADD(minute, -1, SYSUTCDATETIME())'],
        ['E5', 'access cấp cho resource khác', `Resource = N'${BASE}/other'`],
        ['E6', 'phiên TourKit đã hết (SessionId trỏ phiên không tồn tại)', `SessionId = N'${'f'.repeat(32)}'`],
      ]) {
        const g = await newGrant(A);
        const n = sql(`UPDATE dbo.McpOAuthGrants SET ${set} OUTPUT inserted.Id WHERE ${grantWhere(g.access)}`).length;
        const r = await toolsList(g.access);
        check(id, n === 1 && is401(r), `${title} → 401 + resource_metadata`, { updated: n, status: r.status, www: r.www === EXPECT_WWW });
        if (id === 'E6') {
          const rf = await token({ grant_type: 'refresh_token', refresh_token: g.refresh, client_id: A });
          check('E6b', isInvalidGrant(rf), 'refresh khi phiên TourKit đã hết → invalid_grant', rf.body);
        }
      }
    } else for (const id of ['E3', 'E4', 'E5', 'E6']) skip(id, 'ca ép qua CSDL', 'cần --db-cmd; unit test McpOAuthBearerTests');

    // ── F. Focus 5 — CSRF trang đồng ý ─────────────────────────────────────
    {
      const q = authQuery(A, { challenge: pkce().challenge });
      const noSid = await decide('approve', q, {});
      check('F1', noSid.status === 401 && !noSid.body?.redirectUrl, 'approve không X-Session-Id → 401', { status: noSid.status });
      const cookie = await decide('approve', q, { Cookie: `tourkit_tk_session=${SID}; sessionId=${SID}` });
      check('F4', cookie.status === 401 && !cookie.body?.redirectUrl, 'approve chỉ có cookie phiên → 401', { status: cookie.status });
      const plain = await decide('approve', q, { 'X-Session-Id': SID }, { contentType: 'text/plain' });
      check('F5', plain.status === 400 && !plain.body?.redirectUrl, 'approve Content-Type text/plain (kiểu form chéo trang) → 400', { status: plain.status });
      const viaQs = await decide('approve', q, {}, { qs: '?sessionId=' + SID });
      info('approve với ?sessionId= trên URL, không header (minor đã hoãn ở Task 4)', { status: viaQs.status, issuedCode: !!viaQs.body?.redirectUrl });
      for (const [id, title, tq] of [
        ['F3a', 'redirect_uri đổi sang host lạ', authQuery(A, { challenge: pkce().challenge, redirect_uri: 'https://evil.example/cb' })],
        ['F3b', 'redirect_uri đổi sang URI hợp lệ nhưng chưa đăng ký', authQuery(A, { challenge: pkce().challenge, redirect_uri: 'https://chatgpt.com/connector_platform_oauth_redirect' })],
        ['F3c', 'client_id đổi sang client không tồn tại', authQuery('tkdcr_' + '0'.repeat(32), { challenge: pkce().challenge })],
        ['F3d', 'code_challenge_method=plain', authQuery(A, { challenge: pkce().challenge, code_challenge_method: 'plain' })],
        ['F3e', 'resource đổi sang máy chủ khác', authQuery(A, { challenge: pkce().challenge, resource: 'https://evil.example/mcp' })],
      ]) {
        const ap = await decide('approve', tq), dn = await decide('deny', tq);
        check(id, ap.status === 400 && !ap.body?.redirectUrl && dn.status === 400 && !dn.body?.redirectUrl, `approve/deny với ${title} → máy chủ kiểm lại, 400, không URL`, { approve: ap.status, deny: dn.status });
      }
      const dn = await decide('deny', q);
      const du = dn.body?.redirectUrl ? new URL(dn.body.redirectUrl) : null;
      check('F6', dn.status === 200 && du?.searchParams.get('error') === 'access_denied' && du.searchParams.get('iss') === BASE, 'deny hợp lệ → access_denied + iss', { status: dn.status, error: du?.searchParams.get('error') });
    }
    if (SID2) {
      // Không có "yêu cầu đang chờ" gắn người: ai trình X-Session-Id hợp lệ thì mã cấp cho CHÍNH người đó.
      const g = await newGrant(A, SID2);
      const mineList = await connections(SID), theirs = await connections(SID2);
      const inTheirs = theirs.filter(x => x.clientName === RUN + ' A' && !x.revokedUtc);
      // So theo Id: người 1 có grant cùng tên client từ các ca trước, nên không so theo tên.
      const inMine = inTheirs.some(t => mineList.some(m => m.id === t.id));
      const ok = await toolsList(g.access);
      check('F2', inTheirs.length === 1 && !inMine && ok.status === 200,
        'approve bằng phiên người khác → mã/grant gắn người đó, KHÔNG gắn người mở trang (không leo quyền)', { grantOfUser2: inTheirs.length, leakedToUser1: inMine });
      for (const x of inTheirs) await api('/api/v1/mcp/oauth/connections/' + x.id, { method: 'DELETE' }, SID2);
      const crossDel = await api('/api/v1/mcp/oauth/connections/' + (inTheirs[0]?.id ?? 0), { method: 'DELETE' }, SID);
      check('F2b', crossDel.status === 404, 'người 1 thu hồi kết nối của người 2 → 404', { status: crossDel.status });
    } else skip('F2', 'approve bằng phiên người khác', 'thiếu --sid2');

    // ── G. Header trang đồng ý ─────────────────────────────────────────────
    {
      const r = await fetch(BASE + '/oauth/authorize' + authQuery(A, { challenge: pkce().challenge }), { redirect: 'manual' });
      const h = { csp: r.headers.get('content-security-policy'), xfo: r.headers.get('x-frame-options'), ref: r.headers.get('referrer-policy'), cache: r.headers.get('cache-control') };
      check('G1', r.status === 200 && /frame-ancestors 'none'/.test(h.csp || '') && h.xfo === 'DENY' && h.ref === 'no-referrer', 'GET /oauth/authorize: CSP frame-ancestors none + XFO DENY + Referrer-Policy no-referrer', h);
    }

    // ── H. Khoá tay tkmcp_ ─────────────────────────────────────────────────
    {
      const mk = await (await api('/api/v1/mcp/tokens', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ label: RUN + ' key' }) })).json();
      const l = await toolsList(mk.token);
      const d = await api('/api/v1/mcp/tokens/' + mk.id, { method: 'DELETE' });
      const l2 = await toolsList(mk.token);
      check('H1', /^tkmcp_/.test(mk.token || '') && l.status === 200 && (l.body?.result?.tools || []).length === 20 && d.status === 204 && is401(l2),
        'khoá tay tkmcp_: tạo → tools/list 20 → thu hồi → 401', { list: l.status, tools: l.body?.result?.tools?.length, del: d.status, after: l2.status });
    }
  } finally {
    await browser.close();
    // ── Dọn dẹp ────────────────────────────────────────────────────────────
    if (DB_CMD) {
      info('CSDL trước dọn', sql(COUNTS)[0]);
      sql(`DELETE FROM dbo.McpOAuthCodes OUTPUT deleted.ClientId WHERE ClientId IN (SELECT ClientId FROM dbo.McpOAuthClients WHERE ClientName LIKE N'T6 e2e%')`);
      sql(`DELETE FROM dbo.McpOAuthGrants OUTPUT deleted.Id WHERE ${mineGrant}`);
      sql(`DELETE FROM dbo.McpTokens OUTPUT deleted.Id WHERE Label LIKE N'T6 e2e%'`);
      sql(`DELETE FROM dbo.McpOAuthClients OUTPUT deleted.ClientId WHERE ClientName LIKE N'T6 e2e%'`);
      info('CSDL sau dọn', sql(COUNTS)[0]);
    } else {
      for (const sid of [SID, SID2].filter(Boolean))
        for (const x of (await connections(sid).catch(() => [])).filter(x => x.clientName?.startsWith('T6 e2e') && !x.revokedUtc))
          await api('/api/v1/mcp/oauth/connections/' + x.id, { method: 'DELETE' }, sid);
      info('Dọn dẹp', 'đã thu hồi kết nối qua API; dòng client/mã/grant T6 e2e vẫn còn trong CSDL (chạy lại với --db-cmd để xoá)');
    }
  }
}

main().then(() => {
  const n = s => results.filter(r => r.status === s).length;
  console.log(`\nTổng: ${n('PASS')} PASS · ${n('FAIL')} FAIL · ${n('SKIP')} SKIP`);
  for (const r of results.filter(r => r.status === 'FAIL')) console.log(`  FAIL ${r.id} ${r.title}`);
  process.exit(n('FAIL') ? 1 : 0);
}).catch(e => { console.error('LỖI DỪNG SCRIPT:', mask(e?.stack || e)); process.exit(1); });
