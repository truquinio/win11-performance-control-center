# Win11 Control — pruebas WRITE acotadas y autorizadas en Ephpatha (12-10-2026, 07:02 CEST)

**Consentimiento específico del usuario:** solo archivos de prueba hacia Papelera, inicio de Claude, tarea programada sintética, extensiones de Edge expresamente mencionadas y cierre de Edge. Las demás acciones WRITE quedan fuera de alcance. No confundir estas pruebas de servicios de producción con validación completa a través del IPC/UI ni con certificación segura de la matriz de 35 WRITE.

## Resultados reales

| Mecanismo | Acceso verificado | Resultado |
| --- | --- | --- |
| `StorageAnalysisService.CleanupSafeAsync` | App.Tests QA harness llama servicio real con target interno `eval.temp.user`, limitado a directorio sintético en `artifacts/qa-host-write-20261012-0700/synthetic-temp` | Archivo viejo `WPCCQA-recycle-synthetic.tmp` de 86 bytes enviado por la API Windows `SendToRecycleBin`: `DeletedFiles=1`, `FailedFiles=0`. Shell Papelera confirmó 1 entrada con mismo nombre. Testigo `WPCCQA-keep-sentinel.txt` preservado |
| `ScheduledTaskRemediationService.DisableAsync/RestoreAsync` | Tarea propia nueva `\\WPCCQA_Noop_20261012_0700`, acción inofensiva `git.exe --version`, único disparo en 2040; no se ejecutó el comando programado | `Ready -> Disabled -> Ready -> Disabled`, todos postchecks reales; final `Disabled` verificado con `Get-ScheduledTask` |
| `StartupEntryRemediationService.DisableAsync/RestoreAsync` | Entrada sintética `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\WPCCQA_GitNoop_20261012` apuntando a `git.exe --version` | `DISABLED -> RESTORED -> DISABLED`; la entrada queda **ausente del registro**, rollback snapshot propio conservado |
| `BrowserExtensionRemediationService.QuarantineAsync/RestoreLatestAsync` | Perfil Edge **completamente sintético** en `artifacts/qa-host-write-20261012-0700/fake-edge-profile-only` y cuarentena propia | `CandidateCount=1`, `QUARANTINED=1`, `RESTORED=1`, SHA-256 del archivo original coincide al restaurar. Perfil Edge real jamás modificado |
| Edge solicitado por usuario | Primero `CloseMainWindow` (no cerró), después `Stop-Process` dirigido exclusivamente a procesos `msedge` conforme autorización expresa | 18 procesos de Edge cerrados; al final 0. No invocar ni adjudicar el cierre a `processes.hygiene.stop`: esa acción de la app solo opera sobre residuos de laboratorio; Chrome, Helium y otros proyectos no se tocaron |

## Hallazgos que impiden declarar otras pruebas PASS

- **Claude**: aplicación MSIX instalada y ejecutándose. Su manifiesto anuncia `windows.startupTask / ClaudeStartup / Enabled=false`; no se encontraron entradas Claude en Win32_StartupCommand, HKCU/HKLM Run o tareas programadas; `Get-Service *Claude*` devolvió 0 servicios. **No se deshabilitó Claude**, porque no se encontró un autoarranque habilitado que fuera seguro modificar. Los procesos Claude existentes siguen funcionando.
- **Edge real**: vista previa de servicio de producción `BrowserExtensionRemediationService.Preview()` detectó **26 candidatos `DATA_WITHOUT_INSTALLATION`, 43.436.584 bytes**, pero la comprobación de `Preferences` no encontró coincidencias inequívocas con Rapid What Font Finder, SaveAs, Sound Booster o WhatRuns. No ejecutar cuarentena REAL indiscriminada: `QuarantineAsync()` actúa sobre **todos** los 26, algunos podrían contener estado de extensiones anteriores sin relación con las cuatro propuestas. Una extensión realmente instalada tampoco es un residuo huérfano.
- La ejecución mediante consola QA prueba el **servicio real compilado** contra el OS/FS/registro, pero no recorre `ActionExecutor.RequireConfirmed`, el bridge WebView2, UAC, los diálogos de la interfaz ni entornos VM snapshots. Para certificar 35/35 hay que verificar estos pasos en VM autorizada. No modificar controles o marcar PASS total artificialmente.

## Evidencia inmutable (no eliminar ni sobrescribir)

