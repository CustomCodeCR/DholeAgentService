# Fase 2 — Maersk Circuit Breaker: implementación y activación protegida

Estado: implementación propuesta y CI; NO activar en producción antes de aceptación de staging, respaldo, esquema y reconciliación de incidentes.

## Backend
- El endpoint /maersk/operations comunica State=Disabled cuando FeatureEnabled=false. PersistedState muestra de forma independiente el último registro, UpdatedAtUtc la marca de tiempo y ConfigurationSource indica si el origen es explícito o por defecto.
- La detección del primer CAPTCHA, 403 o 429 se persiste como Open, requires_operator=true. Las actualizaciones se serializan por proveedor en transacción PostgreSQL; FailureObserved lleva execution_id e índice único parcial para evitar el doble conteo de una misma ejecución; el evento Opened se genera en la misma transacción.
- No hay reset por temporizador en incidentes que requieren operador.
- Migración aditiva 20261010050000_AddMaerskCircuitFailureDedup: añade execution_id nullable a circuit events e índice único. No elimina sesiones, eventos ni datos históricos.

## Runbook de activación
1. Desplegar en staging solo mediante el workflow protegido 'Deploy Dhole Agent - Staging' con backup fresco, restore drill y schema gate.
2. Verificar SHA exacto de las imágenes y la salud de API/Workers.
3. Preparar backup verificado y usar el workflow 'Activate Maersk Circuit - Postdeploy' con environment=staging, confirmation=ACTIVATE_MAERSK_CIRCUIT_STAGING, backup_dir y expected_release_sha iguales a los del deploy protegido.
4. El workflow vuelve a comprobar backups, restore drill, esquema, SHA de ambos contenedores y 0 trabajos Running. Si existen incidentes WaitingForAuthentication de CAPTCHA previos, NO activa: exige conciliar incidentes antes de permitir búsquedas. No reintenta proveedor.
5. En la activación verificada, persiste flags true en /opt/dhole/.env.staging y recrea exclusivamente dhole-agent-api y dhole-agent-workers. Mantiene otros contenedores, perfiles y DB intactos.
6. Verificar en staging el endpoint y test simulado sin visitar Maersk. Registrar el run ID, valores efectivos y capturas. NO declarar fase 2 cerrada hasta demostrar persistencia Open tras reinicios.
7. Para production, requiere run de activación staging exitoso y reciente (48h) y SHA del despliegue protegido master. El backlog histórico de CAPTCHA lo bloqueará mientras no se concilie.
8. En fallo posterior a escribir el env, deja el worker detenido fail-closed, conserva respaldo privado del env y restaura configuración anterior. Nunca reanuda trabajo prohibido silenciosamente.

## Despliegues posteriores
El preflight fase 8 conserva comprobación íntegra de backup y migraciones. Solo tolera flags ya activos mediante MAERSK_PHASE2_ACTIVE_REDEPLOY=CONFIRMED desde el pipeline protegido, con Circuit y Monitoring ambos true; AgentQueue__ConcurrentDispatcherEnabled permanece false en este rollout.

## Límites
La fase 2 NO cambia IP, perfiles, cookies, usuario ni entorno como reacción a CAPTCHA. No modifica las siete filas WaitingForAuthentication ni resuelve el error FAK, pendientes de fases 3 y 5. No invocar el workflow de activación sin evidencia de staging y autorización.
