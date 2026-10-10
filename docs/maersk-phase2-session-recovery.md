# Hermes / Maersk — Fase 2: recuperación técnica de sesión

## Alcance
Esta fase reutiliza el perfil persistente de Chromium y admite una **única re-apertura del proceso local** si falla el lanzamiento de Chromium. No implementa el circuito de fase 4, la cola/concurrencia general de fase 3 ni una rotación de identidades.

Una restricción del proveedor (CAPTCHA, MFA, 403, 429, bloqueo edge) conserva el perfil; no se resetea y requiere verificación autorizada en el navegador original.

## Exclusión de perfil
- Un lock de archivo `<Browser:ProfilesPath>/.locks/MAERSK/<credentialId>.lock` garantiza exclusión sobre la misma carpeta física de perfil, incluso entre procesos que comparten volumen.
- La exclusión dura desde antes de revisar/reparar el perfil hasta que se dispone el contexto de Playwright. No depende de un TTL corto que expire durante el login interactivo.
- La adquisición espera como máximo `Browser:ProfileLockWaitSeconds` (predeterminado **5**, rango 1–30 segundos); si otra ejecución usa el perfil, se rechaza el acceso en lugar de abrir otro Chromium sobre el mismo directorio.
- El archivo de lock no contiene credenciales ni cookies y **no debe borrarse manualmente** para intentar desbloquear una sesión activa.
- Staging y producción deben mantener **volúmenes `Browser:ProfilesPath` separados**, de modo que cookies y archivos de sesión nunca crucen ambientes. Si se comparte accidentalmente el volumen, el mismo lock físico sigue impidiendo apertura simultánea.

## Decisiones
| Estado | Comportamiento |
|---|---|
| `Blocked` | Detener y conservar el perfil; verificación autorizada |
| `ResetRequested` + ejecución manual | Respaldar estado anterior y crear perfil nuevo **solo por solicitud de reparación técnica** previamente autorizada |
| `ResetRequested` + scheduled/API/gRPC | No reparar; solicitar ejecución manual |
| `Expired`, `LoginRequired`, `Error` | Mantener perfil original e intentar el login oficial existente |
| `Authenticated`, `Ready`, `Unknown` | Reutilizar perfil persistente existente |
| Error al abrir Chromium | Un segundo intento local, sin borrar estado |
| Fallo al crear lock / perfil ocupado | Error explícito y sin apertura concurrente |

## Reparación y rollback
1. Confirmar evidencia de corrupción local o expiración técnica, no un CAPTCHA/restricción del proveedor.
2. Autorización administrativa: utilizar el endpoint existente `POST /api/agents/browser-profiles/{id}/repair-session` (requiere `BrowserProfilesAuthenticate`). El comando solo admite `Expired` o `Error` y marca `ResetRequested`.
3. Ejecutar una acción manual autorizada; el proveedor obtiene el lock exclusivo antes de mover directorios. No elimina ningún `.stale-*`.
4. El directorio anterior se renombra a `<profile>.stale-<UTC>-<id>` y solo entonces se crea el directorio nuevo.
5. Si la recreación falla y el directorio objetivo no existe, se revierte el rename del backup original. Si no puede revertirse, no se borra el backup: revisar el almacenamiento y resolverlo manualmente.
6. Ante regresión, detener worker, verificar que no existe un contexto Chromium abierto, copiar **o restaurar bajo supervisión** el archivo `.stale-*` adecuado y validar la sesión. No hacer rollback con worker activo.

## Retención
`Browser:MaxRetainedProfileBackups` predeterminado **20** (rango 1–500) por perfil. No se realiza limpieza automática de respaldos con datos de sesión. Alcanzar el límite **bloquea nuevas reparaciones** y obliga a retención/backup seguro supervisado; no eliminar archivos por limpieza de Docker/volúmenes.

## Pruebas y despliegue
- Ejecutar workflow `Validate Dhole Agent` con `dotnet restore`, `dotnet build` y `dotnet test` en PR.
- Smoke test limitado: bloqueo duplicado del mismo perfil, diferentes perfiles independientes, reparación autorizada con preservación, `Blocked` sin reset.
- **Sin migraciones PostgreSQL.** No cambiar la ruta ni el volumen de perfiles durante despliegue.
- Rollback de binario: revertir el commit desplegado; no borrar `.locks`, `.stale-*` ni perfiles existentes.

La coordinación de `Queued`/reintentos de trabajos cuando `maersk_browser_profile_busy` ocurre pertenece a la fase 3 y debe tratarse como trabajo pendiente, no como un cambio de identidad.
