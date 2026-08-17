// scripts/e2e/features-flow-diagram.check.js
// E2E TINH NANG - So do luong workflow (/flow-preview).
//
// Chay: node scripts/e2e/features-flow-diagram.check.js
// Khong can dang nhap, khong can app dang chay - doc thang file nguon.
//
// Bat cac loi that su xay ra khi so do nhieu len:
//   1. Them file so do moi ma QUEN khai vao index.html hoac bundle-entry.js
//      -> dev mode chay duoc / prod thieu so do (hoac nguoc lai). Day la cai bay
//         co san cua du an (xem CLAUDE.md muc "Adding a new page").
//   2. Canh tro toi node khong ton tai (go id sai khi them buoc)
//   3. Node mo coi - khong co canh nao noi toi
//   4. Khoa cau hinh tren node KHONG co trong schema
//      -> bang cau hinh im lang thieu o nhap. Loi nguy hiem nhat.
//   5. Icon dung tren node khong co trong bo icon
//   6. So do ve cho workflow KHONG con ton tai trong backend
//   7. Workflow backend co ma CHUA ve so do (canh bao, khong fail)

'use strict';
const fs = require('fs');
const path = require('path');

const ROOT     = path.resolve(__dirname, '..', '..');
const D_FLOWS  = path.join(ROOT, 'wwwroot', 'flows');
const P_OPTS   = path.join(ROOT, 'wwwroot', 'components', 'workflow-options.jsx');
const P_ICONS  = path.join(ROOT, 'wwwroot', 'lib', 'icons.jsx');
const P_INDEX  = path.join(ROOT, 'wwwroot', 'index.html');
const P_ENTRY  = path.join(ROOT, 'wwwroot', 'bundle-entry.js');
const D_SERVICES = path.join(ROOT, 'Services');
const WWWROOT  = path.join(ROOT, 'wwwroot');

const fail = [], warn = [], ok = [];
const read = p => fs.readFileSync(p, 'utf8');

// ── Nap so do bang cach CHAY that cac file flows/ voi window gia ───────────────
// Chac chan hon cat chuoi bang regex: file nao sai cu phap la lo ra ngay.
function loadFlows() {
  const collected = {};
  const win = {};
  const sandboxGlobals = { window: win, console: { warn() {}, log() {} } };

  const files = fs.readdirSync(D_FLOWS).filter(f => f.endsWith('.js')).sort(
    (a, b) => (a === '_registry.js' ? -1 : b === '_registry.js' ? 1 : a.localeCompare(b)));

  for (const f of files) {
    const src = read(path.join(D_FLOWS, f));
    try {
      // eslint-disable-next-line no-new-func
      new Function('window', 'console', src)(win, sandboxGlobals.console);
    } catch (e) {
      fail.push(`[${f}] Khong chay duoc: ${e.message}`);
    }
  }
  if (!win.tourkitFlows) { fail.push('flows/_registry.js khong tao duoc window.tourkitFlows'); return { collected, files }; }
  win.tourkitFlows.all().forEach(d => { collected[d.type] = d; });
  return { collected: collected, files };
}

function extractSchema(src) {
  const grabObj = (name) => {
    const s = src.indexOf(`const ${name} = `);
    if (s < 0) throw new Error(`Khong tim thay ${name}`);
    const e = src.indexOf('\n  };', s);
    return src.slice(s, e + 4);
  };
  const grabArr = (name) => {
    const s = src.indexOf(`const ${name} = [`);
    const e = src.indexOf('\n  ];', s);
    return src.slice(s, e + 4);
  };
  // eslint-disable-next-line no-new-func
  // Mọi mảng hằng mà WORKFLOW_OPTIONS tham chiếu đều phải nạp TRƯỚC, không thì new Function ném
  // "X is not defined" và cả bộ kiểm chết ở bước đọc nguồn (đã dính khi thêm BRIEF_SECTIONS).
  return new Function(
    `${grabArr('MAIL_CATEGORIES')}\n${grabArr('MAIL_TONES')}\n${grabArr('BRIEF_SECTIONS')}\n`
    + `${grabArr('TOUR_TYPES')}\n`
    + `${grabObj('WORKFLOW_OPTIONS')}\nreturn WORKFLOW_OPTIONS;`
  )();
}

function extractIconNames(src) {
  const s = src.indexOf('const paths = {');
  const body = src.slice(s, src.indexOf('\n  };', s));
  const names = new Set();
  for (const m of body.matchAll(/^\s{4}([a-zA-Z0-9_]+):/gm)) names.add(m[1]);
  return names;
}

