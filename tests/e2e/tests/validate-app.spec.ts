import { test, expect } from '@playwright/test';
import { loginAs } from '../helpers/auth';

// ─── V1: App levanta, layout y tarjetas ──────────────────────────────────────
test('V1 — dashboard carga con navbar MainLayout y 4 tarjetas', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  // Navbar del MainLayout presente (fix Routes.razor: DefaultLayout restaurado)
  await expect(page.getByText('MonitorPedidos AI')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible();

  // 4 tarjetas de módulo
  await expect(page.locator('[data-testid="domain-card-M2"]')).toBeVisible();
  await expect(page.locator('[data-testid="domain-card-M3"]')).toBeVisible();
  await expect(page.locator('[data-testid="domain-card-M4"]')).toBeVisible();
  await expect(page.locator('[data-testid="domain-card-M11"]')).toBeVisible();

  // Estado global
  await expect(page.locator('[data-testid="overall-status"]')).toBeVisible();
});

// ─── V2: Timers de cards renderizados ────────────────────────────────────────
test('V2 — timer de cards muestra texto de actualización y countdown', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  // El texto de refresh (cards) debe aparecer
  await expect(page.getByText(/Próxima actualización en:/)).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText(/Actualizado hace:/)).toBeVisible({ timeout: 10_000 });

  // Verificar que el countdown tiene un valor numérico (Ns o Nm Ns)
  const countdownText = await page.locator('p.text-muted.small.mb-0').innerText();
  expect(countdownText).toMatch(/Próxima actualización en:\s+\d+/);

  console.log('Timer cards:', countdownText);
});

// ─── V3: Timer Brand Monitor renderizado ─────────────────────────────────────
test('V3 — timer Brand Monitor muestra próxima consulta automática', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  // Esperar que brand monitor se cargue (desaparece el spinner)
  await expect(page.locator('[data-testid="brand-loading"]')).not.toBeVisible({ timeout: 15_000 });

  // El texto de countdown del brand monitor debe aparecer
  await expect(page.getByText(/Próxima consulta automática en:/)).toBeVisible({ timeout: 10_000 });

  const brandText = await page.locator('p.text-muted.small.mb-1').first().innerText();
  console.log('Brand timer:', brandText);
  expect(brandText).toMatch(/Próxima consulta automática en:\s+\d+/);
});

// ─── V4: Botón "Chequear ahora" funciona ─────────────────────────────────────
// Nota: el estado `disabled` puede ser < 100ms (muy transitorio), el patrón
// correcto es verificar que el botón regresa a enabled. Ver D3.
test('V4 — botón Chequear ahora ejecuta checkers y regresa habilitado', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  const btn = page.locator('[data-testid="btn-check-now"]');
  await expect(btn).toBeEnabled({ timeout: 10_000 });

  const start = Date.now();
  await btn.click();

  // Debe volver a habilitarse en máx 12s (5s checkers + 300ms delay + overhead)
  await expect(btn).toBeEnabled({ timeout: 12_000 });
  const elapsed = Date.now() - start;
  console.log(`Chequear ahora completó en ${elapsed}ms`);

  // Countdown visible y cards siguen presentes
  await expect(page.getByText(/Próxima actualización en:/)).toBeVisible();
  await expect(page.locator('[data-testid="domain-card-M2"]')).toBeVisible();
});

// ─── V5: Botón "Consultar Salesforce" funciona ───────────────────────────────
test('V5 — botón Consultar Salesforce ejecuta consulta y tabla permanece', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  // Esperar carga inicial brand monitor
  await expect(page.locator('[data-testid="brand-loading"]')).not.toBeVisible({ timeout: 15_000 });

  const btnSf = page.locator('[data-testid="btn-brand-refresh"]');
  await expect(btnSf).toBeEnabled({ timeout: 10_000 });

  await btnSf.click();

  // Debe volver a habilitarse tras la consulta (timeout generoso para red real)
  await expect(btnSf).toBeEnabled({ timeout: 25_000 });
  console.log('Consultar Salesforce completó ✓');

  // Tabla brand monitor sigue visible
  const table = page.locator('[data-testid="brand-monitor-table"]').first();
  await expect(table).toBeVisible();

  // Timer actualizado
  await expect(page.getByText(/Próxima consulta automática en:/)).toBeVisible();
});

// ─── V6: Brand Monitor muestra datos de las 4 marcas ─────────────────────────
test('V6 — Brand Monitor muestra PatPrimo, SevenSeven, Atmos, Ostu', async ({ page }) => {
  await loginAs(page, 'Operador');
  await page.goto('/dashboard');

  await expect(page.locator('[data-testid="brand-loading"]')).not.toBeVisible({ timeout: 15_000 });

  const table = page.locator('[data-testid="brand-monitor-table"]').first();
  await expect(table).toBeVisible();

  for (const site of ['PatPrimo', 'SevenSeven', 'Atmos', 'Ostu']) {
    await expect(table.getByText(site, { exact: true })).toBeVisible();
    console.log(`✓ Marca visible: ${site}`);
  }
});
