# Hermes / Maersk — Fase 8: entrega controlada, respaldo y rollback

## Objetivo

Desplegar los cambios ya validados de fases 1–7 sin perder ejecuciones, sesiones persistentes, llaves de protección ni resultados. Este runbook **no autoriza** a evitar CAPTCHA, MFA, 403 o 429 cambiando perfiles, IP, máquinas o ambientes.

**Estado inicial:** `MaerskCircuit:Enabled=false`, `MaerskMonitoring:Enabled=false`, `AgentQueue:ConcurrentDispatcherEnabled=false`. No modificar esos valores antes de migraciones, smoke test y aprobación registrada.

## Cambios al CD

- Los workflows de Agent staging y producción pasan a `workflow_dispatch` únicamente: los `push` a develop/master dejan de desplegar sin intervención.
- Se requiere `confirmation` y una ruta de respaldo válida (`backup_dir`) en el runner local.
- Producción exige además `staging_run_id`, verificado mediante GitHub Actions: workflow correcto, ejecución **manual** (`workflow_dispatch`) en `develop`, SHA idéntico al HEAD actual de `develop`, conclusión success y antigüedad menor a 48 horas. Un antiguo despliegue automático de fase 7 no satisface esta condición.
- `scripts/maersk-phase8-preflight.sh` exige copias íntegras, recientes (menos de 24 horas), del dump de PostgreSQL, del volumen de perfiles Playwright y de las llaves de protección; compara el ambiente y bloquea flags prematuramente habilitados.
- Se elimina el paso legacy de producción que cancelaba `Queued` y marcaba `Running` como `Failed`. Nunca efectuar limpieza destructiva de la cola como efecto de un deploy.
- Se desactiva `cancel-in-progress` para evitar cancelar un despliegue de producción a medio aplicar.

## Preparación asistida desde GitHub Actions (nuevo)

Para reducir operaciones manuales **sin saltarse los controles de seguridad**, existe
`Prepare Maersk Phase 8 Backup` (`.github/workflows/maersk-phase8-backup.yml`).
Este workflow se ejecuta **solo por `workflow_dispatch`** en el runner Dhole y
comparte el grupo de concurrencia con el deployment del mismo entorno.

1. Crear una carpeta durable en el host (ejemplo: `/opt/dhole/backups`),
   con permisos exclusivos para el usuario del runner y espacio suficiente.
   **No copiar secretos, sesiones o respaldos a GitHub.**
2. Detener nuevas ejecuciones, esperar que termine toda ejecución Running y
   detener manualmente **solo el worker del entorno elegido**. Si sigue
   ejecutándose, el nuevo workflow fallará antes de tocar perfiles o BD.
3. En la pestaña **Actions**, elegir `Prepare Maersk Phase 8 Backup`.
   Seleccionar la rama `develop` para staging o `master` para production.
   Indicar `environment=staging|production`, `backup_root` absoluta y la
   autorización exacta `BACKUP_AGENT_STAGING` o `BACKUP_AGENT_PRODUCTION`.
4. El workflow prepara el env file sin imprimir credenciales, crea dump y
   snapshots de ambos volúmenes, valida checksums/metadatos/flags e inicia
   un PostgreSQL 16 temporal sin red para demostrar que el dump restaura.
   Si todo pasa, copiar el campo **snapshot directory** del resumen de la
   ejecución; esa es la ruta que exige `backup_dir` al desplegar.
5. Ejecutar el deployment manual del ambiente elegido usando esa ruta.
   El deployment volverá a comprobar integridad, realizará el ensayo de
   restauración y validará salud de API/worker. Si no se va a desplegar,
   reanudar **explícitamente** el worker detenido por el operador tras
   verificar que no hay mantenimiento pendiente. El workflow de backup
   **nunca** lo inicia ni lo detiene automáticamente.

La comprobación de restore aísla PostgreSQL, **no es una prueba de que las
tarifas reales de Maersk hayan sido extraídas**. El respaldo y sus llaves
quedan únicamente en el host y deben gestionarse según retención segura.

## 1. Preparar respaldo (fuera del repositorio)

