# AI PR Review Setup — MonitorPedidos AI

## Estado

**Workflow activo**: `.github/workflows/ai-pr-review.yml`  
**Modelo**: gpt-4o-mini via GitHub Models (gratuito en repos publicos)  
**Auth**: `GITHUB_TOKEN` automatico — sin API key externa

---

## Arquitectura del flujo

```
Linear issue → task file → rama → PR → AI review (advisory) → revision humana → merge
```

---

## Areas sensibles — contexto dado al modelo

| Area | Invariant |
|------|----------|
| `AppDbContext` en Blazor | Usar `IServiceScopeFactory` para escritura |
| `ProductionDb` | READ-ONLY — nunca DELETE/UPDATE/INSERT/ALTER |
| Antiforgery | `UseForwardedHeaders` antes de `UseAuthentication` |
| `.env` | Nunca commitear |

---

*Estacion 7 — MonitorPedidos AI — 2026-06-08*
