# Eco-QA 35 — laboratorio WRITE sin efectos destructivos (12/10/2026)

## Alcance verificable

**Objetivo del usuario:** probar las 35 acciones WRITE sin VM y sin poner en riesgo Ephpatha. La app mantiene un solo `ActionCatalog` de autoridad; Eco-QA 35 distingue:

1. `QA_NO_DESTRUCTIVA_APROBADA`: requisitos de validación, denegación, metadatos y rutas de aislamiento comprobados.
2. `WRITE_SINTETICA_VERIFICADA`: ejecución aprobada contra recursos exclusivamente desechables dentro de `TestDataRoot`.
3. `OPERACIÓN_REAL_CERTIFICADA`: **NO CONCEDIDA** por pruebas simuladas, contratos, smoke DEMO ni por ejecutar servicios sobre datos sintéticos.

La certificación real de cambios del sistema, UAC, hardware, reinicio y rollback físico sigue en `scripts/release-readiness.ps1` y requiere un entorno con recuperación verificable. La máquina principal no se usa como objetivo de operaciones globales potencialmente destructivas.

## Barrera WRITE común: fallo reproducido y corregido

**Defecto previo**: `ActionExecutor.ExecuteAsync` validaba tipos y parámetros, pero comprobaba `confirmed` dentro de cada handler, potencialmente **después** del registro `STARTED` o de solicitar UAC. La primera prueba de `system.workload.inuse` con `confirmed=false` reveló el problema: RED (exit 1).

**Corrección**: ahora, inmediatamente después de `ValidateParameters`, todos los ActionMode.WRITE exigen `confirmed:true` exacto. Rechaza antes de `workloadGuardService.ShouldBlock`, `OperationCoordinator.Begin`, `AppLogger.STARTED`, `PrivilegeBoundary` y `ElevatedActionClient`. Se mantienen los `RequireConfirmed` individuales por defensa en profundidad.

**Evidencia**: `tests/App.Tests/EcoQa35InterlockTests.cs` genera todos los IDs WRITE desde C# `ActionCatalog`, no una lista manual. Cada una de las 35 acciones pasa cuatro pruebas de IPC por `HostBridge.HandleAsync`:
- Denegar `confirmed:false` sin registrar `STARTED` ni solicitar UAC.
- Denegar `confirmed` ausente.
- Denegar tipo inválido `confirmed:"yes"`.
- Denegar parámetro arbitrario incluso con estructura aparentemente válida.

Se comprueba además que el registro contiene exclusivamente `bridge/REJECTED` y que no aparecen `workload-mode.json` ni `pagefile.json` como efecto secundario. **141/141 pruebas aprobadas (35 × 4 + inventario)**, exit 0. No se envió ningún `confirmed:true` a operaciones reales durante ese lote.

El script `scripts/eco-qa35.ps1` ejecuta el filtro de pruebas, captura TRX y log de xUnit, valida por ID los 140 resultados individuales, calcula SHA256 y escribe un manifest nuevo por ejecución (no sobrescribe evidencia). Última evidencia:
- `artifacts/eco-qa35-20261012-095231-3d511b55/eco-qa35-manifest.json`
- SHA256 `08E18693D47EC371879BB1876CBFFF0EA72027E347221755934190261C860AD0`
- `ECO_QA35_GUARDS_OK actions=35 negativeCases=140 inventory=1 writes=0`

El contrato `scripts/eco-qa35-contract.mjs` comprueba existencia de cobertura dinámica y orden correcto de denegación; se ejecuta desde `scripts/verify.ps1` antes de smoke/build. El gate canónico también ejecuta todos los xUnit.

## WRITE positivas en sandbox

`tests/App.Tests/EcoQa35SyntheticFlowTests.cs` pasó **7/7 tests de flujo**, cubriendo **9 IDs WRITE diferentes**, todos mediante HostBridge y servicios de producción inyectados en la raíz temporal `TestDataRoot`:

