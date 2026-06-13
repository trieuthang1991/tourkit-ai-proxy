import { test, expect } from '@playwright/test';

test('measure hero text wrap', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/landing', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);

  const info = await page.evaluate(() => {
    const h1 = document.querySelector('.lp-hero-text h1');
    const accent = document.querySelector('.lp-hl-accent');
    const text = document.querySelector('.lp-hero-text');
    const r = h1.getBoundingClientRect();
    const ar = accent.getBoundingClientRect();
    const tr = text.getBoundingClientRect();
    return {
      h1Width: r.width.toFixed(0),
      h1Height: r.height.toFixed(0),
      accentWidth: ar.width.toFixed(0),
      accentHeight: ar.height.toFixed(0),
      textColWidth: tr.width.toFixed(0),
      h1FontSize: getComputedStyle(h1).fontSize,
      lineHeight: getComputedStyle(h1).lineHeight,
    };
  });
  console.log(JSON.stringify(info, null, 2));
});
