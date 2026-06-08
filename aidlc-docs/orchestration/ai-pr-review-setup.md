# AI PR Review Setup — MonitorPedidos AI

## Estado

**Modalidad actual**: AI PR Review activo con GitHub Models  
**Modelo**: gpt-4o-mini (gratuito en repos públicos)  
**Workflow**: `.github/workflows/ai-pr-review.yml`

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

## 3. Cómo funciona

1. Se abre un PR → el workflow se activa automáticamente
2. Obtiene el diff del PR (máx 8000 chars)
3. Envía el diff a gpt-4o-mini con contexto de MonitorPedidos AI
4. Posta un comentario con hallazgos en el PR
5. El revisor humano (Diana) evalúa los hallazgos y decide

---

## 4. Configuración

- **Auth**: `GITHUB_TOKEN` automático — sin API key externa
- **Endpoint**: `https://models.inference.ai.azure.com/chat/completions`
- **Trigger**: `pull_request` (opened, synchronize, reopened)
- **Costo**: gratuito en repos públicos

---

## 5. Áreas sensibles — contexto dado al modelo

| Área | Invariant a preservar |
|------|-----------------------|
| `AppDbContext` en Blazor | Usar `IServiceScopeFactory` para escritura — nunca el contexto del circuito |
| `ProductionDb` | READ-ONLY — nunca DELETE, UPDATE, INSERT, ALTER |
| Antiforgery / cookies | `UseForwardedHeaders` debe ir antes de `UseAuthentication` |
| `.env` | Nunca commitear — está en `.gitignore` |
| `BackgroundServices` | Usar scope propio por operación |
| Checkers de monitoreo | Deben correr al arrancar la app |

---

*Versión 1.2 — Estación 7 MonitorPedidos AI — 2026-06-08 — Workflow activo*
