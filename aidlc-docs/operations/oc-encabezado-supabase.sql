-- ============================================================
-- oc_encabezado en Supabase (PostgreSQL) — Fuente M2 modo cloud
-- ============================================================
-- Proyecto Supabase : PedidosMonitorDB (uzieandeucdqolerscgw)
-- Usada por         : SupabaseOrderRepository (IOrderSource) cuando DB_PROVIDER=supabase
-- Sembrada por      : OrderSeederService (carga inicial ÚNICA, solo si la tabla está vacía)
-- Independiente de  : simulated_orders (exclusiva de simulación U7)
-- ============================================================

-- ============================================================
-- 1. CREAR TABLA  (aplicada vía MCP apply_migration: create_oc_encabezado_m2_source)
-- ============================================================
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

-- ============================================================
-- 2. QUERY QUE USA M2 (SupabaseOrderRepository)
-- ============================================================
-- DbOrderChecker calcula la ventana en UTC y agrupa por channel_name.
SELECT channel_name, fecha_generacion
FROM oc_encabezado
WHERE fecha_generacion >= @From   -- UtcNow - WindowMinutes (regla activa: 10 min)
  AND fecha_generacion <= @To;    -- UtcNow

-- ============================================================
-- 3. SEED MANUAL DE PRUEBA (opcional — alternativa al OrderSeederService)
-- ============================================================
-- Inserta pedidos recientes para ambos canales (verde inmediato).
-- Ajustar los intervalos según la ventana de la regla M2.
INSERT INTO oc_encabezado (id_order, seller, channel_name, creation_date, fecha_generacion, estado_factura, estado_actual_orden)
VALUES
    ('SEED-SALESFORCE-1', 'SEED', 'SALESFORCE', now() - interval '1 minute', now() - interval '1 minute', 'OK', 1),
    ('SEED-SALESFORCE-2', 'SEED', 'SALESFORCE', now() - interval '4 minute', now() - interval '4 minute', 'OK', 1),
    ('SEED-SALESFORCE-3', 'SEED', 'SALESFORCE', now() - interval '7 minute', now() - interval '7 minute', 'OK', 1),
    ('SEED-MULTIVENDE-1', 'SEED', 'MULTIVENDE', now() - interval '1 minute', now() - interval '1 minute', 'OK', 1),
    ('SEED-MULTIVENDE-2', 'SEED', 'MULTIVENDE', now() - interval '4 minute', now() - interval '4 minute', 'OK', 1),
    ('SEED-MULTIVENDE-3', 'SEED', 'MULTIVENDE', now() - interval '7 minute', now() - interval '7 minute', 'OK', 1);

-- ============================================================
-- 4. VERIFICACIÓN
-- ============================================================
-- Conteo total y rango de fechas
SELECT COUNT(*) AS total, MIN(fecha_generacion) AS mas_antiguo, MAX(fecha_generacion) AS mas_reciente
FROM oc_encabezado;

-- Pedidos por canal en los últimos 10 min (lo que evalúa M2)
SELECT channel_name, COUNT(*) AS pedidos
FROM oc_encabezado
WHERE fecha_generacion >= (now() at time zone 'utc') - interval '10 minute'
GROUP BY channel_name;

-- ============================================================
-- NOTAS
-- ============================================================
-- · fecha_generacion es timestamp SIN zona — la app escribe/lee en UTC.
-- · El seeder NO es keep-alive: tras la carga inicial, si no llegan pedidos
--   nuevos las filas envejecen fuera de la ventana y M2 pasa a Critical.
-- · Para probar el camino Critical: vaciar la tabla o esperar a que pase la
--   ventana sin nuevos inserts.   TRUNCATE public.oc_encabezado;
-- · Modo DB_PROVIDER=sqlserver NO usa esta tabla — usa oc_encabezado en SQL Server.
--
-- SIMULACIÓN PERIÓDICA OPCIONAL (entornos de prueba):
-- · OrderFeederService inserta pedidos recientes cada OrderFeeder:IntervalMinutes
--   cuando OrderFeeder:Enabled=true (solo modo supabase). Marca filas con seller='SIM'
--   y limpia las simuladas más antiguas que OrderFeeder:RetentionHours.
-- · El intervalo DEBE ser menor que la ventana de la regla M2 para mantener verde.
-- · Desactivar (Enabled=false) en cuanto exista una fuente real de ingestión.
-- · Limpiar manualmente las filas simuladas:  DELETE FROM oc_encabezado WHERE seller = 'SIM';
