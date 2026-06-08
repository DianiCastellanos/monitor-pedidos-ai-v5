# Plan de Code Review — MonitorPedidos AI

## Objetivo

Garantizar que cada cambio al codebase sea correcto, seguro y coherente con la arquitectura, sin acumular deuda técnica ni riesgos silenciosos en las áreas de mayor impacto operativo.

---

## 1. Tipos de review

| Tipo | Cuándo | Quién |
|------|--------|-------|
| **Manual (owner)** | Todo PR o commit significativo | Diana Castellanos |
| **AI review (advisory)** | Todo PR — segunda mirada automática | OpenHands PR Review (setup en `ai-pr-review-setup.md`) |
| **Review de arquitectura** | Cambios que afectan Program.cs, DI, middlewares, schema BD | Diana + contexto AI-DLC |

---

## 2. Qué se revisa en cada PR

### Correctness
- La lógica implementa exactamente lo que dice el task file
- Los acceptance criteria del task file quedan cumplidos
- No hay lógica invertida, condiciones vacías ni casos no manejados

### Seguridad
- No hay credenciales hardcodeadas — todo via `.env` (gitignored)
- `ProductionDb` (vtainternet_qa) no recibe escrituras — READ-ONLY absoluto
- Antiforgery activo en todos los forms POST
- Cookies con `HttpOnly`, `SecurePolicy` apropiada por entorno

### Arquitectura Blazor Server
- `AppDbContext` no se usa directamente en operaciones de escritura desde circuito
- Escrituras usan `IServiceScopeFactory` para crear scope propio
- `BackgroundServices` crean scope por operación, no reutilizan el singleton
- `UseForwardedHeaders` va antes de `UseAuthentication` en el pipeline

### Tests y evidencia
- `dotnet build` con 0 errores y 0 warnings nuevos
- `dotnet test` sin regresiones (baseline: 50/50)
- `npx playwright test` sin regresiones (baseline: 27/27)
- Si el cambio toca UI: screenshot o grabación del flujo
- Si el cambio toca APIs externas: log de respuesta o mock documentado

### Mantenibilidad
- Sin código muerto ni variables no usadas
- Nombres de clases, métodos y propiedades coherentes con los existentes
- Sin comentarios que explican QUÉ hace el código (los nombres ya lo dicen)
- Sin abstracciones prematuras — tres líneas similares es mejor que una abstracción innecesaria

### Compatibilidad
- `DB_PROVIDER` sigue funcionando para ambos modos: `supabase` y `sqlserver`
- Migraciones EF Core no rompen el esquema existente
- Variables de entorno nuevas documentadas en `.env.example` o `render-deployment-plan.md`

---

## 3. Áreas de alto riesgo — atención especial

| Área | Por qué es sensible |
|------|---------------------|
| `Program.cs` | Orden del pipeline es crítico — un middleware fuera de lugar rompe auth o antiforgery |
| `AppDbContext` / EF Core | Doble proveedor (SQL Server / Supabase) — un cambio puede romper uno de los dos modos |
| `OrderFeederService` | Burst mode — `BurstIntervalMinutes` vs `WindowMinutes` determina el ciclo OK→Crítico→OK |
| `DbOrderChecker` | Usa `condition.WindowMinutes` del repo de reglas, NO `appsettings` — error silencioso si se confunden |
| `SalesforceAuthHandler` | Token cache singleton — thread safety y expiración |
| `MonitoringSchedulerService` | Checkers deben correr al startup — riesgo de regresión si se cambia el timing |
| `Dockerfile` / `docker-entrypoint.sh` | Puerto dinámico `${PORT:-10000}` — hardcodear rompe Render |
| `.env` | Nunca commitear — verificar `.gitignore` antes de cualquier commit |

---

## 4. Proceso paso a paso

```
1. Crear rama desde main: feat/<tarea> o fix/<tarea>
2. Implementar con arnés (Claude Code)
3. Ejecutar: dotnet build → dotnet test → playwright test
4. Registrar evidencia en el cuerpo del PR (template)
5. Abrir PR → AI review comenta (advisory)
6. Review manual: Diana valida hallazgos del AI + criterios propios
7. Aprobar y mergear a main
8. Cerrar issue en Linear con referencia al PR
```

---

## 5. Criterios de rechazo (no mergear)

- `dotnet build` con errores
- Tests con regresiones respecto al baseline
- Credenciales en código o en commits
- Escritura sobre `ProductionDb`
- `.env` incluido en el commit
- Cambio en pipeline de `Program.cs` sin prueba de login end-to-end
- `AppDbContext` usado para escritura desde el circuito Blazor sin scope propio

---

## 6. Criterios de aprobación

- Build limpio
- Tests sin regresiones
- Acceptance criteria del task file cumplidos
- Evidencia registrada en el PR
- Áreas sensibles revisadas y sin hallazgos bloqueantes
- Revisión humana aprobada (Diana Castellanos)

---

## 7. Cadena completa

```
Linear issue (DIA-XX)
  → task file (aidlc-docs/orchestration/tasks/0XX-*.md)
  → rama feat/<tarea>
  → implementación con Claude Code
  → evidencia (build + tests + playwright)
  → PR con template completo
  → AI review advisory
  → revisión manual
  → merge a main
  → issue cerrado en Linear
  → memoria del ciclo
```

---

*Versión 1.0 — Estación 7 MonitorPedidos AI — 2026-06-08*
