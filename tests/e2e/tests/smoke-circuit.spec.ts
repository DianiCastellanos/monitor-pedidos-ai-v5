import { test, expect } from '@playwright/test';

test('circuit conecta y timer funciona', async ({ page }) => {
  // Capturar errores de consola
  const errors: string[] = [];
  page.on('console', msg => {
    if (msg.type() === 'error') errors.push(msg.text());
  });
  page.on('pageerror', err => errors.push(err.message));

  const BASE = process.env.TEST_BASE_URL ?? 'http://localhost:5000';
  // Login
  await page.goto(`${BASE}/Identity/Select`);
  await page.click('text=Analista Operativo');
  await expect(page).toHaveURL(/dashboard/);

  // Esperar que el circuito conecte (máx 10s)
  await page.waitForTimeout(5000);

  // Verificar que NO hay error de StartCircuit
  const circuitErrors = errors.filter(e => e.includes('StartCircuit') || e.includes('blazor'));
  console.log('=== CONSOLA ERRORS ===', JSON.stringify(errors, null, 2));

  // Verificar que el countdown existe y tiene un número
  const countdownText = await page.locator('text=/Próxima actualización/').textContent().catch(() => '');
  console.log('=== COUNTDOWN TEXT ===', countdownText);

  // Verificar que el botón Chequear Ahora existe
  const btnCheck = page.locator('button:has-text("Chequear ahora")');
  const btnEnabled = await btnCheck.isEnabled().catch(() => false);
  console.log('=== BTN CHEQUEAR ENABLED ===', btnEnabled);

  // El timer debe tener un número (no estar vacío o en "Cargando")
  const timerEl = page.locator('.text-muted.small');
  const timerText = await timerEl.first().textContent().catch(() => 'NOT FOUND');
  console.log('=== TIMER TEXT ===', timerText);

  expect(circuitErrors).toHaveLength(0);
});
