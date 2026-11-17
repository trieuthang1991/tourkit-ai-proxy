// Đọc JSON từ Response khi HẠ TẦNG chen ngang bằng TRANG HTML.
//
// Ca lỗi thật 06/11/2026: upload file báo giá NCC lên travelai.vn → người dùng chỉ thấy
// «Unexpected token '<', "<!DOCTYPE "... is not valid JSON». Câu đó nuốt mất status thật,
// nên không phân biệt được Cloudflare cắt vì quá giờ (524) với app chết giữa chừng (502)
// hay tường lửa chặn (403) — ba nguyên nhân cần ba cách xử lý khác hẳn nhau.
//
// ⚠️ Đây KHÔNG phải bài Playwright: nằm ngoài `testDir: ./tests` và chạy bằng test runner có
// sẵn của Node, nên không cần `npm install` ở e2e/ (máy dev thường chưa cài node_modules ở đó).
//
//   node --test e2e/unit/doc-json.test.js
//   (trỏ thẳng file, đừng trỏ thư mục — `--test e2e/unit/` bị Node hiểu là tên module)
//
// Nạp wwwroot/lib/util.js với một `window` giả — không trình duyệt, không chạm máy chủ.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const nguon = fs.readFileSync(new URL('../../wwwroot/lib/util.js', import.meta.url), 'utf8');
const cuaSo = {};
new Function('window', nguon)(cuaSo);
const { jsonHoacLoi } = cuaSo.tourkitUtil;

// Response giả — chỉ đúng những gì jsonHoacLoi đụng tới: status, ok, text(), headers.get().
const traLoi = (status, body, headers = {}) => ({
  status,
  ok: status >= 200 && status < 300,
  text: async () => body,
  headers: { get: (k) => headers[String(k).toLowerCase()] ?? null },
});

// Câu lỗi mà jsonHoacLoi ném ra (null nếu nó KHÔNG ném — tức là bài test sai kỳ vọng).
const cauLoi = (r) => jsonHoacLoi(r).then(() => null, (e) => e.message);

const TRANG_524 = '<!DOCTYPE html><html><head><title>travelai.vn | 524: A timeout occurred</title>'
  + '</head><body><h1>Error 524</h1></body></html>';

test('JSON hợp lệ → trả object', async () => {
  const j = await jsonHoacLoi(traLoi(200, '{"quote":{"a":1},"latencyMs":16169}'));
  assert.equal(j.latencyMs, 16169);
});

test('Cloudflare 524 → nêu status, nguyên nhân, tiêu đề trang và mã tra cứu', async () => {
  const loi = await cauLoi(traLoi(524, TRANG_524, { 'cf-ray': 'a562dbaa0ecfc515-NRT' }));
  assert.match(loi, /524/);
  assert.match(loi, /quá 100 giây/);
  assert.match(loi, /524: A timeout occurred/);
  assert.match(loi, /a562dbaa0ecfc515-NRT/);
  // Và tuyệt đối không còn câu của JSON.parse.
  assert.doesNotMatch(loi, /Unexpected token/);
});

test('502 app chết giữa chừng → phân biệt được với quá giờ', async () => {
  const loi = await cauLoi(traLoi(502, '<!DOCTYPE html><html><body>502</body></html>'));
  assert.match(loi, /502/);
  assert.match(loi, /không trả lời/);
  assert.doesNotMatch(loi, /100 giây/);
});

test('413 file quá nặng → nói rõ bị chặn TRƯỚC khi tới ứng dụng', async () => {
  const loi = await cauLoi(traLoi(413, '<!DOCTYPE html><title>413</title>'));
  assert.match(loi, /quá nặng/);
  assert.match(loi, /trước khi tới ứng dụng/);
});

test('HTML kèm 200 (rơi vào trang index) → vẫn báo không phải JSON', async () => {
  const loi = await cauLoi(traLoi(200, '<!DOCTYPE html><title>Trav-ai</title>'));
  assert.match(loi, /không phải JSON/);
  assert.match(loi, /200/);
});

test('lỗi CỦA APP (JSON có error) → giữ nguyên câu của app, không bịa thêm', async () => {
  const loi = await cauLoi(traLoi(502, '{"error":"Tách bớt trang rồi bóc lại","detail":"..."}'));
  assert.equal(loi, 'Tách bớt trang rồi bóc lại');
});

test('body rỗng: 200 → {} ; 500 → "HTTP 500"', async () => {
  assert.deepEqual(await jsonHoacLoi(traLoi(200, '')), {});
  assert.equal(await cauLoi(traLoi(500, '')), 'HTTP 500');
});

test('status lạ không có trong bảng → vẫn nêu status, không lòi undefined', async () => {
  const loi = await cauLoi(traLoi(418, '<html>teapot</html>'));
  assert.match(loi, /418/);
  assert.doesNotMatch(loi, /undefined/);
});

test('Response không có headers → không nổ', async () => {
  const loi = await cauLoi({ status: 504, ok: false, text: async () => '<!DOCTYPE html>' });
  assert.match(loi, /504/);
});

test('tiêu đề trang dài bất thường không nuốt hết câu lỗi', async () => {
  const dai = '<!DOCTYPE html><title>' + 'x'.repeat(5000) + '</title>';
  const loi = await cauLoi(traLoi(520, dai));
  assert.match(loi, /520/);
  assert.ok(loi.length < 400, 'câu lỗi phải bị chặn độ dài, đang là ' + loi.length);
});
