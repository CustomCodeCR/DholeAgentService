# Hermes / Maersk — Fase 5: consola operativa y APIs seguras

## Alcance
La fase 5 expone información útil de la cola y del circuito de fases 3/4 en DholeWeb. No implementa rotación de identidad, solución automática de CAPTCHA, nuevas políticas de circuit breaker, ni instrumentación/alertas de la fase 6.

### API
- `GET /api/agents/maersk/operations` (requiere sesión autenticada y **ambos** scopes `agent.executions.view` y `agent.browser-profiles.view`).
- Devuelve la información de `MAERSK`: estado del circuito, `featureEnabled`, razón/cooldown, contadores por estado de ejecución, últimas 40 ejecuciones, perfiles sin rutas de almacenamiento, y últimos 30 incidentes del circuito cuando está habilitado.
- **No devuelve** cookies, credenciales, contraseñas, HTML de CAPTCHA, prompts, payloads de entrada/salida, rutas de perfiles, ni mensajes de error de proveedor que pudieran contener datos sensibles.
- El endpoint preexistente de fase 4 `POST /api/agents/maersk-circuit/{providerId}/reset` permanece protegido por `agent.browser-profiles.authenticate`. Requiere `reason` de 12–1000 caracteres, `verifiedWithProvider=true`, usuario autenticado, proveedor MAERSK y ausencia de ejecuciones activas incompatibles. La auditoría identifica al actor.
- No se agrega una API para borrar perfiles o forzar eludir una restricción. La reparación técnica legítima continúa por el flujo ya existente `/api/agents/browser-profiles/{id}/repair-session`.

### DholeWeb
- Ruta `/agents/maersk`: estados Closed/Open/HalfOpen, cola (Queued/Running/WaitingForAuthentication/Completed/Failed), perfiles, incidentes, ejecuciones recientes y acceso a sus pantallas de detalle.
- Navegación desde Monitorización y Configuración avanzada de Agents.
- Respuesta móvil mediante tarjetas y tabla con desplazamiento horizontal, strings i18n español/inglés.
- Actualización cada 15 segundos mientras la pestaña está visible; temporizador liberado al desmontar.
- La acción de reset abre un modal que exige declaración explícita y razón; la comprobación se hace tanto en UI como en API.
- Aviso visible cuando `MaerskCircuit:Enabled=false`. **No representar Closed como garantía de disponibilidad** si el feature está apagado.

### Verificación y rollout
1. Verificar que la fase 4 se ha desplegado y que la migración `20261010020000_AddMaerskCircuitBreaker` se aplicó en cada ambiente.
2. Integrar y desplegar **API antes que DholeWeb**. El endpoint de lectura no activa ni desactiva la fase 4.
3. Validar `GET /api/agents/maersk/operations` con JWT y scopes correctos; 403 sin ambos permisos, 404 si MAERSK no existe, redacción de campos. Cuando la fase 4 está desactivada, historial vacío y `featureEnabled=false`.
4. Desplegar la web y comprobar navegación, polling, móvil y permisos en staging.
5. Probar reset únicamente con una verificación real y autorizada del proveedor. Confirmar que un usuario sin `agent.browser-profiles.authenticate` recibe 403. No provocar CAPTCHA intencionalmente.
6. Desplegar en producción de forma coordinada; verificar que el proxy/API gateway lleva `/api/agents/maersk/*` al servicio Agent.
7. Rollback: revertir la vista de la web o la API sin borrar `agent.maersk_circuit_events`, el estado persistente, trabajos en cola ni perfiles.

No se necesita migración nueva en esta fase: la tabla de auditoría ya forma parte de la migración de fase 4. El mapeo EF es exclusivamente para consulta.
