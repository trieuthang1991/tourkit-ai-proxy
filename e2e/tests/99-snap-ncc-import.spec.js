import { test, expect } from '@playwright/test';

const STAGING_SESSION = '58050a0fe0ab41d884025719103bb891';

test('NCC Import page — drop zone visible (chưa upload)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/ncc-import', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);

  await expect(page.locator('.nccim-drop')).toBeVisible();
  await expect(page.locator('text=Kéo thả file vào đây')).toBeVisible();
  await page.screenshot({ path: 'snap-ncc-import-empty.png', fullPage: false });
});

test('NCC Import — upload template Excel → 10 rows preview', async ({ page }) => {
  test.setTimeout(30_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
  }, STAGING_SESSION);

  await page.goto('/ncc-import', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);

  // Upload template Excel (10 NCC mẫu)
  const input = page.locator('input[type="file"]');
  await input.setInputFiles('../wwwroot/files/file_import_ncc.xlsx');
  await page.waitForTimeout(2000);

  await expect(page.locator('.nccim-table')).toBeVisible();
  const rowCount = await page.locator('.nccim-table tbody tr').count();
  expect(rowCount).toBe(10);
  console.log('PREVIEW ROWS:', rowCount);

  // Loại NCC select dropdown đã snap đúng — VD row 1 = "Mũ"
  const firstType = await page.locator('.nccim-table tbody tr').first().locator('select').first().inputValue();
  expect(firstType).toBe('Mũ');

  await page.screenshot({ path: 'snap-ncc-import-preview.png', fullPage: false });
});

test('Landing có 9 features (đã thêm NCC Import)', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(800);
  const c = await page.locator('.lp-feature-card').count();
  expect(c).toBe(9);
});