// ── Chay ──────────────────────────────────────────────────────────────────────
let FLOWS, FLOW_FILES, SCHEMA, ICONS;
try {
  const r = loadFlows();
  FLOWS = r.collected; FLOW_FILES = r.files;
  SCHEMA = extractSchema(read(P_OPTS));
  ICONS  = extractIconNames(read(P_ICONS));
  ok.push(`Nap ${Object.keys(FLOWS).length} so do tu ${FLOW_FILES.length} file, ${Object.keys(SCHEMA).length} schema, ${ICONS.size} icon`);
} catch (e) {
  console.error('KHONG PHAN TICH DUOC NGUON: ' + e.message);
  process.exit(2);
}

// ── 1. Moi file flows/ phai duoc khai o CA index.html VA bundle-entry.js ───────
const indexSrc = read(P_INDEX);
const entrySrc = read(P_ENTRY);
let regMiss = 0;
for (const f of FLOW_FILES) {
  const inIndex = indexSrc.includes(`flows/${f}`);
  const inEntry = entrySrc.includes(`./flows/${f}`);
  if (!inIndex) { fail.push(`[${f}] CHUA khai trong index.html -> dev mode se thieu so do nay`); regMiss++; }
  if (!inEntry) { fail.push(`[${f}] CHUA khai trong bundle-entry.js -> ban prod se thieu so do nay`); regMiss++; }
}
// Nguoc lai: khai roi ma file da bi xoa
for (const m of indexSrc.matchAll(/flows\/([\w.-]+\.js)/g)) {
  if (!FLOW_FILES.includes(m[1])) fail.push(`index.html tro toi flows/${m[1]} nhung file khong ton tai`);
}
for (const m of entrySrc.matchAll(/\.\/flows\/([\w.-]+\.js)/g)) {
  if (!FLOW_FILES.includes(m[1])) fail.push(`bundle-entry.js tro toi flows/${m[1]} nhung file khong ton tai`);
}
if (regMiss === 0) ok.push(`Ca ${FLOW_FILES.length} file flows/ deu da khai o index.html VA bundle-entry.js`);

// ── 1b. Moi file pages/ cung phai khai o CA HAI cho ───────────────────────────
// Cung mot bay nhu flows/ nhung hau qua nang hon: thieu import o bundle-entry.js thi ban prod
// KHONG co trang do -> nguoi dung bam vao menu la TRANG TRANG (React #130), ma dev mode van chay
// binh thuong nen rat de len that. Kiem ca 2 chieu de bat luon file da xoa ma con khai.
const PAGES_DIR = path.join(ROOT, 'wwwroot', 'pages');
const PAGE_FILES = fs.readdirSync(PAGES_DIR).filter(f => f.endsWith('.jsx')).sort();
// Trang cua HTML entry KHAC (admin-trav-ai.html co shell rieng, KHONG dung index.html) — cố ý
// không nằm trong index.html/bundle-entry.js. Đọc từ chính file HTML đó chứ không hard-code tên,
// để thêm trang admin mới không phải sửa bộ kiểm này.
const OTHER_ENTRIES = ['admin-trav-ai.html', 'widget-demo.html', 'stt-compare.html']
  .map(f => path.join(WWWROOT, f)).filter(fs.existsSync).map(read);
const pagesInOtherEntries = new Set(
  OTHER_ENTRIES.flatMap(src => Array.from(src.matchAll(/src="\/?pages\/([\w.-]+\.jsx)"/g)).map(m => m[1])));
const inIndexPages = new Set(Array.from(indexSrc.matchAll(/src="pages\/([\w.-]+\.jsx)"/g)).map(m => m[1]));
const inEntryPages = new Set(Array.from(entrySrc.matchAll(/\.\/pages\/([\w.-]+\.jsx)/g)).map(m => m[1]));
let pageMiss = 0;
for (const f of PAGE_FILES) {
  if (pagesInOtherEntries.has(f)) continue;   // thuoc HTML entry khac
  if (!inIndexPages.has(f)) { fail.push(`[pages/${f}] CHUA khai trong index.html -> dev mode thieu trang nay`); pageMiss++; }
  if (!inEntryPages.has(f)) { fail.push(`[pages/${f}] CHUA khai trong bundle-entry.js -> BAN PROD TRANG TRANG khi mo trang nay`); pageMiss++; }
}
for (const f of inIndexPages) if (!PAGE_FILES.includes(f)) { fail.push(`index.html tro toi pages/${f} nhung file khong ton tai`); pageMiss++; }
for (const f of inEntryPages) if (!PAGE_FILES.includes(f)) { fail.push(`bundle-entry.js tro toi pages/${f} nhung file khong ton tai`); pageMiss++; }
if (pageMiss === 0) ok.push(`Ca ${PAGE_FILES.length - pagesInOtherEntries.size} file pages/ cua app deu da khai o index.html VA bundle-entry.js` + (pagesInOtherEntries.size ? ` (bo qua ${pagesInOtherEntries.size} trang thuoc HTML entry rieng)` : ''));

