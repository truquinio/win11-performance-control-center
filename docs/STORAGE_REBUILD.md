# Limpieza C y D — reconstrucción segura (10/10/2026)

## Arquitectura recuperada

Win11 Performance Control Center (.NET 10/WPF/WebView2/TypeScript) sigue siendo la aplicación. Se reutilizan StorageAnalysisService (cachés regenerables), StorageWatchService (baselines de volumen) y el catálogo tipado de acciones. No se agrega servicio residente.

## Acciones

- disk.duplicates.scan (READ): StorageForensicsService realiza un muestreo acotado de carpetas seleccionadas en C: y D:. Agrupa por tamaño y verifica contenidos con SHA-256. Resuelve Downloads al destino real si es junction, rechaza otros reparse points y rutas del sistema, y limita tiempo, cantidad y tamaño de archivos a hashear. Partial=true significa que NO se inspeccionó todo el disco. El ahorro mostrado es lógico y potencial, nunca físicamente liberado. No borra duplicados.
- disk.cleanup.safe (DRY_RUN): muestra los archivos antiguos dentro de la allowlist, con un mínimo de 7 días.
- disk.cleanup.execute (WRITE): exige confirmación y utiliza la Papelera de Windows en lugar de borrado permanente. Si falla, informa error sin utilizar File.Delete como alternativa. La ruta original y el tamaño quedan en el manifiesto ubicado en %LOCALAPPDATA%\Win11PerformanceControlCenter\State\recycle-manifest.jsonl. Cada intento genera INTENT; los movimientos finalizados generan MOVED.
- disk.storage.watch y disk.hotspots.scan: diagnósticos existentes, limitados, de solo lectura.

## Protección

Se excluyen Windows, Program Files, ProgramData, Recycle Bin, Git, node_modules, WhatsApp, Claude Extensions, workspaceStorage, emu_now, AndroidEmulator, enlaces/junctions y ubicaciones no autorizadas. Dentro de Temp sólo son candidatos .tmp y .temp con edad mínima. No se reciclan SQLite/WAL, QCOW2, keystores, APK ni logs desde Temp. Si está abierto el proceso relacionado con una caché, se omite la categoría.

La acción WRITE no vacía Papelera, desactiva Defender, cambia BIOS, pagefile, hibernación ni controladores. La restauración es manual desde el Explorador de Windows con la ruta original del manifiesto. Si existe INTENT sin MOVED, el resultado es indeterminado: verificar en Papelera antes de repetir o restaurar.

## Límites explícitos

- No es una búsqueda exhaustiva de duplicados en toda la superficie de C: y D:. Los árboles críticos se excluyen, y la búsqueda se limita por tiempo/cantidad.
- No se eliminan duplicados de forma automática, ni versiones de APK, informes o snapshots de proyectos; requieren revisión.
- El campo histórico DeletedBytes expresa bytes lógicos trasladados a Papelera. El campo physicallyRecoveredBytes es null cuando las escrituras simultáneas impiden atribuir recuperación.
- La restauración es manual mediante Papelera; no hay rollback automático de esta acción.
- El seguimiento volumétrico existente no atribuye crecimiento a un proceso específico.

## Verificación

Pruebas con fixtures de SHA-256, tamaños coincidentes pero contenido distinto, límites parciales, enlaces, directorios críticos, reciclaje simulado, fallos de reciclaje, proceso en uso, temporales protegidos y escritura de manifiesto. No se limpian discos reales durante las pruebas.

Puerta de calidad: pwsh -NoProfile -File scripts/verify.ps1. Compilación local: pwsh -NoProfile -File scripts/build-local.ps1. Sin push ni reinicio automático.


## Nuevo seguimiento por carpetas

La accion READ disk.directories.watch guarda hasta 32 capturas por carpeta en State/storage-directories.json. Mide una lista acotada de ubicaciones de C: y D: con limite por carpeta de tiempo y archivos. En la segunda captura calcula diferencias de tamano solo si ambas lecturas son completas; un arbol parcial muestra «No comparable». Los reparse points no se recorren y los archivos de los proyectos no se modifican. Esto identifica carpetas candidatas a investigacion; no atribuye el crecimiento a un proceso ni sustituye USN Journal.