| Acciones | Recurso de prueba / evidencia | Límite |
| --- | --- | --- |
| `system.workload.inuse/auto/maintenance` | Tres cambios de modo, confirmación por IPC y fichero de estado exclusivamente en raíz de prueba | No modifica modo real del usuario |
| `maintenance.policy.readonly/off` | Dos políticas leídas y persistidas en la raíz temporal, sin ejecutar lote pesado | No activa tareas automáticas del usuario |
| `browsers.extensions.orphans.quarantine/restore` | Un único perfil sintético, 2048 bytes generados aleatoriamente, restauración con SHA256 idéntico | No toca extensiones reales |
| `browsers.edge.performance.optimize/restore` | Perfil de política simulado por `EdgePerformanceService(evaluationMode:true)`, rollback a fichero temporal, verificación del borrado del snapshot | No edita registro HKCU de Edge |

También existen ensayos previos en `tests/App.Tests/EcoQosRoundTripTests.cs` sobre **un proceso hijo desechable** para EcoQoS, y en `tests/App.Tests/ServiceStartupRemediationServiceTests.cs`, `EdgePerformanceTests.cs` y `BrowserExtensionRemediationServiceTests.cs` para otros mecanismos. Se debe revisar sus resultados y origen antes de aumentar el número de casos positivos certificados.

## Ejecutar Eco-QA de nuevo

```powershell
cd 'D:\DOCS\AI\PROYECTOS IA\Win11-repair'
node .\scripts\eco-qa35-contract.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\eco-qa35.ps1
dotnet test .\tests\App.Tests\App.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~EcoQa35SyntheticFlowTests' --verbosity quiet
```

**Lo que falta para un 100 % de pruebas positivas:** definir y ejecutar para cada WRITE un sustituto comprobable de efecto/rollback (o proceso/archivo/dispositivo sintético cuando proceda) y trazar la integración sin permitir acceso al sistema real. No convertir mocks aislados en prueba de APIs Windows globales; no reducir los controles de `release-readiness.ps1`. El equipo real debe conservar íntegros perfiles Edge, otros proyectos, registro de inicio, servicios, drivers, energía y pagefile.


## Ampliación 12/10/2026 10:45 CEST — procesos sintéticos y contrato de inclusión

- Se etiquetó `EcoQosRoundTripTests` como `EcoQa35Synthetic`. Sus seis pruebas usan un `PING.EXE` hijo propiedad del test y una raíz temporal; verifican apply/restore con APIs reales de Windows para ese proceso aislado, preservación del baseline ante reintento, fallo cerrado si no se guarda rollback, rechazo de restore sin snapshot, defensa contra PID reutilizado/proceso terminado y recuperación del estado. No actúan sobre otros procesos ni equivalen a certificar QoS del host.
- Se incluyó `ProcessHygieneTests.HostBridge_MaintenanceMode_AllowsOnlyPreclassifiedSyntheticStop` en la misma etiqueta. Recorre HostBridge y prueba una respuesta `EVALUATION` con fixtures en memoria; no termina un proceso real.
- Contrato actualizado para exigir ambos casos en el conjunto. RED detectó diferencia CRLF/LF en la aserción estática; se cambió a patrón tolerante a saltos de línea y pasó.
- Validación GREEN: `node scripts/eco-qa35-contract.mjs` => `ECO_QA35_CONTRACT_OK`; `dotnet test .\tests\App.Tests\App.Tests.csproj -c Release --no-restore --filter '(FullyQualifiedName~EcoQa35)|(Layer=EcoQa35Synthetic)' --verbosity quiet` => exit 0, **158/158**, 0 omitidas.
- Alcance: 9 IDs WRITE tienen flujo positivo por HostBridge en sandbox; 3 servicios ya disponían de fixtures sintéticos; ahora se incluyen además `cpu.ecoqos.apply/restore` contra proceso hijo y `processes.hygiene.stop` mediante fixture de evaluación. Esto amplía la evidencia sintética sin afirmar que las 35 acciones tengan efecto positivo aislado verificable ni que alguna WRITE global esté certificada en producción. UAC, VM/snapshot, operaciones globales y rollback físico siguen sin certificación.


## Adaptador sintético de planes de energía — 12/10/2026 11:06 CEST

