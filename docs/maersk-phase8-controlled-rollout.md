# Hermes / Maersk — Fase 8: entrega controlada, respaldo y rollback

## Objetivo

Desplegar los cambios ya validados de fases 1–7 sin perder ejecuciones, sesiones persistentes, llaves de protección ni resultados. Este runbook **no autoriza** a evitar CAPTCHA, MFA, 403 o 429 cambiando perfiles, IP, máquinas o ambientes.

**Estado inicial:** `MaerskCircuit:Enabled=false`, `MaerskMonitoring:Enabled=false`, `AgentQueue:ConcurrentDispatcherEnabled=false`. No modificar esos valores antes de migraciones, smoke test y aprobación registrada.

## Cambios al CD

- Los workflows de Agent staging y producción pasan a `workflow_dispatch` únicamente: los `push` a develop/master dejan de desplegar sin intervención.
- Se requiere `confirmation` y una ruta de respaldo válida (`backup_dir`) en el runner local.
- Producción exige además `staging_run_id`, verificado mediante GitHub Actions: workflow correcto, rama develop, conclusión success y antigüedad menor a 48 horas.
- `scripts/maersk-phase8-preflight.sh` exige copias íntegras, recientes (menos de 24 horas), del dump de PostgreSQL, del volumen de perfiles Playwright y de las llaves de protección; compara el ambiente y bloquea flags prematuramente habilitados.
- Se elimina el paso legacy de producción que cancelaba `Queued` y marcaba `Running` como `Failed`. Nunca efectuar limpieza destructiva de la cola como efecto de un deploy.
- Se desactiva `cancel-in-progress` para evitar cancelar un despliegue de producción a medio aplicar.

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

## 4. Rollback sin pérdida de datos

1. Si aumenta la cola, aparecen reclamos duplicados o falla el health check, detener la activación. Establecer **false** en los tres flags en API y workers; reiniciar de forma coordinada solo después de conservar estado y permitir terminar trabajos vivos.
2. Volver a las imágenes Docker previas **por etiqueta SHA registrada y verificada** (`dhole/agent-api:<SHA_PREVIO>` y `dhole/agent-workers:<SHA_PREVIO>`), actualizando las tags usadas por Compose antes de recrear servicios. No ejecutar `docker volume prune`, no borrar perfiles `.stale-*`, llaves ni auditoría.
3. No revertir destructivamente una migración: mantener esquema compatible o preparar rollback de esquema específico probado en restauración aislada. Restaurar PostgreSQL desde dump únicamente mediante procedimiento controlado con confirmación de ventana, sabiendo que sobrescribir un DB posterior al respaldo perdería cambios más recientes.
4. Confirmar recuperación de health, `Queued`, `Running`, `WaitingForAuthentication`, los snapshots y la ausencia de nuevos errores. Registrar incidentes y estado final.

## 5. Evidencias obligatorias de cierre

- SHAs de ambos repos y ramas, PRs y checks de CI verde.
- Inventario de volúmenes/DB, ubicación segura de backups, checksums y prueba real de restauración.
- Resultado de migraciones y smoke test de staging; número del workflow exitoso.
- Resultado de health, queue pump, permisos y métricas antes/después en producción.
- Valores efectivos de las tres flags por ambiente, versiones de imagen y rollback ensayado.
- Confirmación explícita de que no hubo rotación de sesiones, bypass del proveedor ni eliminación de datos.

**No considerar completada la fase 8 solo por fusionar estos scripts.** La aceptación operativa necesita ejecutar el backup, restauración de ensayo, deploy y smoke en infraestructura real. Los archivos del repositorio no prueban por sí solos esos hechos.