1. En el **host del ambiente exacto**, asegurar que el directorio durable de respaldos tenga espacio, permisos correctos y no esté dentro de checkout, `/tmp` ni volúmenes que el deploy pueda podar. No subir la copia a Actions/artifacts, Git ni servicios públicos.
2. Obtener ventana de mantenimiento, dejar terminar todos los trabajos `Running` y detener **únicamente** `dhole-agent-workers` desde el proyecto Compose correcto. No interrumpir trabajos vivos: el script se niega a respaldar mientras el worker esté ejecutándose o existan registros `Running`.
3. Preparar el env file del ambiente mediante el procedimiento existente de DholeSystem y establecer en la terminal `DHOLE_ENV_FILE` con su ruta. No imprimirlo ni pasarlo como artifact.
4. Ejecutar `bash scripts/maersk-phase8-backup.sh staging /RUTA/DURABLE` (o `production` según corresponda). El comando crea una carpeta nueva por timestamp UTC con `agent.dump`, `browser-profiles.tar.gz`, `agent-keys.tar.gz`, `metadata.txt` y `SHA256SUMS`.
5. Ejecutar `bash scripts/maersk-phase8-preflight.sh staging /RUTA/DURABLE/staging/TIMESTAMP "$DHOLE_ENV_FILE"` y conservar la ruta para el dispatch. Replicar por separado para producción con su propia ruta y credenciales, sin compartir perfiles ni backups entre ambientes.
6. Si el respaldo falla, conservar la carpeta parcial para diagnóstico (sin publicarla), no desplegar; restaurar el worker detenido mediante el Compose del ambiente.

**Importante:** pasar el preflight significa que los archivos son legibles, íntegros y recientes; no demuestra por sí solo que una restauración funciona. Ensayar restauración en una base temporal aislada, verificar integridad de los datos y perfiles, anotar resultado/tiempo y solo entonces aprobar el deploy.

## 2. Pruebas, migraciones y staging

1. Verificar que las suites `dotnet test`, PostgreSQL 16, la matriz de aceptación de fase 7, CI de Agent y CI de DholeWeb estén verdes en la rama por desplegar.
2. Registrar SHAs de develop/master, versión del esquema y etiquetas Docker anteriores. Comprobar respaldo y restauración de ensayo ANTES de migrar.
3. Validar migraciones idempotentes y compatibles: `20261009234500_AddDurableExecutionLeases`, `20261010020000_AddMaerskCircuitBreaker`, `20261010040000_AddMaerskHealthAlerts`. Revisar el estado real de EF migrations; no aplicar manualmente scripts especulativos ni migraciones a producción directamente.
   Tras iniciar la API y comprobar /health, el workflow comprueba en
   PostgreSQL `agent.execution_leases`, `agent.maersk_circuits`,
   `agent.maersk_circuit_events`, `agent.maersk_health_alerts` y
   `AgentExecutions.next_attempt_at_utc`. Esta verificación es de solo
   lectura, ocurre **antes de arrancar los workers nuevos** y detiene el
   despliegue si las tres migraciones todavía no están aplicadas.
4. En Actions, abrir **Deploy Dhole Agent - Staging** (`develop`) y usar `confirmation=DEPLOY_AGENT_STAGING`, `backup_dir=/ruta/exacta/al/snapshot`. Si el worker fue detenido durante el respaldo, el propio deploy lo levantará después de verificar la API.
5. Validar salud de API, inicio del queue pump de workers, conteos Queued/Running/WaitingForAuthentication, sesiones sin cambio, acceso de operador y permisos. Simular un bloqueo con datos de prueba; no provocar CAPTCHA real.
6. Confirmar que un trabajo de otro proveedor completa aun cuando Maersk esté en espera, que no hay reclamos duplicados y que un reintento técnico preserva `executionId`, snapshot y correlación.
7. Solo después de migración y smoke satisfactorios, habilitar por separado y de forma controlada en staging: primero `MaerskMonitoring__Enabled=true` en API/worker, después `MaerskCircuit__Enabled=true` y finalmente `AgentQueue__ConcurrentDispatcherEnabled=true`. Para cada flag registrar hora, métricas, evidencia y posible rollback. La preflight de deploy estándar exige flags apagados; la activación del piloto se hace como operación separada con control de cambios.

## 3. Promoción a master y producción