- `PowerPlanTuningService` permite inyectar un ejecutor acotado de comandos; la aplicación conserva `powercfg.exe` como ejecutor predeterminado. Las pruebas pasan un ejecutor falso y una ruta de snapshot dentro de `TestDataRoot`, por lo que no invocan el plan de energía del host ni escriben en AppData.
- `EcoQa35SyntheticServiceTests.PowerPlanActions_RoundTripThroughAnInjectedSyntheticCommandRunner` verifica los IDs `thermal.power.balanced`, `thermal.power.performance` y `thermal.power.restore`: captura/restaura baseline, repetición idempotente y ausencia del snapshot tras rollback.
- GREEN focal: `dotnet test ... --filter FullyQualifiedName~PowerPlanActions_RoundTripThroughAnInjectedSyntheticCommandRunner` exit 0, 1/1. El contrato estático ya exige este caso y reporta 7 grupos positivos sintéticos; ejecutar el contrato y el gate integral después de este cambio sigue pendiente.
- Clasificación: adaptador de simulación no destructivo. No certifica `powercfg`, cambios globales, UAC, estado físico ni rollback de Windows.


## Contrato de adaptadores sintéticos — 12/10/2026 11:17 CEST

- `scripts/eco-qa35-contract.mjs` ahora exige las pruebas de evaluación de `ServiceStartupRemediationService`, `StartupEntryRemediationService` y `ScheduledTaskRemediationService`, además del round-trip de planes de energía ya obligatorio. Son servicios con estado/runners de evaluación y recursos sintéticos; no ejecutan SCM, registro real, tareas globales ni `powercfg` del host.
- Contrato GREEN: `node scripts/eco-qa35-contract.mjs` => `ECO_QA35_CONTRACT_OK catalogWrite=35 rejectionCases=140 positiveGroups=10 ... physicalWritesNotCertified`.
- Verificación focal: `dotnet test ... --filter FullyQualifiedName~EcoQa35SyntheticServiceTests` => 4/4. Filtro combinado `(FullyQualifiedName~EcoQa35)|(Layer=EcoQa35Synthetic)` => 159/159, cero omitidas.
- Runner negativo: `scripts/eco-qa35.ps1` => `ECO_QA35_GUARDS_OK actions=35 negativeCases=140 inventory=1 writes=0`. Manifest `artifacts/eco-qa35-20261012-111705-05c707e6/eco-qa35-manifest.json`, SHA-256 `326DE207606DF12A4CB874979CF2A7E461C7C99DB9DD01DEDD32EBE28D528066`.
- Clasificación: `QA_NO_DESTRUCTIVA_APROBADA`. El recuento de diez grupos describe cobertura automatizada con fixtures/adaptadores; no equivale a 35 flujos positivos ni a `OPERACIÓN_REAL_CERTIFICADA`.


## Adaptador sintético del pagefile — 12/10/2026 12:07 CEST

- `PageFileTuningService` admite `PageFileTuningBackend` inyectable para captura, cambio de `AutomaticManagedPagefile`, escritura de `PagingFiles` y disponibilidad de unidad. El backend normal conserva WMI/Registro/DriveInfo; pruebas usan estado y ruta `TestDataRoot`, sin acceder a pagefile real ni Registro.
- El test RED no compilaba porque faltaban backend y constructor inyectable. Tras implementar la interfaz, dos pruebas pasan: round-trip exacto + idempotencia, y recuperación de fallo de escritura con snapshot.
- El test encontró que `RestoreAsync()` podía informar `RESTORED` sin volver a poner la lista original `PagingFiles` cuando el baseline era automático. Ahora escribe el valor capturado antes de verificar y retirar el snapshot; la prueba comprueba la igualdad exacta.
- GREEN focal: `dotnet test ... --filter FullyQualifiedName~PageFileActions_` => 2/2. Contrato y filtro conjunto `(FullyQualifiedName~EcoQa35)|(Layer=EcoQa35Synthetic)` => `positiveGroups=11`, 161/161, cero omitidas.
- **Pendiente de ejecutar**: `scripts/eco-qa35.ps1` fue bloqueado por el revisor automático (`429 Too Many Requests`), por lo que no se generó un manifest nuevo después del cambio. Gate integral y paquete tampoco se ejecutaron.
- Clasificación exclusivamente sintética; no certifica WMI, Registro, UAC, reinicio ni operación física de pagefile.


## Verificación integrada y perfil del paquete — 12/10/2026 12:19 CEST

