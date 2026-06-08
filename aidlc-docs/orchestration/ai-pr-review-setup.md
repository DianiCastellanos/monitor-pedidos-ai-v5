# AI PR Review Setup — MonitorPedidos AI

## Estado

**Modalidad actual**: Code review manual — Diana Castellanos (owner)  
**Próximo paso**: Activar AI PR Review automatizado con OpenHands PR Review

---

## 1. Qué es y por qué

El code review automatizado es una capa de control que revisa cada PR usando IA antes de que llegue a revisión humana. Para MonitorPedidos AI esto importa porque:

- El arnés (Claude Code) produce cambios rápido — la velocidad amplifica errores sin revisión
- El codebase tiene áreas sensibles: autenticación cookie, antiforgery, conexión a BD productiva (READ-ONLY)
- Los checkers corren en background — un bug puede silenciarse sin tests que fallen

---

## 2. Arquitectura del flujo

```
Linear issue → task file → rama → PR → AI review (advisory) → revisión humana → merge → memoria
```

El review AI es **advisory** — la aprobación humana sigue siendo el gate de merge.

---

## 3. Workflow GitHub Actions

Archivo: `.github/workflows/ai-pr-review.yml`

```yaml
name: AI PR Review

on:
  pull_request:
    types: [opened, synchronize, reopened]
  issue_comment:
    types: [created]

jobs:
  ai-review:
    if: |
      github.event_name == 'pull_request' ||
      (github.event_name == 'issue_comment' &&
       contains(github.event.comment.body, '/review-this'))
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write
    steps:
      - name: AI PR Review
        uses: All-Hands-AI/OpenHands@<SHA-FIJO>
        with:
          task: |
            Revisa este PR como segundo revisor técnico.
            Foco: correctness, seguridad, compatibilidad, tests, mantenibilidad.
            El codebase es Blazor Server .NET 8 + SignalR + EF Core.
            Áreas sensibles: autenticación cookie, antiforgery, AppDbContext (usar IServiceScopeFactory
            para escritura), conexión ProductionDb (READ-ONLY, nunca DELETE/UPDATE/INSERT).
            Reporta hallazgos concretos con número de línea. Rol advisory — no bloquees el merge.
          github_token: ${{ secrets.GITHUB_TOKEN }}
          llm_api_key: ${{ secrets.AI_REVIEW_API_KEY }}
          llm_model: ${{ vars.AI_REVIEW_MODEL_ID }}
          llm_base_url: ${{ vars.AI_REVIEW_BASE_URL }}
```

---

## 4. Secrets y variables requeridos

### Secrets (Settings → Secrets → Actions)

| Secret | Valor |
|--------|-------|
| `AI_REVIEW_API_KEY` | API key del proveedor de IA (Anthropic, OpenAI, etc.) |

### Variables (Settings → Variables → Actions)

| Variable | Ejemplo |
|----------|--------|
| `AI_REVIEW_MODEL_ID` | `claude-sonnet-4-6` |
| `AI_REVIEW_BASE_URL` | `https://api.anthropic.com` |
| `AI_REVIEW_STYLE` | `concise` |
| `AI_REVIEW_REQUIRE_EVIDENCE` | `true` |

---

## 5. PR template

Archivo: `.github/pull_request_template.md`

```markdown
## Intención del cambio
<!-- Qué problema resuelve este PR -->

## Issue / Tarea de origen
<!-- Linear: DIA-XX | Task file: aidlc-docs/orchestration/tasks/0XX-*.md -->

## Scope tocado
<!-- Qué archivos y componentes cambian -->

## Comandos ejecutados
```
dotnet build → 0 errores
dotnet test  → X/X passing
```

## Evidencia
<!-- Output de tests, screenshot, log -->

## Riesgos conocidos
<!-- Áreas sensibles tocadas: auth, antiforgery, BD productiva -->

## Notas para reviewer
<!-- Qué debe validar el revisor humano -->
```

---

## 6. Branch protection recomendada

Settings → Branches → Add rule → `main`:

- [x] Require a pull request before merging
- [x] Require approvals: 1
- [x] Dismiss stale pull request approvals
- [x] Require conversation resolution before merging
- [ ] Require status checks (activar cuando el workflow esté live)

---

## 7. Áreas sensibles — guía para el reviewer AI

| Área | Invariant a preservar |
|------|-----------------------|
| `AppDbContext` en Blazor | Usar `IServiceScopeFactory` para escritura — nunca el contexto del circuito |
| `ProductionDb` | READ-ONLY — nunca DELETE, UPDATE, INSERT, ALTER |
| Antiforgery / cookies | `UseForwardedHeaders` debe ir antes de `UseAuthentication` |
| `.env` | Nunca commitear — está en `.gitignore` |
| `BackgroundServices` | Usar scope propio por operación — no inyectar `AppDbContext` como singleton |
| Checkers de monitoreo | Deben correr al arrancar la app (startup fix) |

---

## 8. Rerun manual

Para re-ejecutar el review en un PR existente, agregar el comentario:
```
/review-this
```

---

*Documento generado como artefacto de Estación 7 — Estatus: setup documentado, pendiente activación*
