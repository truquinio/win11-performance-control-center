# AVISO DE COORDINACIÓN — Win11 Control / Limpieza C y D (2026-10-10)

**Destinatario:** agente del trabajo "Win11 Control desde cero", especialmente el worktree `Win11-v2-contracts`.

**ALERTA: NO HACER MERGES, CHERRY-PICKS, RESETS NI REEMPLAZOS AUTOMÁTICOS SIN CONCILIAR CAMBIOS.** Este archivo es un aviso, NO indica que el trabajo del otro agente esté terminado.

## Situación comprobada
- Repositorio canónico local: `D:\DOCS\AI\PROYECTOS IA\Win11-repair`.
- `main` ya contiene el commit de limpieza `b6c94b9` y la integración `c3d5203`. El avance directo de Git había ocurrido antes de recibir la instrucción de coordinación.
- Worktree de limpieza: `D:\DOCS\AI\PROYECTOS IA\Win11-storage-rebuild`, rama `feat/storage-cleanup-rebuild`, HEAD `c3d5203`.
- Worktree del otro desarrollo: `D:\DOCS\AI\PROYECTOS IA\Win11-v2-contracts`, rama `feat/win11-v2-contracts`, HEAD observado `2a4be7e`, **con numerosos cambios staged y unstaged**. No alterar ni descartar su estado.
- Las modificaciones del otro worktree **ya incluyen archivos de la limpieza staged**. Es señal de integración en marcha, pero no prueba de conciliación correcta. No volver a copiar todo el módulo ni crear clases/acciones paralelas.

## Contrato del módulo de limpieza ya implementado
- `disk.duplicates.scan`: lectura acotada, comparación de tamaño+SHA-256, cobertura parcial explícita; NO elimina duplicados.
- `disk.directories.watch`: capturas y crecimiento por carpeta, compara solamente mediciones completas.
- `disk.cleanup.safe`: previsualización.
- `disk.cleanup.execute`: limpieza de cachés permitidas hacia Papelera mediante API de Windows, confirmación y previsualización reciente **obligatorias en backend**. Manifiesto en `%LOCALAPPDATA%\Win11PerformanceControlCenter\State\recycle-manifest.jsonl`.
- Salvaguardas: proteger bot LinkedIn (SQLite/WAL), SIG, emuladores (QCOW2/AVD), APK, keystores, Claude Extensions, navegador/sesiones, Windows; no seguir junctions. En Temp solamente *.tmp/*.temp antiguos de 7 días como candidatos.
- Los bytes enviados a Papelera **no equivalen a espacio realmente liberado**. Restauración de la Papelera es manual (NO se ha implementado botón automático).
- No modificar procesos ni borrar archivos reales al verificar. No existe un escaneo exhaustivo de C: y D:, sino muestreo acotado.
- Documentación: `docs/STORAGE_REBUILD.md`.

## Verificación realizada en rama fusionada
- `scripts/verify.ps1` obtuvo `VERIFY_OK`, 247/247 pruebas .NET, 33/33 regresiones, CI/frontend/UI contracts y auditorías de dependencias aprobados.
- `scripts/build-local.ps1` obtuvo `BUILD_LOCAL=PASS`, ZIP SHA-256 `73A4210357CFFA531E85BC312393107F2E1E27DFC9215862D9B60BE6DF9714E9`.
- Paquete creado en `D:\DOCS\AI\PROYECTOS IA\Win11-storage-rebuild\artifacts\Win11PerformanceControlCenter-win-x64.zip`.
- No significa certificación del estado **actual del otro worktree** ni de hardware real, permisos, UAC, recuperación manual o integración final.

## Protocolo para evitar un Frankenstein
1. **Un único propietario por archivo mientras se integra**. El otro agente mantiene control de `Win11-v2-contracts`; este agente de limpieza NO seguirá editándolo ni fusionando cambios.
2. Antes de integrar, ejecutar `git status`, `git diff --cached`, `git diff` y comparar `main` (`c3d5203`) con `feat/win11-v2-contracts` (`2a4be7e`) para reconciliar código, catálogos C# generados/TS, DEMO y UI.
3. Preservar TODOS los cambios staged y unstaged del otro agente. No usar `git reset --hard`, `clean -fd` ni sobrescribir a ciegas.
4. No duplicar `StorageAnalysisService`, `StorageForensicsService`, `StorageDirectoryTrendService`, los action IDs ni crear una aplicación separada.
5. Verificar paridad de `ActionCatalog.cs` con `src/Frontend/generated/action-catalog.json`, `demo-catalog.ts`, `module-definitions.ts`, `providers/demo.ts` y `app.ts`. Diferencias aquí pueden dejar botones visibles sin comportamiento real.
6. Ejecutar pruebas focales de almacenamiento y **luego** el gate completo (incluida UI smoke). Un build que compila NO basta.
7. No sustituir ejecutables instalados, detener emuladores/bots ni subir/push/release Git sin autorización explícita del usuario.

**Petición para el otro chat:** reconocé este aviso en tu propia conversación, informa cualquier incompatibilidad y designá un único flujo de integración. El usuario exige una sola app consistente.
