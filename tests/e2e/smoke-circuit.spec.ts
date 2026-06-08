import { test, expect } from '@playwright/test';

test('circuit conecta y timer funciona', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', msg => {
    if (msg.type() === 'error') errors.push(msg.text());
  });
  page.on('pageerror', err => errors.push(err.message));

  await page.goto('http://localhost:5000/Identity/Select');
  await page.click('text=Analista Operativo');
  await expect(page).toHaveURL(/dashboard/);

  await page.waitForTimeout(5000);

  const circuitErrors = errors.filter(e => e.includes('StartCircuit') || e.includes('blazor'));
  console.log('=== CONSOLA ERRORS ===', JSON.stringify(errors, null, 2));

  const countdownText = await page.locator('text=/Próxima actualización/').textContent().catch(() => '');
  console.log('=== COUNTDOWN TEXT ===', countdownText);

  const btnCheck = page.locator('button:has-text("Chequear ahora")');
  const btnEnabled = await btnCheck.isEnabled().catch(() => false);
  console.log('=== BTN CHEQUEAR ENABLED ===', btnEnabled);

  const timerEl = page.locator('.text-muted.small');
  const timerText = await timerEl.first().textContent().catch(() => 'NOT FOUND');
  console.log('=== TIMER TEXT ===', timerText);

  expect(circuitErrors).toHaveLength(0);
});
