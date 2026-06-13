// Snapshot UI hiện tại (home + assistant + customers) — để dev xem ngoài chat
import { test } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('snap home + assistant + customers', async ({ page }) => {
  test.setTimeout(60_000);
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, STAGING_SESSION);

  await page.setViewportSize({ width: 1440, height: 900 });

  await page.goto('/#/', { waitUntil: 'networkidle' });
  await page.waitForTimeout(1000);
  await page.screenshot({ path: 'snap-now-home.png', fullPage: false });

  await page.goto('/#/assistant', { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: 'snap-now-assistant.png', fullPage: false });

  await page.goto('/#/customers', { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: 'snap-now-customers.png', fullPage: false });
});
