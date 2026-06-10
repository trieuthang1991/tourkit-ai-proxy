// Snap home greeting (with quota chip) + mascot section
import { test } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';

test('home-greet-quota', async ({ page }) => {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto('/');
  await page.waitForTimeout(3000);   // wait for quota fetch
  await page.locator('.hp-greet').screenshot({ path: 'snap-home-greet.png' });
});

test('home-mascot', async ({ page }) => {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto('/');
  await page.waitForTimeout(2500);
  await page.locator('.hp-mascot-label').screenshot({ path: 'snap-home-mascot.png' });
});
