# Changelog

## [1.2.2] - 2026-11-05

- Admite prod3 como Worker activo con backups, rollback y verificación de exclusividad.
- Clonación resiliente de DholeSystem ante errores DNS transitorios.


## [1.2.1] - 2026-11-05

- Deploy Agent automático por push a master sin activación del Worker legacy.
- Verifica generación activa prod1/prod2, SHA exacto, backups, restauración y salud antes de cambiar procesos.
- Alinea metadatos nativos SemVer al release global Dhole 1.3.1.

## [1.2.0] - 2026-11-05

- Incorporates universal Dhole SemVer policy with GitHub Actions checks.
- Synchronizes service assembly metadata with the ecosystem version.
- Adds permanent Codex instructions and coordinated release requirements.
