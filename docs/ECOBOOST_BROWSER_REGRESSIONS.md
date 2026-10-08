# EcoBoost: browser automation regression lab

## Incidentes medidos (8 de octubre de 2026)

| Caso | Medición | Interpretación |
| --- | --- | --- |
| Chromium headless de SEPE | Base ~145 MB, pico 376,1 MB en 4 procesos y regreso a ~145 MB; renderizador temporal ~45 s | Pico transitorio, no fuga demostrada |
| Chrome for Testing de LinkedIn | 13 procesos/~1,5 GB y después 7 procesos/~345 MB | Consumo activo seguido de descenso |
| Chrome for Testing de LinkedIn, segunda ventana | Pico ~2.180,9 MB con descenso posterior a ~144 MB | Verificar cierre por inactividad; no finalizar procesos del bot |
| Edge | Consumos elevados y fallos previos al intervenir extensiones/flags | No modificar indiscriminadamente ni perder perfiles o sesiones |
| D: | Historial de I/O saturado con pagefile, ADB y búsquedas | No escaneos masivos; preservar pagefile 4–8 GB, techo del usuario 10 GB |
| Emulador Android | Un emulador activo dejó de estarlo durante trabajos anteriores | Proteger QEMU, emulator y ADB; causa no determinada |

## Nueva funcionalidad

Acción: browsers.automation.audit

Categorías separadas: PLAYWRIGHT_CHROME, PLAYWRIGHT_HEADLESS, EDGE, OTHER_CHROME.

La atribución LINKEDIN_BOT / SEPE_AGENT solo se marca CONFIRMED si se identifica su proceso padre en ejecución sin reutilización de PID. De lo contrario UNKNOWN.

Los working sets sumados pueden contar memoria compartida más de una vez. PrivateBytes mide compromiso privado, no RAM exclusiva.

Hasta 60 capturas agregadas, sin persistir argumentos, rutas completas, sesiones, cookies ni IDs de proceso. Un pico aislado nunca se etiqueta como fuga. La revisión de consumo sostenido exige al menos tres muestras con memoria privada superior a 1,5 GiB en un intervalo de dos minutos como mínimo. La caída importante del consumo se clasifica TRANSIENT_RECOVERED.

## Cómo proceder si se repite

1. Volver a medir y comprobar descenso después de la actividad.
2. Revisar Edge mediante el análisis existente y, solo si procede, aplicar su optimización reversible con confirmación.
3. Revisar residuos de laboratorio mediante Process Hygiene; no confundir Playwright, SEPE, ADB o emulador con procesos huérfanos.
4. Incluir el monitor en el lote READ_ONLY_IDLE opcional. La política permanece OFF por defecto y nunca permite WRITE silencioso.

No cerrar Chrome del bot por RAM. Si persiste tras el idle, investigar en el bot la reutilización de páginas/contexts y el cierre por inactividad de 45 segundos.

## Regresiones permanentes

- SEPE 376 -> 145 MB: recuperación, no fuga.
- LinkedIn 2180 -> 144 MB: recuperación, no fuga.
- Consumo privado sostenido: exige tres muestras separadas por al menos dos minutos.
- Chrome/Edge/Headless/Node/PM2/Python/Cloudflared/ADB/QEMU protegidos del MemoryTrim/EcoQoS genérico.
- Ninguna medición abre Chrome, mata procesos o cambia Windows.