- Después del cambio de pagefile, `scripts/verify.ps1` terminó `VERIFY_OK`: 464/464 pruebas .NET, 33/33 evaluaciones; UI smoke 65/65 acciones, 7 flujos contextuales, 3 WRITE simuladas, acceso por teclado y escalas emuladas; Release sin warnings/errores. Duración 180.97 s.
- Paquete QA interno: `artifacts/Win11PerformanceControlCenter-win-x64-20261012-121145-817-1732bc60676b.zip`, SHA-256 `B67E044B5B81738BCDA814A5190A3E6DDF1532EACA16AFE7178D944BEBBEC4B`; cotejo independiente: 33 miembros, 0 discrepancias. EXE SHA-256 `A4616DECD740057E69589BFFA79B124A353A0BAFC6793A992FDDD78311245C82`.
- Perfil del EXE exacto: 60 muestras cada 5 s durante 309.2 s, minimizado en reposo; memoria privada agregada delta `-20.9 MiB`, handles `+5`. CSV `artifacts/profile-sustained-qa-20261012-121304-bbd8504f.csv`, SHA-256 `6B8022E4328FA5ABA3C3A5A1AA13600819037DE5AD64CCBF996401A328F6D72D`; el proceso QA terminó y `WPF_INSTANCES=0`.
- El perfil no incluye interacción ni carga. No acredita ausencia de fugas ni estabilidad prolongada bajo estrés. Las escalas fueron emuladas; no equivalen a DPI físico/NVDA/contraste real.
- `scripts/eco-qa35.ps1` no generó manifest posterior al cambio: el auto-review devolvió 429 antes de ejecución. No se reintentó por otra vía. Los 11 grupos positivos siguen siendo cobertura sintética parcial; 35 WRITE reales/UAC/rollback no certificados.
- `scripts/release-readiness.ps1` permanece bloqueado por falta de `artifacts/release-certification.json`; paquete solo interno QA. Sin publicación, cambios críticos en Ephpatha ni afirmación de 100%.


## Caso positivo: bundle diagnóstico sanitizado — 12/10/2026 12:22 CEST
- `DiagnosticBundle_IsSanitizedAndContainsExpectedFiles` se clasifica además como `EcoQa35Synthetic`. `HostBridge.CreateDefault(TestDataRoot)` dirige export/state a la raíz temporal; el flujo WRITE crea un ZIP local de evaluación, comprueba 10 JSON y saneamiento (IDs PnP/USB, argumentos/comandos y clave de producto ausentes). Solo las lecturas fuente son observación del host.
- Contrato estático GREEN: `node scripts/eco-qa35-contract.mjs` => catálogo 35, 140 rechazos y 12 grupos positivos sintéticos.
- Prueba focal por `dotnet vstest` sobre el assembly existente: 1/1. La compilación incremental para incluir la nueva etiqueta no se pudo verificar: `dotnet test` chocó con acceso denegado a `MarkupCompile.cache` mientras permanecía activo PID dotnet 15416; no se terminó el proceso ajeno. El test ya compilado verifica la lógica, pero la etiqueta requiere confirmación en próxima compilación.
- No se ejecutó Eco-QA runner ni suite integral por este cambio de clasificación. Este caso no acredita artefactos de publicación ni WRITE globales reales.


## Inventario positivo pendiente — 12/10/2026

- Revisión del catálogo autoritativo de 35 WRITE frente a los fixtures sintéticos descritos y casos etiquetados: los 12 grupos actuales representan 24 IDs WRITE con round-trip positivo; no deben leerse como 35 flujos positivos.
- No encontré caso positivo sintético por ID para estos 11: `system.integrity.repair`, `memory.trim`, `disk.cleanup.execute`, `disk.hibernate.reduce`, `network.flushdns`, `network.winsock.reset`, `drivers.rescan`, `drivers.usb.restart`, `multimedia.audio.restart`, `windows.update.services.restart`, `explorer.restart`.
- Propuesta de cobertura: hijos/procesos propiedad del test para `memory.trim`; archivo sintético y papelera aislada para `disk.cleanup.execute`; runners/adaptadores inyectables para DISM, hibernación, red, drivers, audio, Windows Update y Explorer. Cada fixture debe demostrar que ningún comando/target real se invoca, además de denegación, error/cancelación, idempotencia y rollback donde proceda.
- Esta lista es un inventario de brechas de QA sintética, no un juicio de funcionalidad; la cobertura negativa de consentimiento para los 35 permanece aparte. Ninguno de esos sustitutos dará `OPERACIÓN_REAL_CERTIFICADA`.


