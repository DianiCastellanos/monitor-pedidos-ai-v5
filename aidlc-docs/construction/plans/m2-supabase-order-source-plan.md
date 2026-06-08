# Plan: M2 sin SQL Server — `oc_encabezado` nativo en Supabase (Opción B)

> Objetivo: cuando `DB_PROVIDER=supabase`, M2 (DbOrderChecker) lee pedidos
> desde una tabla `oc_encabezado` propia en Supabase mediante un
> `SupabaseOrderRepository` dedicado. `simulated_orders` queda **exclusivo
> para U7/simulación**. Modo `DB_PROVIDER=sqlserver` sin cambios.

---

## 1. Esquema propuesto — `public.oc_encabezado` en Supabase (PostgreSQL)

Espejo del `oc_encabezado` de SQL Server, con nombres snake_case (convención del proyecto en Supabase):

```sql
CREATE TABLE IF NOT EXISTS public.oc_encabezado (
    id_aut_order        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    id_order            varchar(100),
    seller              varchar(200),
    channel_name        varchar(100) NOT NULL,   -- 'SALESFORCE' | 'MULTIVENDE'
    creation_date       timestamp,
    fecha_generacion    timestamp    NOT NULL,   -- campo que M2 usa para la ventana
    estado_factura      varchar(100),
    estado_actual_orden integer
);

CREATE INDEX IF NOT EXISTS ix_oc_encabezado_fecha_channel
    ON public.oc_encabezado (fecha_generacion, channel_name);
```

Se aplica vía **Supabase MCP `apply_migration`** (no por EF — en modo supabase
EF no ejecuta `Migrate()`). Se guarda copia del DDL en
`aidlc-docs/operations/oc-encabezado-supabase.sql`.

## 2. Campos que M2 realmente consume

`DbOrderChecker` solo necesita 2 campos (idéntico al path SQL Server, que hace `SELECT ChannelName, FechaGeneracion`):

| Campo Supabase | Mapea a `OrderSnapshot` | Uso en M2 |
|----------------|------------------------|-----------|
| `channel_name` | `OrderId` | Agrupar y contar por canal |
| `fecha_generacion` | `CreatedAt` | Filtro de ventana temporal |
| (no se lee) | `IsCancelled = false` | Paridad con path SQL (que tampoco lo lee) |

Los demás campos (`id_order`, `seller`, `creation_date`, `estado_*`) se incluyen
por fidelidad al esquema original y uso futuro, pero **M2 no los consume hoy**.

Regla activa M2 (sembrada en `rules`): `WindowMinutes=10, MinOrders=1, Channels=["SALESFORCE","MULTIVENDE"]`.

## 3. Archivos a crear

| Archivo | Propósito |
|---------|-----------|
| `src/MonitorPedidos.Infrastructure/Persistence/SupabaseOrderRepository.cs` | `IOrderSource` vía **NpgsqlConnection + Dapper**. Lee `oc_encabezado` (snake_case). Espejo simétrico de `ProductionOrderRepository`. |
| `src/MonitorPedidos.Web/BackgroundServices/OrderSeederService.cs` | Hosted service **one-shot**: solo en modo supabase + `OrderSeed:Enabled=true`, siembra pedidos recientes al arrancar. |
| `aidlc-docs/operations/oc-encabezado-supabase.sql` | DDL + queries de referencia (documentación). |

## 4. Archivos a modificar

| Archivo | Cambio |
|---------|--------|
| `Program.cs` | Ruteo `IOrderSource`: `supabase` → `SupabaseOrderRepository(supabaseConn)` **siempre** (ignora `ProductionDb`). `sqlserver` → `ProductionOrderRepository` si hay `ProductionDb`, si no `SimulatedOrderRepository` (sin cambios). Registrar `OrderSeederService`. |
| `appsettings.json` | Nueva sección `OrderSeed: { Enabled: true, OrdersPerChannel: 3 }`. |
| `appsettings.Development.json` | Idem (Enabled=true para pruebas locales). |

## 5. Archivos que NO se tocan (garantía de no-ruptura)

- `SimulatedOrderRepository.cs` — sigue para U7 (`ISimulatedOrderRepository`). En supabase ya no se registra como `IOrderSource`.
- `DbOrderChecker.cs`, `IOrderSource.cs`, `OrderSnapshot.cs` — sin cambios.
- `OrderSyncService.cs` — ya se auto-desactiva en supabase.
- `AppDbContext.cs` + todas las migraciones EF — **sin cambios** (no se añade DbSet → no hay migración nueva → `Migrate()` en SQL Server no cambia).
- `ProductionOrderRepository.cs` — sin cambios.

## 6. Estrategia de migración / no romper SQL Server

1. **Sin cambios en EF**: `SupabaseOrderRepository` usa Dapper+Npgsql directo, no EF. No se añade `DbSet<OcEncabezado>` → no se genera migración → el `db.Database.Migrate()` del modo sqlserver queda idéntico.
2. **DDL de Supabase fuera de banda**: la tabla se crea con MCP `apply_migration`. El modo supabase ya salta `Migrate()`, así que no hay colisión.
3. **Instanciación condicional**: `SupabaseOrderRepository` solo se construye si `DB_PROVIDER=supabase`. En sqlserver nunca se toca.
4. **Path SQL Server intacto**: `ProductionOrderRepository` + `oc_encabezado` (SQL) + `OrderSyncService` siguen exactamente igual.

## 7. Cómo se alimentan las fechas recientes (y por qué NO son perpetuas)

- **Seeder one-shot al arranque**: inserta `OrdersPerChannel` pedidos por canal con `fecha_generacion` repartida en los últimos ~9 min (ej. `now-1m`, `now-4m`, `now-7m`). M2 verde desde el primer chequeo.
- **No hay loop de refresco**: tras el arranque el seeder termina. Si **no llegan pedidos nuevos**, las filas envejecen fuera de la ventana de 10 min → M2 pasa a **Critical** según la regla existente. Esto cumple "evitar datos artificialmente perpetuos".
- Para pruebas del camino verde: reiniciar la app re-siembra. Para probar warning/critical: `OrderSeed:Enabled=false` o esperar a que pase la ventana.

## 8. Riesgos

| Riesgo | Mitigación |
|--------|-----------|
| Zona horaria: `fecha_generacion` es `timestamp` sin tz | Seeder escribe `UtcNow`; repo lee como UTC (`DateTimeOffset(.., Zero)`), simétrico con la ventana UTC de DbOrderChecker. |
| Re-seed acumula filas en reinicios frecuentes | Seeder one-shot + limpieza opcional; volumen trivial para pruebas. |
| 🔴 RLS deshabilitado en Supabase (6 tablas) | La nueva `oc_encabezado` también tendrá RLS off por defecto. Decisión separada del usuario. |

## 9. Checklist de implementación (tras aprobación)

- [ ] Aplicar DDL `oc_encabezado` en Supabase (MCP `apply_migration`)
- [ ] Crear `SupabaseOrderRepository.cs`
- [ ] Crear `OrderSeederService.cs`
- [ ] Modificar `Program.cs` (ruteo + registro seeder)
- [ ] Añadir `OrderSeed` a ambos appsettings
- [ ] Guardar `oc-encabezado-supabase.sql`
- [ ] `dotnet build` sin errores
- [ ] Levantar en supabase → M2 verde; verificar logs DbOrderChecker
- [ ] Confirmar modo sqlserver sin cambios (revisión de diff)