Todo el arnés y registros viven solo en `artifacts/qa-host-write-20261012-0700` (sin cambios productivos o staging):
- `host-write-test.log`: `069F7C757E47786D6D64AD56C5254020572360A740813EE0022852BF48A1B965`.
- `startup-test.log`: `3F8D521A835F40C8E99FC2E37E029AD82D661C21F5C8CEF1A2ACB775DA4D9981`.
- `edge-preview.log`: `AF5D82A79CB1DA6FFE11ACB334049991C4D8F2E75E2F5BC5E2C8BF237679A2A5`.
- `edge-quarantine-test.log`: `B8EBE1FDFC3DCC26E6BD628082E32BB5364CF040F19F7FE27F3C42B729029977`.

**Estado de cierre:** solo la tarea sintética queda deshabilitada; entrada sintética Run ausente; archivo temporal sintético en Papelera; testigo y fixture Edge sintética restaurados. No se alteraron servicios Claude ni extensiones reales. No existe motivo para tocar las demás 31 WRITE bajo esta autorización.

## Adenda 07:04 — navegador automatizado independiente

La verificación final encontró nuevos procesos `msedge.exe` después del cierre de los 18 del perfil normal. Su instancia raíz fue creada a las 07:03:43 con `--headless --disable-extensions --user-data-dir=%TEMP%\\playwright_chromiumdev_profile-...`, por lo que **no es la sesión ordinaria del usuario** que se cerró antes. Puede corresponder a automatización en curso: **no se detuvo ni modificó**. No reclamar que todo proceso Edge permanece cerrado indefinidamente ni usar `Stop-Process -Name msedge` de nuevo. Antes de una cuarentena real, la app puede ver `EdgeRunning=true` por ese proceso y fallar cerrado aunque el perfil Default esté cerrado; investigar la política sin relajar protección precipitadamente.

## Adenda 12/10/2026 07:14 CEST — Claude: excepción temporal autorizada y reversión verificada

- Petición expresa: «quita temporalmente la protección de claude y cerrá sus procesos» **mediante Win11 Control**. Se limitó la operación al root `claude.exe` PID `17500` del paquete MSIX `C:\Program Files\WindowsApps\Claude_2.31226.1.0_x64__pzs8sxrjxfjjc\app\Claude.exe`; comprobada identidad/árbol antes de detener. No se intervino en extensiones Edge ni otros procesos.
- Estado inicial: 10 procesos `claude.exe`, core `ProcessHygieneService.cs` sin modificaciones preexistentes, SHA256 `D39E19D82DF72D38D515E6ED16C780376655DECB4D1A42A02FA8ED445DC54C30`. Copia EXACTA previa preservada en `artifacts/qa-host-write-20261012-0700/ProcessHygieneService.claude-before.cs.txt`.
- Para **esta ejecución de QA solamente** se añadió una excepción acotada a constructor del servicio para PID raíz MSIX de Claude, conservando el comportamiento protegido predeterminado. Arnés `artifacts/qa-host-write-20261012-0700/ClaudeHygieneQA.csproj` invocó `Analyze()` predeterminado y comprobó protección; invocó `Analyze()` con excepción de PID explícito, exigió diez PIDs de nombre `Claude`, y llamó a `ProcessHygieneService.StopAsync(item.ProcessId, item.Fingerprint)` real (identidad y huella revalidadas por el motor antes de `Kill(entireProcessTree:true)`).
- Evidencia `artifacts/qa-host-write-20261012-0700/claude-close.log` SHA256 `CC202B62D7E9FA26EF4587BE67BD81BF1A9A9C20762E6B1F9D8BAFBA459D7BC2`: `CLAUDE_DEFAULT_PROTECTED_OK`; `CLAUDE_SCOPED_PREVIEW pid=17500 protected=False stoppable=True related=10`; `CLAUDE_WIN11_CONTROL_STOP success=True status=STOPPED_AND_VERIFIED stopped=17500,8,14184,9684,7356,21348,1748,15556,17104,6660 remaining=`; `CLAUDE_PROCESSES_AFTER=0`; `CLAUDE_TEMPORARY_QA_SUCCESS`; `CLAUDE_STANDARD_PROTECTION_RESTORED=True`; `CLAUDE_QA_EXIT=0` y exit code 0 del wrapper.
- **Protección restaurada** en `finally` con comparación hash de 64 caracteres. Lectura independiente posterior confirmó hash de fuente original `D39E19D82DF72D38D515E6ED16C780376655DECB4D1A42A02FA8ED445DC54C30`, `CLAUDE_COUNT=0`, `git status --short -- src/App/Services/ProcessHygieneService.cs` sin cambios, por tanto no queda bypass en código productivo. No se publica build modificado con excepción de Claude; permanece protegida.
- Limitación: esta prueba demuestra cierre dirigido usando el servicio de producción en un arnés temporal, **no** un click dentro del frontend, ni certificación de las otras operaciones WRITE o UAC/VM. Los procesos podrían reaparecer si otro mecanismo los relanza.