## Proceso sintético propio: MemoryTrim — 12/10/2026

- Se añadió `tests/App.Tests/EcoQa35MemoryTrimTests.cs`: lanza `PING.EXE` propiedad del test, construye identidad PID + hora de inicio, ejecuta `TrimWorkingSetsAsync` sólo sobre ese hijo y lo mata en `finally`. No enumera ni recorta otros procesos. El estado EcoQoS de rollback vive bajo `TestDataRoot` y se verifica que MemoryTrim no lo cree.
- Prueba focal: `dotnet test .\tests\App.Tests\App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~EcoQa35MemoryTrimTests --verbosity quiet -m:1` exit 0, **1/1**, cero omitidas; duración 156 ms. Clasificación sintética de API nativa aplicada únicamente al proceso desechable.
- `scripts/eco-qa35-contract.mjs` ahora exige este caso; con él los grupos positivos descritos suman 13 y los IDs positivos dedicados, 25/35. Quedan diez brechas positivas: `system.integrity.repair`, `disk.cleanup.execute`, `disk.hibernate.reduce`, `network.flushdns`, `network.winsock.reset`, `drivers.rescan`, `drivers.usb.restart`, `multimedia.audio.restart`, `windows.update.services.restart`, `explorer.restart`.
- Este test no recorta memoria en otro programa, no certifica UI/UAC y no da por aprobada una operación real de WRITE.


## Adaptador de comandos globales y reinicio USB sintético — 12/10/2026

- `SystemRemediationService` acepta un runner inyectable para sus cinco comandos externos; la instancia de producción mantiene `RunAsync` con rutas Windows y límites actuales. Los tests usan solo un runner falso y comprueban DISM/SFC, DNS, Winsock y `pnputil /scan-devices` sin iniciar ejecutables ni mutar el host.
- Tres pruebas sintéticas cubren éxito de las cuatro acciones, timeout/fallo y recuperación al reintentar Winsock, y fallo DISM sin iniciar SFC. Verifican argumentos, límites de timeout, estados y `RebootRequired` solo tras éxito. No simulan un rollback porque estos flujos no tienen rollback implementado/aplicable.
- `UsbDiagnosticsService(evaluationMode: true)` usa inventario fijo sintético y sale antes del runner `pnputil`. Dos pruebas cubren resultado `EVALUATION` y rechazo de dispositivo protegido/desconocido. No reinician ningún dispositivo real.
- Validación focal: runner contractual Eco-QA GREEN; `dotnet test ... --filter '(FullyQualifiedName~EcoQa35UsbRestartTests)|(FullyQualifiedName~EcoQa35SystemRemediationTests)'` exit 0, 5/5. Filtro combinado `(FullyQualifiedName~EcoQa35)|(Layer=EcoQa35Synthetic)` exit 0, **177/177**, cero omitidas.
- La cobertura positiva sintética asciende a 31/35 IDs en 16 grupos descritos. Pendientes positivos por ID: `disk.cleanup.execute`, `disk.hibernate.reduce`, `multimedia.audio.restart`, `windows.update.services.restart`, `explorer.restart`. Los 140 rechazos comunes siguen cubriendo consentimientos/entradas negativas para todas las 35.
- Clasificación: `QA_NO_DESTRUCTIVA_APROBADA` solo para casos sintéticos del filtro; ninguna WRITE global real se ejecutó, y `OPERACIÓN_REAL_CERTIFICADA` sigue sin concederse.


## Cobertura sintética ampliada: comandos globales y USB — 12/10/2026

