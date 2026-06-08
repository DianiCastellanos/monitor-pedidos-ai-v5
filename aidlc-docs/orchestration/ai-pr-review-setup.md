# AI PR Review Setup — MonitorPedidos AI

## Estado

**Modalidad actual**: AI PR Review activo — GitHub Models (gpt-4o-mini)  
**Activado**: 2026-06-07  
**Primer test exitoso**: PR #1 en `monitor-pedidos-ai-v5`, respuesta en ~11 segundos  
**Próxima exploración**: OpenHands PR Review (pendiente — ver sección 8)

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

## 3. Implementación activa: GitHub Models (gpt-4o-mini)

**Por qué GitHub Models** como primera implementación:
- Gratuito — usa el `GITHUB_TOKEN` integrado, sin API key externa
- Sin configuración de secrets adicional
- Acceso via `https://models.inference.ai.azure.com`
- El token `${{ secrets.GITHUB_TOKEN }}` ya tiene permiso `models: read`

---

## 4. Workflow GitHub Actions (activo)

**Repositorio**: `monitor-pedidos-ai-v5`  
**Archivo**: `.github/workflows/ai-pr-review.yml`

```yaml
name: AI PR Review
on:
  pull_request:
    types: [opened, synchronize, reopened]
jobs:
  ai-review:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write
      models: read
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - name: AI Review via GitHub Models
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          PR_NUMBER: ${{ github.event.pull_request.number }}
          REPO: ${{ github.repository }}
        run: |
          git fetch origin ${{ github.base_ref }}
          DIFF=$(git diff origin/${{ github.base_ref }}...HEAD | head -c 6000)
          if [ -z "$DIFF" ]; then echo "Sin cambios de codigo."; exit 0; fi
          SYSTEM="Revisor de MonitorPedidos AI (Blazor Server .NET 8 + EF Core). Revisa el diff. Areas sensibles: ProductionDb READ-ONLY (nunca DELETE/UPDATE/INSERT), AppDbContext usar IServiceScopeFactory para escritura, antiforgery. Bullet points, max 200 palabras. Rol advisory."
          PAYLOAD=$(jq -n --arg s "$SYSTEM" --arg d "$DIFF" '{"model":"gpt-4o-mini","messages":[{"role":"system","content":$s},{"role":"user","content":$d}],"max_tokens":400}')
          RESPONSE=$(curl -sf "https://models.inference.ai.azure.com/chat/completions" -H "Authorization: Bearer $GH_TOKEN" -H "Content-Type: application/json" -d "$PAYLOAD")
          COMMENT=$(echo "$RESPONSE" | jq -r '.choices[0].message.content // "No se pudo generar el review."')
          gh pr comment "$PR_NUMBER" --repo "$REPO" --body "$(printf '## Robot AI PR Review (Advisory)\n\n%s\n\n---\n*Revision automatica con GitHub Models (gpt-4o-mini). Aprobacion humana requerida.*' "$COMMENT")"
```

**Clave técnica**: Usa `jq -n` para construir el JSON — evita el problema de multiline Python en YAML literal block scalar.

---

## 5. PR template

**Archivo**: `.github/pull_request_template.md`

El template se aplica automáticamente a todo PR nuevo. Secciones:
- Intención del cambio
- Issue / Tarea de origen (Linear DIA-XX + task file)
- Scope tocado
- Comandos ejecutados (`dotnet build` + `dotnet test`)
- Evidencia
- Riesgos conocidos
- Notas para reviewer

---

## 6. Evidencia del primer test

**PR #1** — `feat(estacion7): docs y workflow AI review — evidencia Estación 7`  
**Repo**: `monitor-pedidos-ai-v5`  
**Resultado**: ✅ Workflow `AI PR Review` completado en ~11 segundos

**Comentario generado por el bot** (extracto):
```
## Robot AI PR Review (Advisory)

- **YAML Workflow**: El archivo de workflow (`ai-pr-review.yml`) parece correcto.
  Se utilizan permisos mínimos y el flujo parece bien estructurado.
- **Plan de revisión (`code-review-plan.md`)**: Bien documentado. Se mencionan
  áreas críticas como `ProductionDb READ-ONLY` y el uso de `IServiceScopeFactory`.
- **Configuración de revisión (`ai-pr-review-setup.md`)**: El enfoque con GitHub
  Models es adecuado y ahorra costos. Asegúrate de mantener actualizados los
  modelos disponibles.

---
*Revision automatica con GitHub Models (gpt-4o-mini). Aprobacion humana requerida.*
```

