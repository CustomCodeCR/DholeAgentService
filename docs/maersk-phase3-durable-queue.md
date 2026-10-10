# Hermes / DholeAgentService — Fase 3: cola duradera y concurrencia segura

## Fuentes de verdad y activación
PostgreSQL `agent."AgentExecutions"` es la cola autoritativa. Redis Streams solo notifica; la entrega duplicada de mensajes Redis no dispara ejecuciones. El dispatcher nuevo se activa con `AgentQueue:ConcurrentDispatcherEnabled=true` **únicamente después de aplicar la migración y verificar el rollout**. El valor en el repositorio permanece `false` para que un despliegue no active consultas a tablas todavía no migradas.

Se registra **un solo** dispatcher en DI por worker: el `QueuedExecutionBackgroundService` anterior si el flag es falso, o el nuevo `ConcurrentQueuedExecutionBackgroundService` si es verdadero. Nunca registrar simultáneamente ambos en el mismo host.

### Configuración predeterminada
```json
{
  "AgentQueue": {
    "ConcurrentDispatcherEnabled": false,
    "MaxConcurrentExecutions": 4,
    "MaxConcurrentMaersk": 1,
    "MaxConcurrentPerBrowserProfile": 1,
    "PollIntervalSeconds": 1,
    "LeaseSeconds": 180,
    "HeartbeatSeconds": 20,
    "MaxTransientRetries": 2,
    "RetryBackoffSeconds": [15, 60]
  }
}
```

Estas opciones son validables, no constantes del código. Maersk usa una única ejecución por proveedor **globalmente** entre procesos que comparten la misma base de datos, con scope de PostgreSQL `maersk:<providerId>`. Para otros proveedores el scope es `execution:<executionId>`. El lock de directorio de Chromium de la fase 2 continúa protegiendo cada perfil incluso en otros procesos. Múltiples servicios locales pueden tener cada uno un máximo de 4 tareas; Maersk queda limitado globalmente a 1 en este entorno/DB.

## Contrato de lease e idempotencia
La migración `20261009234500_AddDurableExecutionLeases` agrega:
- `agent.execution_leases` con `lease_scope` como PK, `execution_id` único, `owner_id`, `expires_at_utc` y `heartbeat_at_utc`;
- `next_attempt_at_utc` nullable en `AgentExecutions` y un índice de cola `Queued`.

El claim es un `INSERT ... ON CONFLICT DO UPDATE ... WHERE expires_at_utc < NOW()` condicionado a que el trabajo esté `Queued`, listo y por debajo de `MaxAttempts`. Un solo proceso gana el lease. Los heartbeats renuevan cada 20 segundos; si el owner no consigue renovarlo, se cancela su ejecución en vez de seguir trabajando sin ownership. La liberación exige `scope + execution + owner`, por lo que un proceso anterior no puede liberar el lease nuevo de otro proceso.

La ejecución se hace con un `IServiceScope` y `DbContext` propio. Las consultas de descubrimiento, heartbeat, trabajo y liberación no comparten `DbContext`.

## Fallos y reintentos
- `WaitingForAuthentication` (CAPTCHA/MFA/403/429): **no** se reclama, no se reintenta, no se migra a staging/prod y no rota IP/cookies/perfil.
- `maersk_offer_timeout` y `maersk_all_searches_failed`: no dispara reset ni reintentos ciegos.
- Fallo técnico explícitamente recuperable: con flag habilitado, hasta dos reintentos diferidos (15 y 60 segundos) sin crear nueva ejecución. Se conservan `executionId`, `correlationId`, `inputJson`, `PromptSnapshot` y `ConfigurationSnapshotJson`.
- Intentos agotados: terminar como `Failed`. Ningún `Start()` permite exceder `MaxAttempts`.
- El proceso interrumpido con un lease expirado queda `Failed / agent_worker_interrupted` para revisión; **no se reejecutan automáticamente** posibles efectos externos.
- Ejecuciones históricas en `Running` sin lease y anteriores a 12 horas también pasan a `Failed / agent_legacy_execution_interrupted`; revisar el resultado externo antes de reenviar.
- `AgentResults.ExecutionId` tiene índice único ya existente para impedir dos resultados por ejecución.

### Equidad y aislamiento
La cola alterna una cuota de tres trabajos manuales por cada trabajo programado, y también consulta explícitamente otros proveedores para que un backlog de Maersk no acapare toda la ventana. Cuando la sesión Maersk está esperando verificación, solo consume el scope correspondiente y no bloquea las demás tareas.

## Procedimiento de despliegue (obligatorio)
1. Crear un respaldo de PostgreSQL y los volúmenes actuales de perfiles persistentes; conservar las rutas por ambiente.
2. Aplicar primero la migración en staging, verificar tabla/columnas/índices. Ejecutar `dotnet test` y prueba de integración con PostgreSQL real.
3. Desplegar código en todos los workers con el flag **false**; dejar terminar las ejecuciones `Running` del despachador anterior antes de cambiar los flags.
4. Habilitar `AgentQueue__ConcurrentDispatcherEnabled=true` solo en staging con workers coordinados; observar 2 consumidores intentando reclamar el mismo job, ausencia de duplicación, heartbeat y tareas de otros proveedores durante bloqueo simulado.
5. Comprobar que las ejecuciones `WaitingForAuthentication` no se reencolan, que un restart no deja ejecuciones `Running` indefinidas, y que los resultados/Outbox no se duplican.
6. Repetir con ventana de despliegue controlada en producción, sin cambios automáticos de perfiles o cookies.
7. En rollback desactivar flag; **no borrar** `agent.execution_leases`, registros de ejecuciones, migraciones o perfiles. Antes de activar el dispatcher anterior, drenar los nuevos leases y asegurar que no quedan instancias con el dispatcher concurrente activo.

**Limitaciones verificables:** mientras el flag esté desactivado, continúa el comportamiento secuencial anterior. Una caída durante operaciones externas no es automáticamente recuperable de forma segura: la ejecución se detiene como interrumpida y necesita revisión. Los cambios de circuito persistente y verificación administrativa corresponden a la fase 4/5 y no se han introducido aquí.
