# Win11 Performance Control Center — auditoría verificable de capas (15/10/2026)

## Alcance y limitación
Auditoría no destructiva sobre Ephpatha por Remote Desktop Commander (el MCP local no está expuesto en esta sesión). Contrastados AGENTS.md, docs/HANDOFF.md, BLUEPRINT_GAP_ANALYSIS_20261011.md, QA_2026-10-11.md, INCIDENT_INTEGRATION_SMOKE_20261007.md, RELEASE_ACCEPTANCE_MATRIX_20261012.md, ECO_QA35.md y COORDINATION_RECONCILIATION_20261013.md. Se revisaron los scripts del gate de publicación y se ejecutaron contratos focales. **Esto NO es una auditoría exhaustiva de cada línea ni certificación física.**

## Git e integridad
- Ruta: `D:\DOCS\AI\PROYECTOS IA\Win11-repair`.
- HEAD observado: `f0277c364e791731378d2f55127c090beaf21cdb`; `main` ahead 33 de origin/main.
- 96 entradas en `git status --porcelain` al inicio; archivos staged, unstaged y untracked coexistiendo.
- 7 worktrees: principal, coordination-20261013, outcome-failclosed, pagefile-feedback, storage-rebuild, storage-watch-safety, v2-contracts.
- No se hizo git add/reset/stash/clean/commit/merge/push; no se alteraron aplicaciones ni configuraciones de Windows.
- `git diff --check` y `git diff --cached --check` acabaron en el lote global con salida 0. Advertencias de futuros cambios CRLF/LF, no fallos de whitespace.
- Bloqueo de integración: `COORDINATION_RECONCILIATION_20261013.md` sigue solicitando confirmación de propietarios de main, Outcome y Pagefile. No reconciliar por copia o merge ciego.

## Matriz de capas

| Capa | Evidencia actual | Pendiente real |
| --- | --- | --- |
| Catálogo y contratos | `CATALOG_CONTRACT_OK actions=89 parameterDefs=49`; `ACTION_CERTIFICATION_OK total=89 read=51 dry_run=3 write=35` | 35 WRITE necesitan certificados físicos; contratos no equivalen a resultados |
| Frontend/UX | `FRONTEND_CONTRACT_OK views=24 developerViews=19 actionButtons=11 literalButtons=42`; frontend modularizado en componentes | Mockup Developer aprobado, pruebas visuales físicas, DPI 100/150/200, NVDA, alto contraste, navegación completa |
| DEMO | `DEMO_FAIL_CLOSED_OK` | No extrapolar a backend real |
| Windows host / IPC / permisos | `PLATFORM_CONTRACT_OK PerMonitorV2 UAC current-user IPC multi-session WMI-policy` | Validar UAC permitido/denegado y aislamiento en VM desechable |
| Seguridad WebView2 | `WEBVIEW_SECURITY_CONTRACT_OK permissions and downloads fail closed` | Pruebas de interacción y amenazas dinámicas adicionales |
| Recursos WMI | `RESOURCE_CONTRACT_OK WMI collections are explicitly disposed` | Medición de fuga/RAM/CPU/disco bajo carga y perfiles reproducibles |
| Almacenamiento/AppData | Incidente IntegrationSmoke del 07/10 identificado (bloqueo de metadatos en escaneo recursivo); documentación acredita fixtures, presupuestos y regresiones posteriores | E/S lenta y cancelación que no garantice interrupción de API Windows; no volver a caminar AppData real en integración |
| CPU, RAM, energía, USB | `ECO_QA35_CONTRACT_OK catalogWrite=35 rejectionCases=140 positiveGroups=16 positiveWriteIds=35`; cobertura sintética del catálogo | No implica 35 cambios reales de Windows ni garantías de rollback físico |
| Outcomes/Recovery | Docs describen auditoría fail-closed, journaling, rollback y aislamiento en pruebas | Ensayar fallos intermedios, reboot/restart, permisos, postcondiciones y rollback por acción en VM |
| CI | `CI_CONTRACT_OK workflow uses canonical verification gate + real-world evals` | Ejecutar un único verify completo al integrar revisión congelada; validar artefacto exacto |
| Publicación | `RELEASE_READINESS_CONTRACT_OK traversal and junction blocked; no fake WRITE certification` | `artifacts/release-certification.json` con evidencia física, signoff y hash de release aún no certificado |
| Compatibilidad PowerShell 5/7 | `SHA256_COMPAT_OK` en powershell.exe y pwsh.exe; `PS5_COMPAT_NEGATIVE_OK` en ambos; proceso total exit 0 | No confundir regresión arreglada con autorización de release |