---

## 7. Cómo navegar a los resultados en GitHub

### Ver el workflow corriendo / resultado

1. Ir al repo en GitHub: `github.com/<owner>/monitor-pedidos-ai-v5`
2. Click en la pestaña **Actions** (barra horizontal superior)
3. En el panel izquierdo aparece la lista de workflows — click en **AI PR Review**
4. Verás cada ejecución con su estado (✅ / ❌) y duración
5. Click en una ejecución → click en el job **ai-review** → expande el step **AI Review via GitHub Models** para ver el log completo

### Ver el comentario del bot en un PR

1. Click en la pestaña **Pull requests**
2. Click en el PR abierto
3. Scroll hacia abajo — el bot (`github-actions`) publica el review con encabezado `## Robot AI PR Review (Advisory)`

### Camino rápido desde el dashboard

```
github.com/<owner>/monitor-pedidos-ai-v5
  └─ Actions                           ← tab superior
       └─ AI PR Review (panel izq.)
            └─ [run #N] feat(...)      ← click en la ejecución
                 └─ ai-review          ← click en el job
                      └─ AI Review via GitHub Models  ← expande el step
```

---

## 8. Áreas sensibles — guía para el reviewer AI

| Área | Invariant a preservar |
|------|-----------------------|
| `AppDbContext` en Blazor | Usar `IServiceScopeFactory` para escritura — nunca el contexto del circuito |
| `ProductionDb` | READ-ONLY — nunca DELETE, UPDATE, INSERT, ALTER |
| Antiforgery / cookies | `UseForwardedHeaders` debe ir antes de `UseAuthentication` |
| `.env` | Nunca commitear — está en `.gitignore` |
| `BackgroundServices` | Usar scope propio por operación — no inyectar `AppDbContext` como singleton |
| Checkers de monitoreo | Deben correr al arrancar la app (startup fix) |

---

## 9. Branch protection recomendada

Settings → Branches → Add rule → `main`:

- [x] Require a pull request before merging
- [x] Require approvals: 1
- [x] Dismiss stale pull request approvals
- [x] Require conversation resolution before merging
- [ ] Require status checks (activar cuando el workflow esté en repos que lo usen)

---

## 10. Exploración futura: OpenHands PR Review

**Estado**: Pendiente — documentado para evaluación futura

OpenHands (All-Hands AI) es un agente de code review más profundo que puede:
- Leer el codebase completo, no solo el diff
- Ejecutar los tests y verificar que pasan
- Proponer correcciones directamente en el código (no solo comentarios)
- Entender el contexto de arquitectura completo (Blazor Server, EF Core, SignalR)

### Configuración OpenHands (cuando se active)

```yaml
# Reemplazar el job `ai-review` en ai-pr-review.yml por:
jobs:
  ai-review:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write
    steps:
      - name: AI PR Review (OpenHands)
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

### Secrets requeridos para OpenHands

| Secret | Valor |
|--------|-------|
| `AI_REVIEW_API_KEY` | API key del proveedor (Anthropic o OpenAI) |

| Variable | Ejemplo |
|----------|---------|
| `AI_REVIEW_MODEL_ID` | `claude-sonnet-4-6` |
| `AI_REVIEW_BASE_URL` | `https://api.anthropic.com` |

### Comparativa GitHub Models vs OpenHands

| Criterio | GitHub Models (activo) | OpenHands (futuro) |
|----------|------------------------|---------------------|
| Costo | Gratis | Requiere API key de pago |
| Velocidad | ~11s | ~2-5 min |
| Profundidad | Diff únicamente | Codebase completo |
| Ejecuta tests | No | Sí |
| Propone fixes | No | Sí |
| Configuración | Ninguna | Secrets + variables |

> Recomendación: mantener GitHub Models como primera línea (rápido, gratis) y activar OpenHands en PRs de alto riesgo o cuando se tenga API key disponible.

---

*Versión 2.0 — GitHub Models activo desde 2026-06-07 — OpenHands pendiente evaluación — Estación 7 MonitorPedidos AI*
