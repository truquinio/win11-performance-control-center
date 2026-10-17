# Coordinación Win11 Control — escaneo de espacio y cobertura parcial (11/10/2026)

**No sobrescribir ni fusionar automáticamente los cambios concurrentes de main.** Este aviso añade evidencia verificable sin alterar el código productivo ni el staging de otros agentes.

- Repositorio canónico: `D:\DOCS\AI\PROYECTOS IA\Win11-repair`, HEAD observado `f0277c3` con cambios staged/unstaged de recuperación, frontend y UIA. Preservar íntegramente esos cambios y el resto de avisos.
- Rama aislada: `fix/storage-watch-completeness`; worktree `D:\DOCS\AI\PROYECTOS IA\Win11-storage-watch-safety`; commit **`b9320a9`**.
- Archivos de producto modificados exclusivamente en la rama: `src/App/Services/StorageWatchService.cs` y `tests/App.Tests/StorageWatchTests.cs`; documento nuevo `docs/STORAGE_WATCH_COMPLETENESS_QA_20261011.md`. En main ambos archivos siguen limpios (sin solapamiento observado).
- **Defecto reproducido**: un escaneo `disk.hotspots.scan` que limita a 16 carpetas podía afirmar cobertura completa con 17 o más. Test rojo 0/1 (archivo `artifacts/storage-watch-cap-red.log`). Arreglo con un único directorio extra de lookahead: al detectar carpeta omitida marca `Complete=false` y `Partial=true` sin ampliar el límite de carpetas medidas.
- Prueba positiva del límite exacto (16) y regresiones: focal 6/6, suite .NET **250/250**, 33 incident evals; **`VERIFY_OK`** en `Win11-storage-watch-safety\artifacts\verify-storage-watch-safety.log`, cero vulnerabilidades detectadas por npm/NuGet en las fuentes consultadas.
- No se escaneó el almacenamiento del usuario ni se ejecutó ningún `WRITE` Windows real. Pruebas en carpetas temporales sintéticas.
- **Integración pendiente, NO aplicada a main**: examinar `git status`, `git diff --cached`, el aviso de storage anterior y el HEAD actual, conciliar con cambios en curso, integrar manualmente este commit y repetir gate integral del estado combinado. No usar `reset --hard`, `clean`, reemplazos de carpetas, ni publicar prelanzamientos.

Bloqueos finales permanecen: VM Windows aislada para WRITE/UAC/rollback, mockup Developer aprobado y pruebas físicas DPI/NVDA.