// ── Workflow that su ton tai trong backend (quet CA cay Services/) ────────────
const backendTypes = new Set();
(function scan(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { scan(p); continue; }
    if (!e.name.endsWith('.cs')) continue;
    const src = read(p);
    if (!/:\s*IScheduledWorkflow\b/.test(src)) continue;
    const m = src.match(/public\s+string\s+Type\s*=>\s*"([^"]+)"/);
    if (m) backendTypes.add(m[1]);
  }
})(D_SERVICES);
ok.push(`Backend dang co ${backendTypes.size} workflow: ${[...backendTypes].sort().join(', ')}`);

// ── Kiem tung so do ───────────────────────────────────────────────────────────
for (const [type, flow] of Object.entries(FLOWS)) {
  const tag = `[${type}]`;
  const isDemo = !!flow.demo;

  // 6. So do ve cho workflow con ton tai? (bo qua so do minh hoa)
  if (!isDemo && !backendTypes.has(type)) {
    fail.push(`${tag} So do ve cho workflow KHONG co trong backend - da bi xoa/doi ten?`);
    continue;
  }
  if (isDemo && backendTypes.has(type)) {
    fail.push(`${tag} So do danh dau demo:true nhung backend LAI CO workflow nay - bo co demo di`);
  }

  const ids = new Set(flow.nodes.map(n => n.id));
  if (ids.size !== flow.nodes.length) fail.push(`${tag} Co node trung id`);

  for (const e of flow.edges) {
    if (!ids.has(e.source)) fail.push(`${tag} Canh ${e.id}: source '${e.source}' khong ton tai`);
    if (!ids.has(e.target)) fail.push(`${tag} Canh ${e.id}: target '${e.target}' khong ton tai`);
  }

  const targeted = new Set(flow.edges.map(e => e.target));
  const sourced  = new Set(flow.edges.map(e => e.source));
  flow.nodes.forEach((n, i) => {
    if (i === 0) {
      if (n.type !== 'fpTrigger') fail.push(`${tag} Node dau tien '${n.id}' phai la trigger`);
      return;
    }
    if (!targeted.has(n.id)) fail.push(`${tag} Node '${n.id}' (${n.data.title}) khong co canh nao noi toi`);
  });
  flow.nodes.forEach(n => {
    if (!sourced.has(n.id) && !targeted.has(n.id)) fail.push(`${tag} Node '${n.id}' hoan toan tach roi`);
  });

  const schemaKeys = new Set((SCHEMA[type] || []).map(o => o.key));

  for (const n of flow.nodes) {
    if (!ICONS.has(n.data.icon)) fail.push(`${tag} Node '${n.id}' dung icon '${n.data.icon}' khong co trong icons.jsx`);
    for (const k of (n.data.cfg || [])) {
      if (k === '@interval') continue;
      if (!schemaKeys.has(k)) {
        fail.push(`${tag} Node '${n.id}' tro toi option '${k}' KHONG co trong schema - bang cau hinh se thieu o nhap`);
      }
    }
  }

  if (isDemo) { ok.push(`${tag} So do minh hoa (khong gan cau hinh)`); continue; }

  const covered = new Set();
  flow.nodes.forEach(n => (n.data.cfg || []).forEach(k => covered.add(k)));
  const uncovered = [...schemaKeys].filter(k => !covered.has(k));
  if (uncovered.length) warn.push(`${tag} ${uncovered.length} option khong sua duoc tren so do: ${uncovered.join(', ')}`);
  else ok.push(`${tag} Moi option deu gan vao mot node`);

  const nInterval = flow.nodes.filter(n => (n.data.cfg || []).includes('@interval')).length;
  if (nInterval !== 1) fail.push(`${tag} Phai co DUNG 1 node mang '@interval' (dang co ${nInterval})`);

  if (!flow.label) fail.push(`${tag} Thieu label`);
  if (!flow.note)  warn.push(`${tag} Thieu note (dong giai thich duoi bang thong tin)`);
}

for (const t of backendTypes) {
  if (!FLOWS[t]) warn.push(`[${t}] Backend co workflow nay nhung CHUA ve so do`);
}

console.log('=== E2E: So do luong workflow ===\n');
ok.forEach(m => console.log('  OK   ' + m));
warn.forEach(m => console.log('  WARN ' + m));
fail.forEach(m => console.log('  FAIL ' + m));
console.log(`\n${ok.length} ok · ${warn.length} canh bao · ${fail.length} loi`);
process.exit(fail.length ? 1 : 0);
