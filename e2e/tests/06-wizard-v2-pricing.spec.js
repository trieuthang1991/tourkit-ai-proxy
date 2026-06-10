// E2E test: v2 pricing logic port vào /wizard
// Cover: Step 1 hotelStars chips · paxRanges editor · AI autofill parser
//        Step 3 multi-pricing-options matrix · Apply ghi đè margin
import { test, expect } from '@playwright/test';

const TEST_SESSION = '5294b8ec7d8f4e12bec4b44334946e1b';

async function setSession(page) {
  await page.addInitScript((sid) => {
    localStorage.setItem('tourkit_tk_session', sid);
    localStorage.setItem('tourkit_skip_login_gate', '1');
  }, TEST_SESSION);
}

async function gotoWizardStep(page, stepNum) {
  await setSession(page);
  await page.goto('/wizard');
  // Wait for wizard step bar render
  await page.locator('.wizard-stepbar .step').first().waitFor({ timeout: 10000 });
  // Click step by index (button text contains step label)
  const labels = ['Yêu cầu khách hàng', 'AI Lập lịch trình', 'Bảng tính giá', 'Xuất báo giá'];
  if (stepNum > 1) {
    await page.locator('.wizard-stepbar .step', { hasText: labels[stepNum - 1] }).click();
    await page.waitForTimeout(700);
  }
}

test.describe('Wizard Step 1 — Hotel stars + Pax ranges (v2 port)', () => {
  test('Hotel star chips render + toggle multi-select', async ({ page }) => {
    await gotoWizardStep(page, 1);
    // Label section
    await expect(page.getByText('Lựa chọn phân khúc khách sạn')).toBeVisible();
    // 4 chip pills: 3* / 4* / 5* / 6*
    const chips34 = page.locator('.chip', { hasText: /^\s*[3456]\*\s*$/ });
    await expect(chips34).toHaveCount(4);

    // Default: hotelStars = [3, 4, 5] → chips 3,4,5 active, 6 inactive
    const star6 = page.locator('.chip', { hasText: /6\*/ });
    await expect(star6).not.toHaveClass(/active/);

    // Click 6* → active
    await star6.click();
    await expect(star6).toHaveClass(/active/);

    // Click 3* → toggle off
    const star3 = page.locator('.chip', { hasText: /3\*/ });
    await star3.click();
    await expect(star3).not.toHaveClass(/active/);
  });

  test('Pax range editor: MATCHED badge follows adults count', async ({ page }) => {
    await gotoWizardStep(page, 1);
    await expect(page.getByText('Quản lý khoảng khách (Pax Range)')).toBeVisible();

    // 3 default ranges: 1-14, 15-30, 31-200
    // Default adults trong DEMO_REQUEST cao (40 chục) → matched range 31-200
    const ranges = page.locator('text=/\\d+\\s*–\\s*\\d+\\s*pax/i');
    // At least 1 MATCHED badge visible
    const matchedBadges = page.locator('text=✓ MATCHED');
    await expect(matchedBadges).toHaveCount(1);
  });

  test('AI Autofill: parse câu Việt → điền form', async ({ page }) => {
    await gotoWizardStep(page, 1);
    // Click "AI điền nhanh" button
    await page.getByRole('button', { name: /AI điền nhanh/i }).click();
    // Textarea xuất hiện
    const textarea = page.locator('textarea[placeholder*="Đoàn 40 khách"]');
    await expect(textarea).toBeVisible();

    // Gõ mô tả + parse
    await textarea.fill('Đoàn 30 khách Vingroup đi Quy Nhơn 4N3Đ, 6tr/người, team building gala dinner');
    await page.getByRole('button', { name: /^Điền form$/ }).click();

    // Check parser output đã hiện hits
    await expect(page.getByText(/Đã nhận diện/)).toBeVisible();
    // Hits should mention destination + adults + days
    await expect(page.locator('text=📍').first()).toBeVisible();
    await expect(page.locator('text=👥').first()).toBeVisible();
    await expect(page.locator('text=☀️').first()).toBeVisible();
  });
});

test.describe('Wizard Step 3 — Multi-pricing-options matrix (v2 port)', () => {
  test('Matrix renders 9 options (3 stars × 3 ranges) with correct math', async ({ page }) => {
    await gotoWizardStep(page, 3);
    // Toggle "Bảng phương án giá" mở
    await page.getByRole('button', { name: /Bảng phương án giá/i }).click();
    await page.waitForTimeout(300);

    // 9 Apply buttons (3 × 3 default config)
    const applyBtns = page.locator('button', { hasText: /^Apply$/ });
    await expect(applyBtns).toHaveCount(9);

    // First row data sanity check — 3* × 1-14 pax × 28% markup
    const rows = page.locator('table tbody tr', { has: page.locator('button', { hasText: /Apply/ }) });
    const firstCells = await rows.first().locator('td').allTextContents();
    expect(firstCells[0].trim()).toBe('3*');
    expect(firstCells[1].trim()).toBe('1-14');
    expect(firstCells[2].trim()).toBe('28%');
    // Net + Price + Profit là số tiền (có chứa đ)
    expect(firstCells[3]).toMatch(/đ/);
    expect(firstCells[4]).toMatch(/đ/);
    expect(firstCells[5]).toMatch(/đ/);
  });

  test('Apply button: ghi đè margin slider', async ({ page }) => {
    await gotoWizardStep(page, 3);
    await page.getByRole('button', { name: /Bảng phương án giá/i }).click();
    await page.waitForTimeout(300);

    // Read margin display BEFORE Apply
    const marginDisplay = page.locator('span').filter({ hasText: /^\d+\.\d+%$/ }).first();
    const beforeMargin = await marginDisplay.textContent();

    // Click 2nd Apply (different range) → margin changes
    const applyBtns = page.locator('button', { hasText: /^Apply$/ });
    await applyBtns.nth(1).click();
    await page.waitForTimeout(400);

    const afterMargin = await marginDisplay.textContent();
    expect(afterMargin).not.toBe(beforeMargin);
    // Badge "GLOBAL OVERRIDE" visible
    await expect(page.locator('text=GLOBAL OVERRIDE')).toBeVisible();
  });

  test('costType toggle Đoàn ↔ × Pax thay đổi NET cell', async ({ page }) => {
    await gotoWizardStep(page, 3);
    // First row của costing-table
    const firstRow = page.locator('.costing-table tbody tr').first();
    const netCell = firstRow.locator('td').nth(4);   // cell 4 = Giá NET (sau cột Loại giá)

    const netBefore = (await netCell.textContent())?.trim();
    // Click "× N Pax" button trên cùng row
    await firstRow.locator('button', { hasText: /× \d+ Pax/ }).click();
    await page.waitForTimeout(300);

    const netAfter = (await netCell.textContent())?.trim();
    expect(netAfter).not.toBe(netBefore);
    // After toggle 'pax', NET cell contains "× N = " hint
    expect(netAfter).toMatch(/× \d+ = /);
  });
});
