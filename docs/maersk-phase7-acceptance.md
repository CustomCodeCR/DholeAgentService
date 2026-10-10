# Hermes / Maersk — Fase 7: pruebas unitarias y de integración

## Objetivo y límites

Validar de forma reproducible lo implementado en las fases previas (clasificación de fallos, perfiles persistentes, cola durable, circuit breaker, API/consola y alertas). **No se cambia la identidad del navegador, se evita ningún CAPTCHA ni se contacta a Maersk durante estas pruebas.** La fase 8 sigue siendo el despliegue controlado, activación y rollback; no se ejecuta aquí.

Las pruebas usan .NET 10/MSTest y PostgreSQL 16 local en GitHub Actions para el backend, y Node 24/Vue TypeScript para el frontend.

## Matriz de aceptación automatizada

| ID | Escenario | Resultado obligatorio | Prueba |
|---|---|---|---|
| A1 | CAPTCHA / verificación | Mantener ejecución original en `WaitingForAuthentication`, preservar correlación e input, no reclamar su lease y abrir circuito que requiere operador | `CaptchaPausesOriginalJob_WithoutStarvingOtherProvider` |
| A2 | Aislamiento | Mientras Maersk está bloqueado, un trabajo de otro proveedor sí consigue claim | `CaptchaPausesOriginalJob_WithoutStarvingOtherProvider` |
| A3 | Reset con lease activo | No cerrar un circuito abierto mientras exista un trabajo `Running` con lease vigente; requerir confirmación humana incluso cuando deja de estar `Running` | `VerifiedResetCannotClearCircuitWhileRunningLeaseIsActive` |
| A4 | Auditoría y estado | Con reset autorizado, registrar un evento y conservar `WaitingForAuthentication`; no reencolar ni cambiar perfiles | `VerifiedResetCannotClearCircuitWhileRunningLeaseIsActive` |
| A5 | Backoff técnico | No reclamar antes de `next_attempt_at_utc`, y al vencer solo un worker reclama; conservar execution ID, correlación, input y snapshot | `TechnicalRetryRespectsBackoff_AndPreservesOriginalSnapshotAcrossOwners` |
| A6 | Monitor apagado | No crear/actualizar/confirmar alertas cuando el flag está desactivado; el historial se conserva | `DisabledMonitoringCannotAcknowledgeOrMutateExistingIncident` |
| A7 | Clasificación | CAPTCHA/403/429 nunca autorizan reintento, rotación de identidad ni reconstrucción de perfil; fallos locales recuperables son distintos | `MaerskPhase7SafetyMatrixTests` |
| A8 | Configuración por defecto | Circuito, dispatcher concurrente y monitoreo permanecen desactivados para rollout controlado | `FlagsRemainOffByDefaultUntilStagingAcceptanceAndRollout` |
| A9 | Control de UI | Un operador sin permiso, monitor inactivo, alerta resuelta/reconocida o circuito cerrado no obtiene una acción administrativa habilitada | `tests/maerskPhase7Acceptance.test.ts` de DholeWeb |
| A10 | Contratos | La vista no expone cookies, contraseña, input/output crudos ni HTML de proveedor | `tests/maerskOperations.test.ts` y `tests/maerskPhase7Acceptance.test.ts` |

Las pruebas previas de fases 2–6 continúan ejecutándose en la suite general, incluidas las pruebas multi-worker y las migraciones. Este gate de aceptación también ejecuta los escenarios A1–A6 expresamente y **falla si se omiten o resultan inconclusos**; por eso necesita la variable `AGENT_QUEUE_POSTGRES_TEST_CONNECTION` y PostgreSQL disponible.

## Comandos reproducibles

```bash
# Postgres 16 de pruebas, con permisos para crear DB temporales:
export AGENT_QUEUE_POSTGRES_TEST_CONNECTION='Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres'

dotnet restore DholeAgentService.slnx
dotnet build DholeAgentService.slnx -c Release --no-restore
dotnet test DholeAgentService.slnx -c Release --no-build
dotnet test tests/Dhole.Agent.IntegrationTests/Dhole.Agent.IntegrationTests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~MaerskPhase7AcceptancePostgresTests' \
  --logger 'console;verbosity=normal'

# DholeWeb (en el repositorio separado)
pnpm install --frozen-lockfile
pnpm run test:agent
pnpm exec vue-tsc --build
pnpm run build-only
```

Los tests crean **bases de datos aisladas con nombres aleatorios y las eliminan al finalizar**. No configurar la variable de conexión contra una base productiva ni staging, y nunca ejecutar un test con credenciales reales del proveedor.

## Validaciones manuales pendientes para fase 8

1. Respaldar PostgreSQL, volúmenes Playwright y evidencias antes de modificar flags.
2. Verificar migraciones `20261009234500_AddDurableExecutionLeases`, `20261010020000_AddMaerskCircuitBreaker` y `20261010040000_AddMaerskHealthAlerts` en un entorno de staging.
3. Con autorización operativa y **sin provocar CAPTCHA**, comprobar que el endpoint de operación exige scopes de lectura y el reconocimiento de alertas requiere `agent.browser-profiles.authenticate`.
4. Ensayar aislamiento y reinicio de dos workers con perfiles de pruebas, evitando tareas reales duplicadas.
5. Confirmar que una sesión bloqueada nunca se reinicia ni conmuta de ambiente para evadir restricciones.
6. Validar que la consola traduce, se adapta a móvil y conserva los incidentes en vistas de solo lectura.
7. Sólo en la fase 8 realizar activaciones graduales documentadas y definir rollback operativo.

**La ejecución correcta de los tests offline no acredita acceso real a Maersk ni despliegues completados**.
