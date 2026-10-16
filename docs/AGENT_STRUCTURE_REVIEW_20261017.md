# Revisión de estructura de agentes — 2026-10-17

## Decisión
La estructura de la imagen sirve como catálogo de ideas, pero no conviene copiarla tal cual: está pensada para Claude Code y este repositorio ya usa Codex y tiene `AGENTS.md`, `CLAUDE.md`, skills locales, `.github/workflows/ci.yml` y controles de seguridad/QA.

## Inventario confirmado
- Instrucciones raíz: `AGENTS.md` (92 líneas) y `CLAUDE.md` (46 líneas, excluido de Git por `.gitignore`).
- Skills del repositorio: `.agents/skills/manual-testing/` y `.agents/skills/win11-lean-qa/`.
- Validación automatizada: `scripts/verify.ps1` y workflow de GitHub Actions.
- Seguridad, arquitectura, pruebas y aceptación: `docs/SECURITY.md`, `docs/ARCHITECTURE.md`, `docs/TESTING.md` y `docs/RELEASE_ACCEPTANCE_MATRIX_20261012.md`.
- No se encontraron `.claude/`, `agents/`, `hooks/`, `.mcp.json`, ni reglas separadas. La ausencia no es por sí sola una carencia: varias de esas funciones ya las cubren los mecanismos existentes.

## Recomendación por componente
- `AGENTS.md`: mantener como reglas comunes breves y estables. Ya contiene misión, seguridad, evidencia, cuotas, coordinación y continuidad; dividirlo ahora produciría fragmentación sin necesidad demostrada.
- `CLAUDE.md`: mantenerlo local/no versionado como adaptación para ese cliente; no crear `CLAUDE.local.md` duplicado.
- `rules/`: añadir solo cuando aparezca una regla realmente contextual (p. ej., un subárbol con riesgos o convenciones distintas). Codex puede leer `AGENTS.md` anidados; no convertir instrucciones en enlaces cuyo soporte no está comprobado.
- `skills/`: conservar las dos skills existentes. Crear una nueva únicamente para un flujo recurrente medido; no copiar una skill ajena ni instalar comandos imaginarios.
- `agents/`: no añadir perfiles de agentes. No hay una necesidad autorizada de subagentes ni contratos de propiedad que requieran otro runtime.
- `hooks/`: no añadir scripts de hooks por semejanza visual. El workflow ejecuta el gate canónico en PR; cualquier hook local sería opcional, fácil de omitir y requiere definir instalación/soporte.
- `.mcp.json` y `settings.local.json`: no versionar configuración de conectores, permisos, rutas locales ni posibles secretos. Desktop Commander ya se configura fuera del repositorio.
- `worktrees/`: no crear carpeta manual. Los worktrees son metadatos Git y ya se usan como worktrees reales.

## Resultado
No se crea configuración duplicada ni se mueve material existente. La mejora útil aquí es preservar límites claros: `AGENTS.md` como contrato común, `CLAUDE.md` local como adaptación del cliente, skills solo para rutinas reutilizables, `docs/` como especificación/evidencia, y GitHub Actions como gate compartido. Revaluar la estructura solo si crece una necesidad concreta (por ejemplo, instrucciones distintas por subárbol o una comprobación de seguridad que deba ser obligatoria en CI).

## Límites
Este documento es una evaluación estática de nombres y reglas existentes, no una prueba de compatibilidad de configuración en Claude Code ni una auditoría exhaustiva de instrucciones externas. No se cambió código ni configuración del proyecto.
