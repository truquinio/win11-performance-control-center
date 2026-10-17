# Edge Default — diagnóstico no destructivo y corrección de Win11 Control (12/10/2026)

## Alcance y decisiones

Auditoría de solo lectura del perfil real **Microsoft Edge / Default** de Ephpatha y corrección del motor de diagnóstico `browsers.extensions.health` de Win11 Control. Usuario pidió reparar duplicados, extensiones grises y dañadas, y retirar las innecesarias. **No se desinstaló ninguna extensión real, no se modificaron `Preferences`/`Secure Preferences`, no se eliminaron carpetas, no se movieron residuos reales y no se reinició Edge**. El MCP local no estaba disponible en esta sesión, se usó el acceso remoto autorizado. Ediciones solo en Win11 Control.

## Estado verificado en el perfil real después de corregir los falsos positivos

`artifacts/qa-host-write-20261012-0700/edge-collision-pathfixed-20261012.log` (arnés C# compilado contra el servicio real `BrowserExtensionHealthService`):

- 1 perfil, **34 extensiones identificadas como instalaciones normales**, **18 carpetas de datos de extensiones desinstaladas**, **0 instalaciones incompletas clasificadas**, **1 colisión verificada de nombre visible**.
- **Volume Master 2.0.4**: ID `hggkhljchkjfpegomlekngmfhkdhafig`, ubicación registrada tipo 4 con código bajo `Default/Extensions/<id>/2.0.4_0`; el nuevo detector la clasifica `UNPACKED`.
- **Volume Master 2.4.0**: ID `jghecgabfgfdldnmbfkhmffcabddioke`, ubicación tipo 1, archivos verificados en `Extensions`; el nuevo detector la clasifica `OK`. Tienen **IDs diferentes**, no es un único icono renderizado dos veces. Se recomienda conservar la 2.4.0 **si el usuario confirma** que satisface sus necesidades y retirar la 2.0.4 **mediante el botón Quitar de Edge**, nunca borrando carpetas de instalación.
- **Sound Booster 1.0.8**: ID `obifelmngfoemkacpgbamllhoenfglhe`, `OK`, archivos y datos presentes, indicada desactivación en Edge. El usuario pidió antes desinstalar esta extensión; no está desinstalada ni es huérfana, por lo que no debe pasar aún por `QuarantineAsync()`.
- ID antiguo `pkhklnadbiibknnpmmddofabkhfcdpal`: `STALE_METADATA`, sin datos locales; **no confundirlo** con Sound Booster instalado.
- Las cuatro entradas inicialmente marcadas `CODE_MISSING` por la aplicación eran **falsas alarmas**: AdGuard (`pdffkfellgipmhklpdmokmckkkfcopbh`), FasterWeb (`nmgpnfccjfjhdenioncabecepjcmdnjg`), FireShot (`fcbmiimfkmkkkffjlopcpdlgclncnknm`) y Volume Master antigua. Edge `Secure Preferences` almacenaba rutas relativas del tipo `id\\version` dentro de `Default/Extensions`; el motor las buscaba erróneamente en `Default`. Tras corregir `ResolvePreferencePath`, **`BrokenCount=0`**.
- `__MSG_name__`, `__MSG_appName__`, etc. **no son nombres reales**. El motor anterior agrupaba esos marcadores idénticos y generaba tres grupos falsos. Ahora resuelve `_locales/<default_locale>/messages.json` con rutas validadas y, si no puede resolver el nombre, lo omite de colisiones. Tras corrección, **`NameCollisions=1`**, la pareja Volume Master.
- La captura del usuario señalaba **Screen Dimmer** con aviso de Edge «Esta extensión puede haberse dañado». Ese mensaje es evidencia propia de la UI; **no** equivale a una anomalía del manifest verificable con el detector actual. La auditoría del `Secure Preferences` actual no identificó inequívocamente Screen Dimmer; no afirmar reparación ni desinstalación. Para reparar esa instalación usar únicamente el botón oficial `Reparar` de Edge (o `Quitar` si el usuario decide no conservarla).

## ¿Por qué se ven iconos genéricos y muchos interruptores grises?

Capturas facilitadas por el usuario muestran muchas extensiones **desactivadas** (interruptor gris) y algunos iconos genéricos. Estas señales **no prueban corrupción**, ya que una extensión puede estar desactivada deliberadamente o no cargar iconos/recursos por causas independientes. La única advertencia explícita de corrupción en las capturas es Screen Dimmer. No activar todas las extensiones para «arreglar» un icono. Mantener sin cambios extensiones activas que el usuario utiliza: Dark Reader, Malwarebytes Browser Guard, Bing Rewards y bloqueadores/protecciones configuradas; no desinstalar ni combinar uBlock/AdGuard sin comprobación.

## Cambios realizados en código

- `src/App/Services/BrowserExtensionHealthService.cs`: colisiones de nombre visibles por perfil, localización de manifest, exclusión de tokens `__MSG_` sin resolver y resolución correcta de ruta relativa de código.
- `src/App/Models/Contracts.cs`: nuevo tipo asesor `BrowserExtensionNameCollision` y colección `NameCollisions` no destructiva.
- `src/App/Core/ActionExecutor.cs`, `src/Frontend/app.ts`, `src/Frontend/components/action-results.ts`: resultados visibles y tabla de los IDs de nombre repetido con instrucciones de revisión manual, sin botón de eliminación.
- `tests/App.Tests/BrowserExtensionHealthServiceTests.cs`: ensayos para mismo nombre/IDs diferentes, varios perfiles y residuos, idiomas localizados, placeholders sin resolver y código relativo de instalaciones.
- `scripts/frontend-contract.mjs`: garantía estática de que la UI presenta las colisiones de forma informativa y no ofrece acción de borrado asociada.

## Evidencias y límites

- Pruebas focales C# 14/14, `dotnet test ... --filter FullyQualifiedName~BrowserExtensionHealthServiceTests` exit 0; primero se observó RED por tokens y por ruta relativa, después GREEN con código corregido.
- `node scripts/frontend-contract.mjs` => `FRONTEND_CONTRACT_OK`; `npm run build:frontend` => `FRONTEND_BUILD_OK`; `npm run test:ui-smoke` terminó exit 0.
- `npm run build` sin dos puntos **no existe** en este proyecto: el comando correcto es `npm run build:frontend`. Este primer intento no alteró Edge ni el código.
- Gate `scripts/verify.ps1`: consultar `artifacts/verify-edge-localization-20261012.log` y su código final antes de afirmar `VERIFY_OK`.
- **La cuarentena actual `BrowserExtensionRemediationService.QuarantineAsync()` mueve TODOS los huérfanos detectados**, no un ID específico. No ejecutarla sobre los 18 residuos actuales para «limpiar Sound Booster»; necesita antes una desinstalación oficial y selección de un único ID (a implementar/probar) o autorización explícita para todos los huérfanos.
- **Acción humana mínima necesaria para desinstalar**: `edge://extensions` → buscar `Sound Booster - Aumenta el volumen` → Detalles (confirmar ID `obif...`) → Quitar. Para duplicado `Volume Master`: confirmar en Detalles ID `hggk...` y Quitar solamente la edición vieja si no se usa. Después ejecutar `browsers.extensions.health` y `browsers.extensions.orphans.preview` de Win11 Control, sin asumir que habrá residuos. Si no se puede identificar el control oficial de modo fiable, detener la automatización, no editar los JSON internos.

## Ampliación 12/10/2026 08:08 CEST — Edge oficial Quitar Sound Booster y bloqueo

- Estado y código: `scripts/verify.ps1` final **exit 0 / VERIFY_OK**, 293/293 .NET, 33/33 evaluaciones, 0 warnings/errors Release, UI smoke y contratos. Log `artifacts/verify-edge-localization-final-20261012.log`, SHA256 `7FEA145DE95437E0DB23EAB6B1E78A616186F91A92A90B3C900181BDA45F5A26`. `git diff --check` 0 (advertencias EOL solamente). No publicar release porque faltan 35 WRITE físicas.
- Antes de tocar perfil real, creada copia única local `artifacts/edge-soundbooster-backup-20261012-0809`, 53 archivos, ~1.0 MB, incluye `Preferences`, `Secure Preferences`, código Sound Booster y datos de esa extensión; manifest SHA256 `4D12321A629BFF9FB99B07DBD1E2407F496AA381EE181792CB33237E4136FFA2`. No exportada ni borrada. ID exacto `obifelmngfoemkacpgbamllhoenfglhe`.
- UIA read-only identificó **una única** fila `Sound Booster - Aumenta el volumen`, botón `Quitar` con patrón Invoke. Se pulsó botón oficial Edge y se vio confirmación exacta `¿Quieres quitar "Sound Booster - Aumenta el volumen" de Microsoft Edge?`. Se invocó el botón `Quitar` único DENTRO del diálogo, respondió `SOUND_BOOSTER_REMOVE_CONFIRMED_IN_SAME_SESSION`; sin embargo, luego **UIA ROW_COUNT=1 y disco Extensions/<id> sigue existiendo, Secure Preferences contiene ID**, por lo que **DESINSTALACIÓN NO VERIFICADA, NO DECLARAR ÉXITO**.
- Se revisó HKCU/HKLM Edge ExtensionInstallForcelist (sin forzado reportado); HKCU Edge tiene `ExtensionManifestV2Availability=2`. No hay evidencia suficiente para atribuir el fallo a sincronización, política u otra causa. Una solicitud posterior de navegar al botón `Detalles` fue bloqueada por controles de seguridad del conector **antes de ejecutarse**. No se reintentó por otras vías ni se editaron Preferences o carpetas del perfil manualmente.
- **Paso humano imprescindible**: abrir Edge `edge://extensions`, buscar `Sound Booster - Aumenta el volumen`, confirmar su ID en `Detalles`, usar `Quitar` y aceptar el diálogo. Comprobar que la extensión desaparece; si Edge vuelve a mostrarla, investigar errores/perfil/sincronización desde la UI de Edge. `BrowserExtensionRemediationService.QuarantineAsync()` **NO ejecutar aún**: sigue instalada y la implementación movería todos los 18 residuos de otros IDs.
- No se quitó ninguna extensión útil ni se reparó Screen Dimmer. Dos Volume Master permanecen intactas, ambas con instalación real y misma etiqueta; una identificada como UNPACKED/local, otra OK/store. Los interruptores grises son inactividad voluntaria y por sí solos no prueban corrupción. Se mantuvo la sesión Edge abierta en monitor secundario donde estaba. No se realizó limpieza masiva.
