# Plan Red-Team — Bloque 2
## RT3 (BD caída) · RT2 (Token Salesforce inválido)

**Fecha**: 2026-06-01  
**Estado**: EN EJECUCIÓN  

---

## Estrategia

Los checkers corren **antes** del primer tick del `PeriodicTimer`, por lo que
requieren esperar el intervalo configurado. En `Development`:

| Checker | Intervalo dev | Espera máxima en test |
|---|---|---|
| M2/M4/M11 (DbOrderChecker, DbHealthChecker, JobsChecker) | 30 s | 45 s |
| M3 Salesforce/Multivende (ApiCheckers) | 1 min | 75 s |

**Método**: Modificar `.env` → reiniciar app → Playwright verifica estado degradado
→ restaurar `.env` → reiniciar app. El `.env` es gitignored y nunca se commitea.

---

## RT3 — Base de datos caída

**Escenario PRD**: Apagar SQL Server → M4 detecta Critical inmediato  
**Método elegido**: Cambiar IP del servidor en `.env` por una inválida (`192.168.20.99`)  
**Tiempo de espera**: 45 s (checkers M2/M4 = 30 s + buffer)

### Comportamiento esperado (IT9 — Graceful Degradation)
- M4 BD Salud → dot `data-status="critical"`
- M2 BD Pedidos → dot `data-status="critical"`
- Dashboard → badge general muestra `CRÍTICO` o `ADVERTENCIA`
- Brand Monitor → fallback a LastCheckStore o skeleton (no crash)
- NOC → M4 y M2 cards en rojo

### Config a cambiar temporalmente
```
# ANTES (original)
ConnectionStrings__DefaultConnection=Server=tcp:<IP_SERVIDOR_BD>,1433;...
ConnectionStrings__ProductionDb=Server=tcp:<IP_SERVIDOR_BD>,1433;...

# DURANTE TEST (IP inválida → falla en 5s por Connect Timeout=5)
ConnectionStrings__DefaultConnection=Server=tcp:192.168.20.99,1433;...
ConnectionStrings__ProductionDb=Server=tcp:192.168.20.99,1433;...
```

---

## RT2 — Token Salesforce inválido

**Escenario PRD**: Revocar token Salesforce → M3 detecta 401 → alerta CRITICAL  
**Método elegido**: Poner ClientId/ClientPassword inválidos en `.env`  
**Tiempo de espera**: 75 s (checker API = 1 min + buffer)

### Comportamiento esperado (SOP-001)
- `SalesforceApiChecker` → HTTP 401 → `CheckStatus.Critical`
- `AlertTemplateRenderer` → crea incidente con `AccionSugerida` = "Renovar token manualmente según SOP-001"
- M3 NOC → `api-status-sf` con `data-status="critical"`
- M3 NOC/Dashboard → muestra "Sin respuesta" o "HTTP 401"

### Config a cambiar temporalmente
```
# ANTES (original)
Salesforce__ClientId=<SALESFORCE_CLIENT_ID>
Salesforce__ClientPassword=<SALESFORCE_CLIENT_PASSWORD>

# DURANTE TEST (credenciales inválidas)
Salesforce__ClientId=INVALID_CLIENT_ID
Salesforce__ClientPassword=INVALID_PASSWORD
```

---

## Errores encontrados durante ejecución

### E4 — SalesforceApiChecker no incluía "401" en el mensaje de error

**Archivo**: `src/MonitorPedidos.Web/Features/ApiChecks/SalesforceApiChecker.cs`  
**Síntoma**: RT2 test 4 falla — SOP-001 nunca aparece en el detalle del incidente  

**Causa raíz**:
`MonitoringService` implementa `BR-TOKEN-01` (línea 55): si `result.Details` contiene "401",
asigna `CauseCategory.Token` → usa template con SOP-001. Pero el checker devolvía:
```
"Error de autenticación — token inválido o expirado"   ← sin "401"
```
El `MonitoringService` no detectaba "401" → asignaba `CauseCategory.Api` →
template sin SOP-001.

**Corrección**:
```csharp
// ANTES
? $"Error de autenticación — token inválido o expirado"
// DESPUÉS
? $"HTTP 401 — token inválido o expirado"   // "401" activa BR-TOKEN-01
```

**Impacto**: La corrección hace que el incident de Salesforce con 401 ahora use el
template `(Token, Critical)` que incluye "Renovar token manualmente según SOP-001."
El `UpdateAlert` del IncidentService actualiza el incidente existente (sin crear duplicado).

---

### E5 — Ruta de historial de incidentes era incorrecta en el test

**Archivo**: `tests/e2e/tests/rt2-token-salesforce-invalido-muestra-critical-y-sop001.spec.ts`  
**Síntoma**: Row "M13 — Salesforce API" nunca encontrado — timeout 90s  

**Causa raíz**: El test usaba `/incidents/history` pero la ruta real del `HistoricPage.razor`
es `@page "/incidents"`.

**Corrección**: Cambiar `page.goto('/incidents/history')` → `page.goto('/incidents')`.

---

## Resultado final — ✅ Bloque 2 CERRADO

### RT3 — BD caída: 3/3 passing

```
ok RT3 — M4 BD Salud muestra Critical cuando BD no responde    39.6s
ok RT3 — M2 BD Pedidos muestra Critical cuando BD no responde  23.7s
ok RT3 — Dashboard no crashea con BD caída (graceful degradation)  0.7s

3 passed (1.1 min)
```

**Evidencia**: M4 detectó Critical en 39s, M2 en 23s. Connect Timeout=5s en
la cadena de conexión hace que el fallo sea rápido. IT9 graceful degradation
confirmado: el Dashboard cargó sin crash, todos los módulos visibles.

### RT2 — Token Salesforce inválido: 4/4 passing (con fix E4)

```
ok RT2 — M3 Salesforce muestra Critical con token inválido    22.8s
ok RT2 — M3 card global refleja Critical                      22.4s
ok RT2 — Dashboard M3 refleja falla de Salesforce              1.7s
ok RT2 — Detalle incidente muestra SOP-001 (BR-TOKEN-01)       2.6s

4 passed (51.5s)
```

**Evidencia**: Con token inválido, el checker detecta HTTP 401 en ~22s.
El incidente creado muestra `AccionSugerida = "Renovar token manualmente según SOP-001."`.
Fix aplicado en `SalesforceApiChecker.cs` — mensaje 401 ahora incluye el código HTTP.

### .env restaurado
Después de cada RT, las credenciales fueron restauradas a sus valores correctos.
El `.env` está en el estado original con BD y Salesforce válidos.