- El contrato ahora reporta 16 grupos y 31/35 IDs con positivo sintético; tests focales de comandos globales + USB **5/5** y filtro combinado **177/177**, 0 omitidos. Los 140 casos de denegación siguen cubriendo los 35 IDs WRITE.
- `SystemRemediationService` usa un runner falso en pruebas y conserva el runner real como predeterminado. Los cinco invocados cubren cuatro acciones y verifican argumentos, límites: DISM/SFC 25 min; DNS/Winsock/reescaneo 2 min; error y timeout con resultado fail-closed, retry y no ejecución de SFC tras fallo DISM. Ningún comando del host se inició.
- USB en modo evaluation usó solo dispositivos sintéticos; resultado EVALUATION para cámara ficticia y rechazo de root hub/desconocido, sin ejecutar `pnputil`.
- Restan cinco positivos sintéticos por ID: `disk.cleanup.execute`, `disk.hibernate.reduce`, `multimedia.audio.restart`, `windows.update.services.restart` y `explorer.restart`. Los runners deben seguir simulando efectos globales, preservar paths/servicios reales y probar error/recuperación donde aplique.
- `OPERACIÓN_REAL_CERTIFICADA` continúa pendiente de VM/UAC/snapshot, validación física, sign-off y gate de publicación.


## Ensayos de reinicio de servicios con adaptador — 12/10/2026

- `SystemRemediationService` admite además un `serviceRestartRunner` inyectable. Producción conserva `RestartServiceAsync` (enumeración, stop/start y verificación de estado); los tests usan resultados sintéticos y no contactan al Service Control Manager.
- Dos casos verifican audio (`Audiosrv`) y Windows Update (`BITS`, `wuauserv`) por nombre/orden, éxito y fallo parcial. Ante error de BITS se intenta también wuauserv y se informa `SERVICE_RESTART_PARTIAL`; no se declara reboot ni rollback falso.
- Test focal `EcoQa35SystemRemediationTests`: 5/5; contrato GREEN; filtro combinado Eco-QA: **179/179**, cero omitidas.
- El recuento positivo sube a 33/35 en los 16 grupos existentes. Restan: `disk.cleanup.execute`, `disk.hibernate.reduce` y `explorer.restart`. El runner sólo certifica lógica de orquestación con adaptador; no reinicia servicios reales.


## Corrección de recuento y Explorer — 12/10/2026

- Recuento recalculado por IDs WRITE únicos: el total correcto después de hibernación era 33/35, no 34; Explorer añade 1 y el estado vigente es **34/35 IDs** en **15 grupos positivos**. Los apartados anteriores quedan como cronología, no se reescriben.
- `SystemRemediationService` tiene `explorerRestartRunner` inyectable y predeterminado al restart real. El caso simula fallo de verificación seguido por recuperación, sin terminar ni iniciar el Explorer del usuario. El handler conserva `RequireConfirmed` y delega en el servicio.
- Contrato GREEN. Focal de remediación+USB: **9/9**, combinado `(FullyQualifiedName~EcoQa35)|(Layer=EcoQa35Synthetic)`: **181/181**, 0 omitidas.
- Única brecha positiva sintética por ID restante: `disk.cleanup.execute`. La cobertura negativa 35×4 permanece. Ninguna prueba de adaptador equivale a operación real certificada ni sustituye VM/UAC/snapshot/sign-off.

## Limpieza de almacenamiento aislada: cobertura positiva completa — 12/10/2026

- `HostBridge.CreateDefault(TestDataRoot)` dirige el reciclaje del modo de evaluación a `StorageRecycle` dentro del fixture; la configuración de producción conserva el reciclaje real de Windows.
- `CleanupWithPreview_ConfirmsAndRecyclesOnlySyntheticFile` crea un archivo sintético de ocho días, exige preview y confirmación, comprueba su SHA-256 tras el traslado, verifica rechazo del consentimiento de un solo uso y restaura el original desde el reciclaje aislado con el mismo hash.
- La prueba no escribe en el Registro, carpetas de usuario, Papelera de Windows ni otros procesos. El gate contractual inspecciona el adaptador del puente, la prueba y la restauración.
- Eco-QA focal combinado: 182/182 aprobadas, cero omitidas. El caso individual de limpieza pasó 1/1. El contrato reporta 35/35 IDs WRITE con positivos sintéticos y 16 grupos; los 140 rechazos cubren permisos/consentimiento.
- Esto completa `QA_NO_DESTRUCTIVA_APROBADA` para el inventario sintético actual. No significa `OPERACIÓN_REAL_CERTIFICADA`: aún hacen falta VM desechable, snapshot, UAC, rollback de operaciones reales, estabilidad física, validación visual/publicación y gate con evidencia firmada.