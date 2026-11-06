# Changelog

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
