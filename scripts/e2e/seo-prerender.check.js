// seo-prerender.check.js — canh hai chỗ dễ lệch của lớp SEO. Chạy: node scripts/e2e/seo-prerender.check.js
//
// VÌ SAO CẦN:
//   1. Nội dung dựng sẵn ở Configuration/SeoSetup.cs phải TRÙNG chữ với wwwroot/pages/landing.jsx.
//      Trùng thì đó chỉ là "gửi sớm hơn"; lệch thì Google coi là gian lận nội dung (cloaking) và
//      phạt. Sửa câu chữ trên landing mà quên bên C# là lệch ngay, và KHÔNG có gì báo.
//   2. SeoSetup.Routes cũng là danh sách đường dẫn hợp lệ để trả 404. Thêm trang mới trong app.jsx
//      mà quên khai ở đó thì mở link trực tiếp vào trang đó ra 404 — hỏng kiểu khó lần vì trong app
//      bấm qua vẫn chạy (router client không hỏi server).
'use strict';

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', '..');
const seoCs = fs.readFileSync(path.join(root, 'Configuration', 'SeoSetup.cs'), 'utf8');
const landing = fs.readFileSync(path.join(root, 'wwwroot', 'pages', 'landing.jsx'), 'utf8');
const appJsx = fs.readFileSync(path.join(root, 'wwwroot', 'app.jsx'), 'utf8');

let ok = 0, errs = [], warns = [];
const pass = (m) => { ok++; console.log('  OK   ' + m); };
const fail = (m) => { errs.push(m); console.log('  LOI  ' + m); };
const warn = (m) => { warns.push(m); console.log('  WARN ' + m); };

// ── 1. Câu chữ dựng sẵn có nằm trong landing.jsx không ──────────────────────
//
// Lấy các chuỗi tiếng Việt trong LandingBody() + 3 mảng hằng. Chỉ soát chuỗi có ký tự có dấu hoặc
// dài > 12 — bỏ qua tên thẻ HTML, class, và mấy chuỗi kỹ thuật ngắn.
const bodyStart = seoCs.indexOf('public static string LandingBody()');
const bodyEnd = seoCs.indexOf('// ── robots.txt', bodyStart);
const arraysStart = seoCs.indexOf('private static readonly string[] FeatureTitles');
const region = seoCs.slice(arraysStart, bodyEnd);

const literals = [...region.matchAll(/"([^"\\]{4,})"/g)].map(m => m[1]);
const contentStrings = literals.filter(s =>
  /[àáâãèéêìíòóôõùúýăđĩũơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ]/i.test(s));

if (contentStrings.length === 0) fail('Khong doc duoc chuoi noi dung nao tu SeoSetup.cs — regex hong?');

const missing = contentStrings.filter(s => !landing.includes(s));
if (missing.length === 0) {
  pass(`${contentStrings.length} cau chu dung san deu co nguyen van trong landing.jsx`);
} else {
  fail(`${missing.length} cau KHONG con trong landing.jsx (lech noi dung -> rui ro cloaking):`);
  missing.forEach(s => console.log('         · ' + s));
}

// ── 2. Route trong app.jsx vs SeoSetup.Routes ───────────────────────────────
const declared = [...appJsx.matchAll(/Route path="([^"]+)"/g)]
  .map(m => m[1])
  .filter(p => p !== '*' && !p.includes('<'))       // path="*" va vi du trong comment
  .map(p => p.split('/:')[0])                        // /help/:slug -> /help
  .filter((v, i, a) => a.indexOf(v) === i);

const seoPaths = [...seoCs.matchAll(/new\("(\/[^"]*)"/g)].map(m => m[1]);

// Đường con (vd /visa/history) coi là đã phủ nếu đoạn đầu có khai — SeoSetup.IsKnownRoute khớp
// theo đoạn đầu, nên không cần khai riêng từng đường con.
const covered = (p) => seoPaths.includes(p) || seoPaths.includes('/' + p.split('/').filter(Boolean)[0]);

const notCovered = declared.filter(p => !covered(p));
if (notCovered.length === 0) {
  pass(`${declared.length} route trong app.jsx deu duoc SeoSetup.Routes phu (khong bi 404 oan)`);
} else {
  fail('Route co trong app.jsx nhung SeoSetup.Routes KHONG phu -> mo link truc tiep se 404:');
  notCovered.forEach(p => console.log('         · ' + p));
}

// Chiều ngược: khai ở C# mà app.jsx không có route → không hỏng gì, chỉ là rác. Cảnh báo thôi,
// vì có đường xử lý ngoài <Route> (vd "/" và "/landing" bắt sớm, "/home" chuyển hướng).
const handledOutsideRoute = ['/', '/landing', '/home'];
const orphan = seoPaths.filter(p => !handledOutsideRoute.includes(p) && !declared.includes(p));
if (orphan.length > 0) {
  warn('Khai o SeoSetup.Routes nhung khong thay <Route> tuong ung (co the la duong cu):');
  orphan.forEach(p => console.log('         · ' + p));
}

// ── 3. Trang nội bộ phải noindex ────────────────────────────────────────────
const indexTrue = [...seoCs.matchAll(/new\("(\/[^"]*)"[^)]*Index:\s*true/g)].map(m => m[1]);
const expectPublic = ['/', '/landing'];
const unexpected = indexTrue.filter(p => !expectPublic.includes(p));
if (unexpected.length === 0) {
  pass('Chi trang chu duoc index; moi trang noi bo deu noindex');
} else {
  fail('Trang noi bo bi mo cho index (se lot vao Google):');
  unexpected.forEach(p => console.log('         · ' + p));
}

console.log('');
console.log(`${ok} ok · ${warns.length} canh bao · ${errs.length} loi`);
process.exit(errs.length > 0 ? 1 : 0);
