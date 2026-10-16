# Ideas de referencias externas para Win11 Control — 14/10/2026

## Alcance
Revisión conceptual de los enlaces compartidos por el usuario. No se copiaron dependencias ni código externo. Las propuestas se subordinan a `AGENTS.md`, `docs/RELEASE_ACCEPTANCE_MATRIX_20261012.md` y a los gates de publicación.

## Ideas aplicables, ordenadas por valor y riesgo

### P0 — Trazabilidad compacta de cada recomendación
**Inspiración:** Prompt Master propone explicitar tarea, contexto, restricciones, criterios de éxito, fallos previos y memoria; además comprueba que el resultado esté acotado.  
**Aplicación:** asegurar que cada acción del catálogo presente en lenguaje claro: problema detectado, evidencia y fecha, riesgo/impacto, modo READ/DRY_RUN/WRITE, previsualización, confirmación necesaria, permisos, resultado/postcomprobación y recuperación disponible. Mostrar incertidumbre o dato faltante sin convertirlo en una instrucción de escritura.  
**Criterio:** contrato catálogo/backend/frontend alineado; controles con resultado observable; cobertura de accesibilidad; revisión de seguridad. Es mejora de claridad y auditabilidad, no un generador de prompts ni una función de IA.

### P1 — Informe visual accesible para soporte
**Inspiración:** capacidades multimodales de lectura de capturas/documentos en Qwen-MM-Plugins; MarkItDown convierte documentos a Markdown para indexación/lectura.
**Aplicación:** mejorar el flujo existente de capturas y bundles para que incorpore metadatos útiles (DPI, resolución, pantalla, modo, versión/hash), índice de evidencias y descripción textual accesible. Si se explora OCR o interpretación de imagen, primero como herramienta local opcional, invocada por el usuario, con salida revisable y sin acceso de red por defecto.
**Criterio:** pruebas sintéticas y de archivo controlado; probar alto contraste, DPI y teclado/NVDA; verificar redacción de datos sensibles y que no se incluyan secretos, sesiones, documentos ni contenido ajeno.

### P2 — Compresión determinista de diagnósticos largos
**Inspiración:** Headroom reduce redundancia en salidas JSON/logs antes de enviarlas a un modelo.
**Aplicación:** no integrar proxy ni middleware LLM. Añadir a los informes un resumen determinista y reproducible que agrupe eventos repetidos y conserve siempre los registros originales, rutas, códigos, tiempos y hashes; permitir abrir el detalle íntegro.
**Criterio:** mismo resumen para misma entrada; ninguna pérdida de evidencia ni falsos negativos; ahorro medido en bytes y legibilidad antes de plantear cualquier integración.

### P3 — Mejoras de proceso para cerrar el producto
**Inspiración:** Everything Claude Code y `one-skill-to-rule-them-all` describen guías de trabajo, memoria/revisión de patrones y habilidades.
**Aplicación:** adoptar solo prácticas ya coherentes con el repositorio: handoff breve aditivo, checklist con estado y evidencia, revisión segura antes de integrar, pruebas focales y control de duplicados. Mantener las reglas y documentación existentes como fuente de verdad; no instalar agentes, skills o telemetría automática.
**Criterio:** menos repetición y evidencia más fácil de auditar sin cambiar alcance ni reglas de seguridad.

## No incluir en el alcance actual

- **OmniRoute u otras pasarelas multi-modelo:** no resuelven un requisito del producto y abrirían envío de diagnóstico a terceros, gestión de claves, fallback y dependencias operativas.
- **MiroFish, Ruflo o enjambres de agentes:** añadirían servicios, memoria y complejidad; no sustituyen pruebas físicas, rollback ni certificación Windows.
- **Qwen-MM-Plugins como dependencia de la aplicación:** su caso principal es harness multimodal/servicios de modelos. El producto es una herramienta local de diagnóstico y reparación de Windows; una integración de nube no se justifica ahora.
- **MarkItDown como runtime obligatorio:** posible utilidad para un futuro importador optativo de informes, pero agrega runtime Python/dependencias a una app WPF/.NET ya existente. Priorizar los formatos locales y dependencias actuales.
- **Reescritura del sistema de prompts o memoria de proyecto:** los problemas actuales de cierre son evidencias de VM/UAC/rollback, QA visual/accesibilidad, pruebas prolongadas y conciliación Git.

## Prioridad real para terminar Win11 Control

1. Preservar cambios concurrentes y conciliar Git con los responsables; no integrar a ciegas.
2. Obtener VM desechable autorizada con snapshot para los 35 WRITE y documentar UAC, postcondición, interrupción y rollback.
3. Conseguir mockup Developer vigente y completar QA física de DPI 100/125/150/200%, NVDA, alto contraste, teclado y estabilidad prolongada.
4. Verificar el artefacto exacto, hashes, revisión humana y gate `release-readiness.ps1`; publicar solo con signoff y autorización.
5. Evaluar P0/P1 de esta nota únicamente después de asegurar o dentro del trabajo de aceptación, sin convertirlos en bloqueos nuevos.

## Fuentes revisadas

- [Prompt Master](https://github.com/nidhinjs/prompt-master) — estructura de prompts, contexto, criterios, límites y eficiencia.
- [Everything Claude Code](https://github.com/FaranMustafa/everything-claude-code) — prácticas y herramientas para harnesses de agentes.
- [OmniRoute](https://github.com/diegosouzapw/OmniRoute) — puerta de enlace y fallback multi-proveedor.
- [MiroFish](https://github.com/666ghj/MiroFish) — sistema de simulación multiagente.
- [Qwen-MM-Plugins](https://github.com/QwenLM/Qwen-MM-Plugins) — plugins multimodales para harnesses de agentes.
- [Qwen-MM-Plugins: resumen de Tools4All](https://tools4all.ai/trends/alibaba-qwen-mm-plugins-enable-multimodal-agents) — contexto secundario; se priorizó el repositorio del proveedor.
- [MarkItDown](https://github.com/microsoft/markitdown) — conversión de documentos a Markdown.
- [One Skill to Rule Them All](https://github.com/rebelytics/one-skill-to-rule-them-all) — captura de prácticas para revisión de skills.
- [Headroom](https://github.com/headroomlabs-ai/headroom) — compresión de contexto y salidas para agentes.
- [Ruflo](https://github.com/ruvnet/ruflo) — harness y coordinación de agentes.
- `https://kujira.so/`, `https://github.com/morluto/rea`, `https://github.com/OpenCut-app/OpenCut`, `https://claudearchitects.org/certification`, `https://runwayml.com/` — se identificaron en la lista compartida; no se propone dependencia ni se hizo una evaluación profunda porque no presentan una necesidad directa para los gates actuales.

## Registro de verificación

- Archivos fuente y control de aceptación consultados: `AGENTS.md`, `docs/RELEASE_ACCEPTANCE_MATRIX_20261012.md`, y anexos de continuidad de `docs/HANDOFF.md`.
- No se editaron fuentes, código, artefactos ni cambios Git concurrentes.
- Esta nota agrega propuestas; no marca ningún criterio de aceptación completado, no modifica el porcentaje/estado de certificación y no autoriza releases ni acciones WRITE.
