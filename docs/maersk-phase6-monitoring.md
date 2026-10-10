# Hermes / Maersk — Fase 6: instrumentación y alertas operativas

## Alcance implementado
La fase 6 toma señales locales de PostgreSQL del servicio Agent, **sin llamar a Maersk** ni cambiar identidades, IP, cookies, perfiles, navegador o sesión. Los operadores pueden consultar y reconocer alertas desde `/agents/maersk`.

No se cambia el circuit breaker de fase 4, ni el dispatcher de fase 3. Reconocer una alerta **nunca** cambia `maersk_circuits` ni borra intentos o resultados. Los bloqueos por CAPTCHA, 403 o 429 continúan requiriendo verificación legítima con el proveedor y reset humano separado.

### Señales y reglas predeterminadas

| Clave | Severidad | Señal |
|---|---|---|
| `provider-access` | Critical | Circuito `Open` con `RequiresOperator` (si el circuito está habilitado) |
| `browser-profile-blocked` | Critical | Uno o más perfiles Maersk activos en `Blocked` |
| `half-open-stalled` | Warning | `HalfOpen` sin resolver por 20 minutos (si el circuito está habilitado) |
| `queue-backlog` | Warning | Uno o más jobs `Queued` hace 20 minutos o más, excluyendo backoff futuro |
| `running-stalled` | Warning | Una ejecución `Running` por 75 minutos o más |
| `execution-failure-spike` | Warning | Cinco fallos terminales en una ventana de 60 minutos |

Se incluyen además: timestamp del job más antiguo pendiente, último completado correctamente, fallos en ventana, jobs demorados en cola y prolongados en ejecución. Son métricas observacionales, no confirmaciones de disponibilidad del proveedor.

### Persistencia y concurrencia
Migración `20261010040000_AddMaerskHealthAlerts`:
- `agent.maersk_health_alerts` registra clave, código estructurado, severidad, estado Active/Resolved, primer/último avistamiento, cantidad de episodios, reconocimiento y actor.
- Índice único `(provider_id, alert_key)` evita duplicados aun con múltiples workers. `INSERT ... ON CONFLICT` mantiene un único registro por señal; los polls no incrementan `occurrences` salvo reapertura posterior a resolución.
- Una alerta desaparece de la lista activa solo si su señal ya no existe; el registro se conserva como Resolved. Si reaparece, conserva ID, incrementa episodios y exige un nuevo reconocimiento.
- Las transacciones protegen la evaluación completa. No se almacenan mensajes completos de fallos, cookies, respuestas del proveedor ni contraseñas.
- El monitor captura fallos del ciclo, registra logs estructurados y continúa en el siguiente poll. No emite búsquedas automáticas.

### API y pantalla
- `GET /api/agents/maersk/operations` agrega `monitoring` con métricas, alertas y `monitoringEnabled`. Mantiene los scopes de lectura de ejecuciones y perfiles.
- `POST /api/agents/maersk/alerts/{alertId}/acknowledge` usa el scope administrativo existente `agent.browser-profiles.authenticate`, exige actor autenticado y solo admite alertas activas no reconocidas. En conflicto devuelve 409.
- DholeWeb usa `/agents/maersk` para mostrar severidad, códigos, timestamps, métricas, indicador de monitor desactivado y acción de reconocimiento si se tiene el permiso.
- La pantalla sigue siendo bilingüe y responsive, con polling que se suspende en pestañas ocultas. El botón de reconocimiento no se debe confundir con `reset` del circuito.

### Flags de activación
```json
{
  "MaerskMonitoring": {
    "Enabled": false,
    "PollIntervalSeconds": 60,
    "QueuedWarningMinutes": 20,
    "RunningWarningMinutes": 75,
    "FailureWindowMinutes": 60,
    "FailureCountThreshold": 5,
    "HalfOpenWarningMinutes": 20
  }
}
```

**El valor predeterminado permanece false.** La nueva tabla no se lee ni se escribe desde el monitor cuando está deshabilitado; la API sí calcula métricas ordinarias de ejecuciones. El circuito de fase 4 y la cola de fase 3 mantienen sus propias banderas independientes.

### Checklist de despliegue
1. Confirmar los despliegues de fase 5 y tomar respaldo de PostgreSQL. Aplicar la migración en staging **antes** de habilitar el monitor.
2. Verificar `agent.maersk_health_alerts` y los índices únicos, ejecutar CI de .NET 10 y PostgreSQL 16 con dos evaluaciones concurrentes y prueba de reconocimiento.
3. Desplegar primero Agent (API y worker) con flag `false`; luego DholeWeb. Las alertas solo aparecen si el flag se habilita en el **worker**, y el API debe tenerlo habilitado también para devolverlas y reconocerlas.
4. En staging activar `MaerskMonitoring__Enabled=true` en API y worker. Validar la señal con datos ya existentes y sin generar tráfico artificial al proveedor. Revisar el primer/último avistamiento, deduplicación y que un reconocimiento no desbloquea circuitos.
5. Validar rutas y scopes, servicio sin proveedor MAERSK, fallos de PostgreSQL, recuperación de alerta y nuevo episodio; confirmar que otros providers continúan.
6. Habilitar en producción tras smoke test, revisar la primera muestra y ajustar umbrales de forma documentada.
7. Rollback mediante `MaerskMonitoring__Enabled=false` en todas las instancias. Conservar tabla, auditoría y perfiles. No borrar las alertas al desactivar.

**Limitación:** esta fase proporciona alertas operativas persistentes, logs estructurados y UI con polling. No afirma ofrecer notificaciones push/email/SMS, Prometheus exporter ni comprobación externa de disponibilidad; tales canales requieren integración configurada y verificación posterior.
