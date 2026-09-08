// API direct: AI feature dùng Models:Primary + Models:{Feature} (qua AiModelRegistry)
import { test, expect, request } from '@playwright/test';
import { PHIEN, THIEU_PHIEN } from '../helpers/phien.js';

test.describe('API — providers + 3 tool mới', () => {
  test('GET /api/v1/providers trả 5 provider có deepseek', async ({ request }) => {
    const r = await request.get('/api/v1/providers');
    expect(r.ok()).toBeTruthy();
    const list = await r.json();
    expect(list.length).toBeGreaterThanOrEqual(5);
    const ids = list.map(p => p.id);
    expect(ids).toContain('deepseek');
    expect(ids).toContain('anthropic');
  });

  test('GET /api/v1/session trả tenantId', async ({ request }) => {
    // KHÔNG có phiên = chưa cấu hình để chạy bài này → bỏ qua CÓ TÊN.
    test.skip(!PHIEN, THIEU_PHIEN);

    const r = await request.get('/api/v1/session', { headers: { 'X-Session-Id': PHIEN } });

    // CÓ phiên mà bị từ chối là chuyện KHÁC HẲN, và phải ĐỎ. Bản trước console.warn rồi return —
    // Playwright ghi PASSED, nên phiên chết vài tháng vẫn không ai hay. Người chạy đã tự tay đưa
    // phiên vào thì họ cần biết nó không dùng được, chứ không cần một dấu tích xanh.
    expect(r.status(), 'E2E_SESSION bị từ chối — lấy sessionId mới rồi chạy lại').not.toBe(401);
    expect(r.ok()).toBeTruthy();
    const d = await r.json();
    expect(d.tenantId).toBeTruthy();
  });
});

test.describe('API — ChatTools enum đầy đủ', () => {
  test('GET /api/v1/ai/tools (nếu có) trả 17 tool', async ({ request }) => {
    const r = await request.get('/api/v1/ai/tools');
    const ct = r.headers()['content-type'] || '';
    // Đường này là TUỲ CHỌN — chưa có thì bỏ qua CÓ TÊN. console.warn rồi return sẽ ghi PASSED,
    // nghĩa là bài này "xanh" mãi mãi kể cả khi đường đó ra đời rồi hỏng.
    test.skip(r.status() === 404 || !ct.includes('json'),
      'Chưa có /api/v1/ai/tools trên máy chủ này (đường tuỳ chọn).');
    const data = await r.json();
    const tools = data.tools || data;
    expect(tools.length).toBeGreaterThanOrEqual(14);
  });
});
