# Win11 Control — checklist vivo de aceptación

Actualizado: 2026-10-15. Fuente normativa: docs/RELEASE_ACCEPTANCE_MATRIX_20261012.md.
Las casillas registran evidencia concreta; una prueba sintética no cierra certificación física. No hay porcentaje agregado: las tres áreas de aceptación son gates independientes.

## Completado con evidencia de una ejecución concreta
- [x] Eco-QA 35 no destructiva: 141/141 tests, 35 WRITE × 4 guardas negativas + inventario; 0 WRITE real. Evidencia: artifacts/eco-qa35-20261015-021514-10c2bb1e/eco-qa35-manifest.json (SHA-256 FB67A7163DE72B91544451F26DD361DC8C13B889A42DE9D3A39B4D580E2F23EB), eco-qa35.trx (D71B64D8E96C3D5A366F8B794B815FC33C9D6E17EE0A2963AA41E37AFC0CF575). Clasificación: QA_NO_DESTRUCTIVA_APROBADA, no OPERACIÓN_REAL_CERTIFICADA.
- [x] Contratos negativos del gate: PowerShell 5.1/7 rechaza manifiesto sintético incompleto; traversal/junction fuera de raíz rechazados. Evidencia y hashes en docs/HANDOFF.md, sección “Actualización QA — gate SHA256 y contención de rutas — 15/10/2026”.
- [x] Smoke UI headless del DEMO y capturas Compact/Developer en viewport escritorio/tablet; ejecución y límites anotados en docs/HANDOFF.md (15/10/2026). No es QA física ni aprobación de mockup.
- [x] Inventario del ActionCatalog y alineación de metadata frontend: 89 acciones (51 READ, 3 DRY_RUN, 35 WRITE), igualdad verificada en evidencia de docs/HANDOFF.md. No demuestra el resultado físico de las acciones.
- [x] Prueba focal de recuperación/cancelación: 6/6 tests pasaron en 265 ms (TRX hash 9C45F9FEEE78547460EF549CB1B80242BEB14A0EF2784000CC99A1960C850236; binario de tests 0B54290BA663648E592A9B30CBAF6898E599850B133E6335943A75941BE8AA8D). Cubre lease del coordinador, registro de WRITE cancelada y respuesta del watchdog; no prueba interrupción cooperativa del trabajo ni impide solapamiento tras timeout.

## Pendiente — no marcar hasta completar y guardar evidencia
- [ ] Resolver cancelación/timeout: revisión estática detectó que el watchdog puede abandonar una lectura sin detenerla y liberar el lease. Hacer prueba focal aislada, definir cancelación cooperativa y evitar solapamiento; cambios en HostBridge/ActionExecutor/tests tienen propietarios pendientes.
- [ ] Comparar Compact y Developer en la app real con mockups aprobados, capturas completas e interacción; confirmar aprobación manual del mockup Developer vigente.
- [ ] Probar la app WPF/WebView2 físicamente a 100%, 125%, 150% y 200%; teclado, foco, NVDA/Narrator y alto contraste. La consulta de Windows a 100% de escala no cuenta como prueba de la app.
- [ ] QA interactiva prolongada: rendimiento, memoria, cancelación/errores/recuperación; medir en hardware objetivo y conservar trazas sin cerrar procesos ajenos.
- [ ] Comprobar/reparar selectivamente Sound Booster y Screen Dimmer en Edge Default por la UI oficial; no alterar otras extensiones, perfiles ni datos. En la observación más reciente Edge muestra una ventana titulada Personal con 18 páginas; Computer Use no pudo identificar la URL y detuvo la lectura. No se modificaron extensiones; confirmar que la ventana corresponde a Default antes de actuar.
- [ ] Conciliar los cambios staged/unstaged/untracked por propietario. Main observado en f0277c364e791731378d2f55127c090beaf21cdb con cambios concurrentes y 7 worktrees; consultar el estado live y confirmar propietarios antes de modificar o conciliar archivos.
- [ ] Congelar una revisión integrada; ejecutar una sola vez el gate integral sobre fuentes reconciliadas y producir el paquete QA reproducible.
- [ ] Verificar SHA-256 del ejecutable exacto que se empaqueta y ejecutar ese mismo artefacto; obtener signoff manual. El binario local actual (SHA-256 820C6D67042E3EBB60F2B33547C6D634242BCDEA0FB98C44779E3E2C94BB9FBD) no coincide con el EXE del ZIP QA más reciente, 12/10 (A4616DECD740057E69589BFFA79B124A353A0BAFC6793A992FDDD78311245C82); reempaquetar tras congelar fuentes. El gate sigue bloqueado por falta de artifacts/release-certification.json.
- [ ] Al final, certificar las 35 WRITE en VM Windows 11 desechable con snapshot comprobado: permisos/UAC concedidos y denegados, confirmaciones, postchecks, interrupciones y rollback documentados por acción. No hacerlo en Ephpatha.
- [ ] Publicar solo después de que todos los gates pasen y haya autorización explícita. No publicado.

## Regla de actualización
- Marcar [x] solo la tarea concreta cuya evidencia se verificó; mantener pendientes los criterios físicos aunque pasen tests sintéticos.
- Añadir fecha, comando/resultado, ruta y hashes. No borrar entradas históricas ni cambiar pruebas reportadas por evidencia nueva.
- Estado global actual: PARCIAL / NO CERTIFICADO / NO PUBLICADO.

## Auditoría de espacio — 2026-10-15
- Inventario de metadatos del repo: `artifacts/` = 867 archivos, 86,2 MiB; `src/App/bin+obj` = 20,7 MiB; `tests/App.Tests/bin+obj` = 10,9 MiB.
- Se encontraron grupos de archivos con hashes iguales dentro de bundles y snapshots QA. No se borraron: esos archivos forman parte de versiones de prueba preservadas y pueden ser necesarias para comparar artefactos.
- Verificación específica: cada carpeta `artifacts/demo-preserved-*` tiene 19 archivos de 0,6 MiB, igual que `artifacts/demo`, pero no es un duplicado exacto: en la copia `demo-preserved-20261012-203954-999-f1f6b7853813`, `style.css` es 44.678 bytes / SHA-256 `337560A5C8949F32AF2E49BAF1CCFCC76E291775CCFDBD7684BD05BF4BDB8A35`; el DEMO actual es 43.519 bytes / `E557B89232D59BC57F45EF413B061B910FAB0ADAEA60D4E791C5E813E707A287`. `docs/HANDOFF.md` cita estas carpetas como respaldo de versiones previas. Ninguna carpeta eliminada ni enviada a la papelera.
- Estado: limpieza revisada, sin candidato seguro con ahorro relevante. Mantener las versiones preservadas y la evidencia de QA hasta que se concilien las fuentes y se determine qué artefactos ya no se necesitan para aceptación.


## Revalidación de bloqueos — 2026-10-16
- Desktop Commander Local responde. Su inventario mostró 0 procesos `msedge.exe` y 13 procesos `msedgewebview2.exe`; estos últimos no prueban Edge abierto.
- CUA mostró solo Codex In-app Browser y Codex MCP Apps; Edge Default no es accesible desde la sesión. No se inspeccionó ni modificó perfil o extensiones.
- Git sigue en HEAD `f0277c364e791731378d2f55127c090beaf21cdb`, 98 entradas de estado y 7 worktrees. El aviso de conciliación sigue esperando confirmación de responsables.
- Edge, congelación/integración y pruebas físicas permanecen pendientes; no se marcaron como completadas. No se repitieron suites, ni se creó un paquete o manifiesto de certificación.
