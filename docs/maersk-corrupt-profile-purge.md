# Limpieza de perfil Chromium — corrupción local de Maersk

Este mantenimiento **elimina realmente** la carpeta Chromium activa de **una** credencial Maersk en el volumen del entorno seleccionado. No es un reinicio de contenedor ni una eliminación del volumen entero. Las claves, otros proveedores y PostgreSQL se mantienen.

## Aplicabilidad

Únicamente para corrupción local registrada como `maersk_browser_profile_corrupt` durante las últimas 24 horas. El comando se niega a continuar si el circuito del proveedor está abierto o existen señales recientes de CAPTCHA, HTTP 403, bloqueos edge o verificaciones de autenticación. **No está diseñado para esquivar controles antibot de Maersk**.

## Procedimiento

1. Detener nuevas programaciones, drenar toda ejecución Running y parar solamente `dhole-agent-workers` del entorno correspondiente.
2. Generar un respaldo completo y reciente con el workflow protegido `Prepare Maersk Phase 8 Backup`, incluyendo `agent.dump`, `browser-profiles.tar.gz`, `agent-keys.tar.gz`, `metadata.txt` y `SHA256SUMS`; validar restauración aislada.
3. Preparar el archivo env del entorno utilizando `DholeSystem/deploy/prepare-environments.sh`. No mostrarlo en logs.
4. Ejecutar desde el checkout de Agent:

```bash
bash scripts/maersk-corrupt-profile-maintenance.sh \
  production --check "<credential-id-uuid>" "<verified-backup-dir>" "<prepared-env>"
```

5. Solo si `--check` confirma corrupción estrictamente local, aplicar:

```bash
bash scripts/maersk-corrupt-profile-maintenance.sh \
  production --apply "<credential-id-uuid>" "<verified-backup-dir>" "<prepared-env>" \
  PURGE_CORRUPT_MAERSK_PROFILE_PRODUCTION
```

Cambiar `production` por `staging` y usar `PURGE_CORRUPT_MAERSK_PROFILE_STAGING` para staging. Cada credencial se procesa individualmente; no existe un borrado masivo automático.

Se exige Worker detenido, cero ejecuciones Running, evidencia de error técnico de la credencial y respaldo verificable de la base, perfiles y llaves del **mismo ambiente**. El script bloquea reparaciones si detecta una restricción impuesta por Maersk. Nunca elimina `.locks` ni perfiles de otros proveedores por separado.

## Después

La próxima ejecución necesita un login legítimo nuevo porque el perfil Chromium original ya no existe. Restauración: mantener el Worker detenido y recuperar del respaldo validado únicamente el directorio de esa credencial, con procedimientos controlados. No reabrir circuitos ni reencolar ejecuciones sin verificación real del proveedor. Esta operación no puede ejecutarse mientras el Worker esté encendido, y no se conecta al vigilante periódico de contenedores.

Ninguna ejecución en GitHub Actions implica que el perfil haya sido eliminado del servidor Argos.
