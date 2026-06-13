// Snap mobile topbar after search hidden
import { test } from '@playwright/test';
const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';

test('mobile-topbar-no-search', async ({ page }) => {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/deals');
  await page.waitForTimeout(2500);
  await page.locator('.topbar').screenshot({ path: 'snap-mobile-topbar-clean.png' });
});
