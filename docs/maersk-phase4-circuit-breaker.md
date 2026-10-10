# Hermes / Maersk — Fase 4: circuit breaker persistente

## Alcance
El circuito detiene nuevas ejecuciones cuando el proveedor presenta CAPTCHA, MFA/verificación, HTTP 403, HTTP 429 u otros fallos de autenticación que requieren intervención. Evita tormentas de solicitudes sin cambiar IP, cookies, máquina, ambiente o perfil. Las fases 5–8 (interfaz, alertas avanzadas y operaciones adicionales) quedan fuera de alcance.

### Estados

| Estado | Comportamiento |
|---|---|
| `Closed` | Se admiten solicitudes Maersk normalmente |
| `Open` por CAPTCHA/MFA/401/403/429 | **No se reanuda por cooldown.** Se conserva el perfil y el trabajo en cola; requiere verificación con el proveedor y liberación por operador autenticado |
| `Open` por fallo técnico del proveedor repetido | Tres errores `maersk_offer_timeout`, `maersk_provider_service_unavailable` o `maersk_authentication_service_error` consecutivos; cooldown 15 minutos |
| `HalfOpen` | Al vencer el cooldown técnico, PostgreSQL permite **solo un probe** atómico, independientemente del número de workers |
| `HalfOpen` fallido | Vuelve a `Open` con cooldown técnico; sin regenerar sesiones |
| `HalfOpen` exitoso | Pasa a `Closed`; reinicia contador |
| `HalfOpen` interrumpido | Queda cerrado a nuevas pruebas hasta revisión manual; el operador puede intervenir luego de verificar que no hay ejecución activa |

Un `Open` por CAPTCHA o una restricción del proveedor **permanece abierto aunque `open_until_utc` haya pasado**. Una ejecución exitosa anterior o ajena al probe no puede cerrar un `Open` con `requires_operator=true`.

### Persistencia y auditoría
Migración `20261010020000_AddMaerskCircuitBreaker`:
- `agent.maersk_circuits`: una fila por `provider_id`, con estado, motivo estructurado, contador, cooldown, bloqueo por operador e identificador de probe.
- `agent.maersk_circuit_events`: apertura, probe completado, reset administrativo (actor, motivo y fecha). No almacenar tokens, cookies, contraseñas, HTML del CAPTCHA ni mensajes libres del proveedor.

El estado es **global por proveedor dentro de la misma base PostgreSQL**. Staging y producción deben usar sus propias bases y perfiles, sin intercambiar identidades para esquivar restricciones.

### API de operación
Estos endpoints usan autorización existente:
- `GET /api/agents/maersk-circuit/{providerId}`, scope `BrowserProfilesView`.
- `POST /api/agents/maersk-circuit/{providerId}/reset`, scope `BrowserProfilesAuthenticate`, con body:
```json
{
  "reason": "Verificación del proveedor completada en el perfil original",
  "verifiedWithProvider": true
}
```
Solo un usuario autenticado puede resetear, y queda un evento auditado. La confirmación explícita implica que el operador realmente verificó el acceso legítimo en el navegador original, por ejemplo a través de noVNC. El API no resuelve CAPTCHA ni intenta evitarlo; la confirmación del operador no se puede sustituir por cron o cooldown.

Si el `BrowserProfile` aún está `Blocked`, también se debe corregir legítimamente ese estado mediante el flujo normal de autenticación antes de ejecutar búsquedas. Resetear el circuito **no** borra ni modifica el perfil. No reanudar sin una verificación real.

### Despliegue controlado
1. Confirmar que los despliegues de fases 2–3 están sanos. Hacer backup de PostgreSQL.
2. Aplicar migración en staging y comprobar tablas, columnas y permisos. CI ejecuta tests reales de PostgreSQL 16.
3. Instalar código con `MaerskCircuit:Enabled=false`; no activar antes de que todos los workers y APIs tengan la nueva migración.
4. Habilitar **con el mismo valor** `MaerskCircuit__Enabled=true` en worker y API de staging; confirmar que `Open` detiene tanto despachadores como cron; hacer pruebas de dos workers y timeout.
5. Verificar manualmente restablecimiento con scope correcto, motivo auditado y perfil original. Validar que otros proveedores continúan.
6. Realizar habilitación coordinada en producción después del smoke test. Confirmar que `GET` devuelve el estado esperado.
7. Para rollback, deshabilitar el flag en todos los procesos y conservar tablas, registros y evidencias. **No borrar incidentes ni profiles**; antes de desactivar el circuito durante una restricción real de Maersk, suspender los schedules relacionados para no repetir solicitudes automáticamente.

### Valores por defecto
```json
{
  "MaerskCircuit": {
    "Enabled": false,
    "TransientFailureThreshold": 3,
    "TechnicalCooldownSeconds": 900,
    "ProviderCooldownSeconds": 1800
  }
}
```

La cola concurrente de fase 3 mantiene su propio flag. Su activación no es un requisito para registrar incidentes una vez el circuito esté habilitado, pero requiere rollout separado y verificado.
