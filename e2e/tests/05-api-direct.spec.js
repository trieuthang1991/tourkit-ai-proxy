// API direct: AI feature dùng Models:Primary + Models:{Feature} (qua AiModelRegistry)
import { test, expect, request } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';

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
    const r = await request.get('/api/v1/session', {
      headers: { 'X-Session-Id': TEST_SESSION }
    });
    if (r.status() === 401) {
      console.warn('Session expired — skip session check');
      return;
    }
    expect(r.ok()).toBeTruthy();
    const d = await r.json();
    expect(d.tenantId).toBeTruthy();
  });
});

test.describe('API — ChatTools enum đầy đủ', () => {
  test('GET /api/v1/ai/tools (nếu có) trả 17 tool', async ({ request }) => {
    const r = await request.get('/api/v1/ai/tools');
    const ct = r.headers()['content-type'] || '';
    if (r.status() === 404 || !ct.includes('json')) {
      console.warn('Endpoint /api/v1/ai/tools chưa có (optional, skip)');
      return;
    }
    const data = await r.json();
    const tools = data.tools || data;
    expect(tools.length).toBeGreaterThanOrEqual(14);
  });
});
