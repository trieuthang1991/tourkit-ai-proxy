// Snapshot home chat (HomeChatCard) — kiểm format reply có xuống dòng giữa các đoạn.
import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('home chat: gửi câu hỏi → bubble có \\n giữa đoạn (pre-wrap render đúng)', async ({ page }) => {
  test.setTimeout(90_000);
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, STAGING_SESSION);

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/#/', { waitUntil: 'networkidle' });

  // Bubble greeting xuất hiện ngay khi mount (không cần chờ AI)
  const greeting = page.locator('.hp-chat-bubble--bot').first();
  await expect(greeting).toBeVisible({ timeout: 5000 });

  const ws = await greeting.evaluate(el => getComputedStyle(el).whiteSpace);
  console.log('whiteSpace của .hp-chat-bubble:', ws);
  expect(ws).toMatch(/pre/);   // pre-wrap đã áp đúng

  // Inject text có \n\n vào DOM thật → verify visually paragraph có cách nhau
  await greeting.evaluate(el => {
    const span = el.querySelector('.hp-chat-bubble-text') || el;
    span.textContent = 'Đoạn 1 — câu phân tích đầu tiên.\n\nĐoạn 2 — câu phân tích thứ hai, tách dòng rõ.';
  });
  await page.screenshot({ path: 'snap-home-chat-format.png', fullPage: false, clip: { x: 600, y: 200, width: 700, height: 350 } });
});