1. Promover por PR los commits **ya probados** de la fase y confirmar que ambos historiales divergentes preservan el mismo cambio funcional. Nunca force-push.
2. Realizar backup fresco y un ensayo de restauración del entorno **producción**, con workers detenidos sin trabajo Running. No tomar ni restaurar perfiles desde staging.
3. En Actions, usar **Deploy Dhole Agent - Production** (`master`) con `confirmation=DEPLOY_AGENT_PRODUCTION`, `backup_dir` de producción y el `staging_run_id` reciente y exitoso. El workflow está configurado para fallar si no recibe estas evidencias.
4. Desplegar primero API, verificar `/health`; luego workers y `AGENT_QUEUE_PUMP_STARTED`. Comparar conteos pre/post y tasa de errores. No limpiar ni reiniciar sesiones bloqueadas.
5. Verificar los endpoints de operación y permisos antes de habilitar cualquier flag productivo; activar uno por vez, midiendo lease/heartbeat, cola y alertas. Nunca cerrar automáticamente un circuito que requiere operador.

## Comprobaciones de restauración y rollback automatizado

Los workflows de staging y producción ejecutan, antes de reconstruir imágenes,
`scripts/maersk-phase8-restore-drill.sh`: levantan un PostgreSQL 16 desechable
**sin red**, restauran el `agent.dump` completo y verifican la presencia de tablas
`agent`. El contenedor y sus volúmenes anónimos se eliminan al terminar. Nunca se
restaura sobre la base en uso. Este ensayo puede requerir espacio adicional en el
runner; si falta espacio o una migración rompe la restauración, el despliegue se
detiene sin tocar API ni workers.

Antes de construir, `scripts/maersk-phase8-image-rollback.sh capture`
protege los IDs de las imágenes existentes usando tags
`phase8-prev-<ambiente>-<GITHUB_RUN_ID>`; no depende de que sigan presentes
los tags `latest`/`staging`. El paso `mark-deploy` marca el comienzo real
del cambio. Si falla un paso posterior, el workflow ejecuta `restore`,
recupera ambos tags originales y recrea API y workers sin eliminar volúmenes,
bases, sesiones, colas o filas de auditoría. Si falla la propia recuperación,
investigar el log y **no continuar el rollout**; la restauración no revierte
migraciones SQL.

En producción se prohíbe `docker image prune -af` dentro de la fase 8:
borraría imágenes antiguas etiquetadas de las que depende el rollback. Solo
se permite limpiar imágenes sin etiqueta y cache de build. Antes de retirar
manualmente tags `phase8-prev-*` comprobar política de retención y al menos
una versión estable recuperable.

## 4. Rollback sin pérdida de datos

1. Si aumenta la cola, aparecen reclamos duplicados o falla el health check, detener la activación. Establecer **false** en los tres flags en API y workers; reiniciar de forma coordinada solo después de conservar estado y permitir terminar trabajos vivos.
2. Volver a las imágenes Docker previas **por etiqueta SHA registrada y verificada** (`dhole/agent-api:<SHA_PREVIO>` y `dhole/agent-workers:<SHA_PREVIO>`), actualizando las tags usadas por Compose antes de recrear servicios. No ejecutar `docker volume prune`, no borrar perfiles `.stale-*`, llaves ni auditoría.
3. No revertir destructivamente una migración: mantener esquema compatible o preparar rollback de esquema específico probado en restauración aislada. Restaurar PostgreSQL desde dump únicamente mediante procedimiento controlado con confirmación de ventana, sabiendo que sobrescribir un DB posterior al respaldo perdería cambios más recientes.
4. Confirmar recuperación de health, `Queued`, `Running`, `WaitingForAuthentication`, los snapshots y la ausencia de nuevos errores. Registrar incidentes y estado final.

## 5. Evidencias obligatorias de cierre

- SHAs de ambos repos y ramas, PRs y checks de CI verde.
- Inventario de volúmenes/DB, ubicación segura de backups, checksums y prueba real de restauración.
- Resultado de migraciones y smoke test de staging; número del workflow exitoso y SHA exacto de develop.
- Resultado de health, queue pump, permisos y métricas antes/después en producción.
- Valores efectivos de las tres flags por ambiente, versiones de imagen y rollback ensayado.
- Confirmación explícita de que no hubo rotación de sesiones, bypass del proveedor ni eliminación de datos.

**No considerar completada la fase 8 solo por fusionar estos scripts.** La aceptación operativa necesita ejecutar el backup, restauración de ensayo, deploy y smoke en infraestructura real. Los archivos del repositorio no prueban por sí solos esos hechos.
