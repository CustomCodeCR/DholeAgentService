# Changelog

## [1.3.2] - 2026-11-08 (candidato F9, sin publicar)

- Sincroniza release global `rt-20261107-01` a `1.13.11` tras resolver los hallazgos de CI de Config, Reports y Gateway. Bump PATCH de DholeAgentService `1.3.1` a `1.3.2`, sin modificar REST, eventos, precios ni datos; flags realtime OFF y PR F9 aún DRAFT.


## [1.3.1] - 2026-11-08 (candidato F9, sin publicación)

- Sincroniza metadatos de versión global del batch Dhole Realtime `rt-20261107-01` a `1.13.10`; actualización PATCH independiente de DholeAgentService `1.3.0` → `1.3.1`. No cambia contratos REST, datos, lógica de negocio ni flags; requiere gates F0–F8 y CI de SHA final antes de merge.


## [1.3.0] - 2026-11-08 (candidato F6.12, NO LIBERADO)

- F6.12: eventos mínimos de cambios en ejecuciones, definiciones y programaciones del agente integrados en el mismo EF SaveChanges del Outbox existente.
- Evento independiente por recurso y stream dedicado; no transmite credenciales, datos privados, contenido de publicaciones, cuerpos ni URLs internas. REST autorizado es la única fuente de snapshots de datos.
- Requiere todos los flags de aprobación del release, apagados por defecto. Se añade CI de contrato y compilación; SemVer componente 1.2.2 → 1.3.0 y global candidato 1.13.0 sin despliegue.

## [1.2.2] - 2026-11-05

- Soporta despliegues controlados en la generación activa prod3, con backups, rollback y comprobaciones de exclusividad del Worker.
- Reintenta la clonación de infraestructura ante fallos DNS transitorios.

## [1.2.1] - 2026-11-05

- Automatiza el deploy master con selección del Worker activo prod1/prod2, snapshots y rollback.
- Retira despliegue legacy de producción tras staging; mantiene aislamiento de workers y preflight.
- Incrementa SemVer nativo y manifiesto global 1.3.1.

## [1.2.0] - 2026-11-05

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
