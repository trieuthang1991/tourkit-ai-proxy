// Vbee TTS relay gateway — chạy trên Ubuntu (Node 18+), zero-dependency.
//
// Vì sao cần: server proxy chính (Windows Server 2012 R2) có Schannel quá cũ,
// KHÔNG bắt tay TLS được với api.vbee.vn (thiếu TLS 1.3 / x25519). Con Ubuntu này
// (OpenSSL mới) gọi Vbee bình thường → làm trung gian: Windows chỉ gửi {text},
// gateway lo toàn bộ submit → poll → tải mp3 rồi trả về.
//
// Bảo mật: Windows phải gửi header  X-Api-Key: <GATEWAY_API_KEY>  (khớp env).
// Vbee AppId/Token CHỈ nằm ở gateway này (không lộ ra ngoài).
//
// Chạy sau nginx (nginx lo TLS + cipher tương thích 2012 R2), gateway chỉ listen 127.0.0.1.

const http = require('http');

const PORT       = parseInt(process.env.PORT || '8090', 10);
const HOST       = process.env.HOST || '127.0.0.1';
const API_KEY    = process.env.GATEWAY_API_KEY || '';
const VBEE_BASE  = 'https://api.vbee.vn/v1';
const APP_ID     = process.env.VBEE_APP_ID || '';
const TOKEN      = process.env.VBEE_TOKEN || '';
const VOICE      = process.env.VBEE_VOICE || 'hn_female_ngochuyen_full_48k-fhg';
const SAMPLE     = parseInt(process.env.VBEE_SAMPLE_RATE || '24000', 10);
const BITRATE    = parseInt(process.env.VBEE_BITRATE || '128', 10);
const SPEED      = parseFloat(process.env.VBEE_SPEED || '1.0');
const WEBHOOK    = process.env.VBEE_WEBHOOK_URL || 'https://tourkit.vn/vbee-callback';
const POLL_SECS  = parseInt(process.env.VBEE_POLL_TIMEOUT || '45', 10);
const MAX_CHARS  = 2000;

if (!API_KEY) { console.error('THIẾU GATEWAY_API_KEY'); process.exit(1); }
if (!APP_ID || !TOKEN) { console.error('THIẾU VBEE_APP_ID / VBEE_TOKEN'); process.exit(1); }

const authHeaders = () => ({ 'Authorization': `Bearer ${TOKEN}`, 'App-Id': APP_ID });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function vbeeSubmit(text, voice) {
  const res = await fetch(`${VBEE_BASE}/tts`, {
    method: 'POST',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({
      text, voiceCode: voice || VOICE, mode: 'async',
      outputFormat: 'mp3', bitrate: BITRATE, sampleRate: SAMPLE,
      speed: SPEED, webhookUrl: WEBHOOK,
    }),
  });
  const raw = await res.text();
  if (!res.ok) throw new Error(`Vbee submit ${res.status}: ${raw.slice(0, 200)}`);
  const rid = JSON.parse(raw).requestId;
  if (!rid) throw new Error(`Vbee không trả requestId: ${raw.slice(0, 200)}`);
  return rid;
}

async function vbeePoll(requestId) {
  const deadline = Date.now() + POLL_SECS * 1000;
  let delay = 500;
  while (Date.now() < deadline) {
    const res = await fetch(`${VBEE_BASE}/tts/requests/${requestId}`, { headers: authHeaders() });
    const raw = await res.text();
    if (!res.ok) throw new Error(`Vbee poll ${res.status}: ${raw.slice(0, 200)}`);
    const j = JSON.parse(raw);
    if (j.status === 'COMPLETED') {
      if (!j.audioLink) throw new Error('Vbee COMPLETED nhưng thiếu audioLink');
      return j.audioLink;
    }
    if (j.status === 'FAILED') throw new Error(`Vbee FAILED: ${raw.slice(0, 200)}`);
    await sleep(delay);
    delay = Math.min(delay + 250, 1500);
  }
  throw new Error(`Vbee poll quá hạn (${requestId})`);
}

async function synthesize(text, voice) {
  text = String(text).trim().slice(0, MAX_CHARS);
  if (!text) throw new Error('Thiếu text');
  const rid  = await vbeeSubmit(text, voice);
  const link = await vbeePoll(rid);                         // fetch tự follow redirect (302 → CDN)
  const buf  = Buffer.from(await (await fetch(link)).arrayBuffer());
  if (buf.length < 200) throw new Error('Vbee trả audio rỗng');
  return buf;
}

function readBody(req, cap = 64 * 1024) {
  return new Promise((resolve, reject) => {
    let n = 0; const chunks = [];
    req.on('data', c => { n += c.length; if (n > cap) { reject(new Error('body quá lớn')); req.destroy(); } else chunks.push(c); });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  const url = (req.url || '').split('?')[0];

  if (req.method === 'GET' && url === '/healthz') {
    res.writeHead(200, { 'Content-Type': 'text/plain' }); return res.end('ok');
  }

  if (req.method === 'POST' && url === '/vbee/tts') {
    if ((req.headers['x-api-key'] || '') !== API_KEY) {
      res.writeHead(401, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ error: 'unauthorized' }));
    }
    try {
      const body = await readBody(req);
      const { text, voice } = JSON.parse(body || '{}');
      const mp3 = await synthesize(text, voice);
      res.writeHead(200, { 'Content-Type': 'audio/mpeg', 'Content-Length': mp3.length, 'X-Tts-Engine': 'vbee' });
      return res.end(mp3);
    } catch (e) {
      console.error('[vbee/tts]', e.message);
      res.writeHead(502, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify({ error: e.message }));
    }
  }

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'not found' }));
});

server.listen(PORT, HOST, () => console.log(`Vbee gateway listening http://${HOST}:${PORT}  (voice=${VOICE})`));
