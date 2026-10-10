# Hermes / Maersk — Fase 1: diagnóstico y activación controlada

Fecha: 2026-10-10. Alcance: solo fase 1. No activar circuit breaker, monitor ni despachador concurrente, ni tocar sesiones o trabajos.

## Hallazgos confirmados en GitHub (no sustituyen evidencia del servidor)
- DholeAgentService master: 5b07541420f2a08a4dff8eb92c7335bfaf41dda1; develop: 5c20ce64989c37a24fde9671cf29633b9668d105. Historiales divergentes; no fusionar ramas completas entre sí.
- Workers/appsettings.json define MaerskCircuit.Enabled=false, MaerskMonitoring.Enabled=false y AgentQueue.ConcurrentDispatcherEnabled=false; MaxConcurrentMaersk=1. Variables de entorno prevalecen.
- PostgresMaerskCircuitBreaker retorna Closed sintético cuando está apagado; permite entrada y omite RecordFailure. Closed no demuestra acceso al proveedor.
- El preflight de fase 8 exige backups válidos y los tres flags apagados para despliegue protegido; NO suprimir el gate.
- El schema gate comprueba leases, circuitos, eventos, alertas y columna next_attempt_at_utc. Su presencia en código no implica migración aplicada.
- DholeSystem inyecta AGENT_POSTGRES_CONNECTION_STRING en API y Workers y carga DHOLE_ENV_FILE. Staging usa proyecto/red dhole-staging; producción dhole. Verificación operativa pendiente.
- La incidencia informa siete WaitingForAuthentication con maersk_hcaptcha_required. Estado real en PostgreSQL todavía no confirmado.

## Pendientes obligatorios antes de cerrar
- ID de imagen, revisión y flags efectivos de API/Workers en staging y producción.
- Confirmar archivo de configuración correcto y DB común para API/Workers por entorno, con redes y volúmenes aislados entre entornos.
- Migraciones aplicadas; de faltar, exigir respaldo antes de aplicación (fuera de fase 1).
- BrowserProfiles de Maersk y su estado Blocked; snapshot del circuito y de las siete ejecuciones.
- Run de despliegue protegido, SHA desplegado y backup validado, no solo CI de PR.
- Snapshot de estados y contadores antes de cambios.

## Comprobación read-only
En el host autorizado, con env preparado:
- bash scripts/inspect-maersk-phase1.sh staging /ruta/protegida/.env.staging
- bash scripts/inspect-maersk-phase1.sh production /ruta/protegida/.env.production

El script solo imprime una lista permitida de flags, image ID, revision si existe, coherencia de conexión sin revelar credenciales y datos mínimos de circuito, perfiles y siete incidentes. No publica cookies, storageState, inputs, DOM ni secretos. El acceso SQL utiliza default_transaction_read_only y schema-check solo SELECT. UNKNOWN/MISMATCH/MISSING_OR_UNVERIFIED o exit != 0 significa NO ACTIVAR. Si una variable de entorno no está, NO deducir valor efectivo de una imagen que no se ha inspeccionado.

Revisar con un operador autorizado docker compose config -q sin emitir compose completo; comparar nombres/redes/volúmenes y destinos PostgreSQL de staging y producción. Verificar backups y ejercicios de restauración con el flujo de fase 8. El script no modifica estado de aplicación.

## Plan de activación diferida
1. Staging: capturar inventario, confirmar versiones reales, respaldos/restauración de DB, perfiles y keyring y gates de esquema.
2. Fase 2 (NO ejecutar ahora): crear activación explícita posdespliegue, sin editar preflight a ciegas. Establecer MaerskCircuit__Enabled=true y MaerskMonitoring__Enabled=true en origen real, recrear API/Workers sin borrar volúmenes, comprobar valores de ambos; despachador concurrente permanece desactivado hasta pruebas de cola.
3. Validar Open con inyección de CAPTCHA simulado sin visitar Maersk; comprobar reinicios y otros proveedores.
4. Producción solo con staging aceptado: backups frescos, restore drill, desplegar misma versión revisada, repetir activación y verificación. Reconciliación y reanudación se harán únicamente en sus fases posteriores.
5. Rollback conserva incidentes y bloqueo operativo equivalente; no rotar identidad/IP/entorno para eludir verificaciones.

## Estado
Diagnóstico estático confirmado; evidencia de runtime NO VERIFICADA. Fase 1 permanece BLOQUEADA hasta ejecutar las comprobaciones en ambos hosts. No avanzar a fase 2 solamente porque se fusionen cambios.