## Pruebas ejecutadas en esta pasada
- `node scripts/catalog-contract.mjs` → `CATALOG_CONTRACT_OK`.
- `node scripts/frontend-contract.mjs` → `FRONTEND_CONTRACT_OK`.
- `node scripts/eco-qa35-contract.mjs` → `ECO_QA35_CONTRACT_OK`, positivo sintético de 35 IDs, no físico.
- `node scripts/release-readiness-contract.mjs` → dos mensajes verdes, incluidos traversal y junction.
- `node scripts/sha256-compat-contract.mjs` → PowerShell 5.1 y 7 OK; hash y ausencia correctos.
- `node scripts/release-readiness-ps5-compat-contract.mjs` → rechazo controlado del manifiesto incompleto en PowerShell 5.1 y 7; proceso global exit 0.
- `node scripts/action-certification.mjs` → 89 acciones catalogadas.
- `node scripts/demo-fail-closed-contract.mjs` → OK.
- `node scripts/webview-security-contract.mjs` → OK.
- `node scripts/platform-contract.mjs` → OK.
- `node scripts/resource-contract.mjs` → OK.
- `node scripts/ci-contract.mjs` → OK.
- `git diff --check` / `git diff --cached --check` → sin errores, advertencias EOL.
- Un primer comando de tests no llegó a ejecutar por interpolación de variables en una shell anidada; se corrigió la invocación, no se atribuyó PASS a ese intento.

## Hallazgos técnicos
1. El resumen anterior de 87 acciones y 231/288 tests quedó obsoleto. La fuente real actual tiene 89 acciones. Documentos posteriores registran 464/464 tests (.NET) en 12/10; **no se volvió a ejecutar esa suite**.
2. `app.ts` ya se modularizó; no reconstruir el frontend ni crear un segundo catálogo.
3. El HANDOFF del 14/10 describía el gate PowerShell 5.1 en RED, pero el código actual incluye `sha256-compat.ps1`, llama `Get-WpccSha256` y el contrato lo reconoce. La regresión negativa PS5/PS7 pasó hoy.
4. La carencia crítica no es un fallo demostrado de los 35 WRITE sintéticos; es su **falta de certificación física** (UAC/denegación/interrupción/postcheck/reversión) en VM con snapshots.
5. Sin coordinación de los 96 cambios concurrentes, editar archivos compartidos o fusionar ramas puede causar pérdida de contribuciones.

## Orden de ejecución recomendado
P0. Solicitar a responsables de main/Outcome/Pagefile confirmación de propiedad y ventana de congelación; reconciliar a nivel de hunk en worktree aislado, preservando staged/unstaged/untracked.
P1. Ejecutar una sola suite integral sobre revisión conciliada, con RAM disponible y sin suites/GPU/AVD competidores.
P2. Completar la matriz de 35 WRITE con pruebas de UAC, denegación, interrupción, postcheck y rollback en VM desechable expresamente autorizada con snapshot verificable.
P3. Certificar UI Compact/Developer contra referencias vigentes, DPI físico 100/125/150/200, NVDA/alto contraste, teclado y multimonitor.
P4. Medir rendimiento en reposo y bajo interacción, no solo Working Sets sumados; revisar muestreo WMI, caché y latencia con baseline.
P5. Congelar revisión, empaquetar y comparar hash del artefacto realmente instalado/probado con hash candidato; ejecutar release-readiness y obtener signoff.

## Mejoras Ferrari, solo tras cerrar bloqueos P0/P1
- Motor de diagnóstico con reglas explicables y evidencia local, prioridad por impacto/riesgo/confianza y estados «sin datos».
- Performance Engine con muestreo adaptativo/cachés de duración limitada; perfiles Trabajo/Gaming medidos en el Dell G7, sin overclock o tweaks irreversibles.
- Before/after por acción con baseline comparable, descripción de riesgos, evidencia y estado parcial real.
- Recovery Shield con journal durable, operaciones idempotentes y rollback probado, no prometido; papelera no reversible automáticamente.
- Browser Doctor solo diagnóstico y reparación consentida de Edge sin borrar sesiones, perfiles o extensiones.
- Battery Health observacional: distinguir desgaste de batería del consumo en segundo plano; hardware degradado no se arregla por software.

## Estado final de esta auditoría
**PARCIAL / NO CERTIFICADO.** Contratos focales verdes, compatibilidad PS5/7 negativa verificada, trabajo concurrente preservado. NO se ejecutaron tests .NET integrales, builds, WRITE reales, QA gráfica física nueva, instalación, commit, merge, push o release. No se atribuye porcentaje de avance certificado.
