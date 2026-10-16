# Ideas de favoritos Edge aplicables a Win11 Control — 14/10/2026

## Resumen
Se revisaron 1.607 enlaces del listado de favoritos. Solo una fracción guarda relación con el Win11 Performance Control Center. La lista aporta sobre todo herramientas para **verificar** accesibilidad, DPI y capturas; no justifica añadir servicios de IA, telemetría ni dependencias al producto.

## Aprovechables ahora para terminar la aceptación

### 1. Accessibility Insights for Windows — prioridad alta
El sitio oficial de Microsoft indica que esta herramienta sirve para evaluar accesibilidad de aplicaciones de escritorio Windows, inspeccionar propiedades UI Automation, ejecutar FastPass y revisar contraste. Usarla en QA del binario candidato junto con las comprobaciones ya pendientes de teclado y NVDA; registrar versión, pantalla, escala, hallazgo y evidencia. No presentarla como sustituto del lector de pantalla o revisión humana.

Fuente: [Accessibility Insights](https://accessibilityinsights.io/).

### 2. NVDA — prioridad alta como prueba real
La página de NV Access describe NVDA como lector de pantalla gratuito para Windows. Es una herramienta de validación: probar orden de foco, nombres/roles/estados, mensajes de resultado, confirmaciones y navegación de Compact/Developer. No agregar NVDA al runtime ni marcar accesibilidad como aprobada hasta completar la prueba física.

Fuente: [NV Access / NVDA](https://www.nvaccess.org/).

### 3. Responsively App — uso limitado para el frontend WebView2
Su función documentada es mostrar múltiples perfiles/tamaños y capturar páginas web en paralelo. Puede ayudar a detectar rápidamente problemas de composición en el frontend, pero no simula de forma equivalente el escalado DPI de WPF, el monitor físico, UI Automation de escritorio ni el entorno real de WebView2. Usarla como preflight visual opcional y conservar los ensayos físicos requeridos.

Fuente: [Responsively App](https://responsively.app/).

### 4. Squoosh / TinyPNG / Compressor.io — preparación de capturas, no del runtime
Herramientas de compresión pueden reducir el peso de capturas de documentación si la calidad visual, metadatos y resolución siguen siendo suficientes. Como las capturas pueden revelar datos del equipo, preferir procesamiento local y revisión/redacción antes de subir material a un servicio web. No comprimir ni reemplazar evidencia original; trabajar sobre una copia derivada y conservar el original/hash.

Fuentes guardadas en favoritos: [Squoosh](https://squoosh.app/), [TinyPNG](https://tinypng.com/), [Compressor.io](https://compressor.io/).

### 5. MarkItDown — posible importación futura de documentación
Convierte documentos/Office a Markdown y podría ayudar a importar material de soporte. No hace falta para los criterios actuales ni conviene sumar Python al runtime WPF/.NET. Considerarlo solo si aparece un caso de importación local concreto, optativo y con pruebas de privacidad/formato.

Fuente: [Microsoft MarkItDown](https://github.com/microsoft/markitdown).

## Útiles para el proceso de desarrollo

- [Visualizing Git](https://git-school.github.io/visualizing-git/) y [Learn Git Branching](https://learngitbranching.js.org/?locale=es_ES): apoyo didáctico para entender ramas/commits. No resuelven automáticamente la conciliación actual; esa sigue requiriendo preservar cambios y coordinar responsables.
- [GitHub Flavored Markdown task lists](https://github.github.com/gfm/#task-list-items-extension-): sirve para checklists legibles. Mantener en todo caso la evidencia y el historial aditivo que exige el proyecto.
- [Excalidraw](https://excalidraw.com/): podría ayudar a dibujar flujos de confirmación/rollback; solo si aclara revisión de seguridad o onboarding, sin reemplazar contratos ni diagramas de arquitectura vigentes.
- Recursos de iconos, paletas, mockups y generadores CSS pueden servir para preparar una referencia aprobada; no copiar assets de procedencia/licencia dudosa ni rehacer la UI por estética mientras falte el mockup Developer vigente.

## No recomendados para incluir ahora

- OmniRoute, OpenRouter, Groq, Ollama u otros gateways/modelos: no resuelven los gates pendientes y podrían enviar diagnósticos a terceros, requerir credenciales y añadir rutas de fallback.
- MiroFish, Ruflo, Hermes Agent y otros frameworks/agentes: más servicios y coordinación sin reemplazar las pruebas de 35 acciones WRITE, UAC, snapshot/rollback ni aprobación de publicación.
- Plataformas de generación de imágenes/vídeo, editores online, hosting, bases de datos y herramientas de sitios web: fuera del producto y de sus criterios de aceptación.
- Extensiones de navegador para inspección comercial, scraping o automatización: no necesarias; evitar instalar o ejecutar extensiones de orígenes no verificados en el perfil Edge del usuario.
- Cursos, blogs genéricos y tutoriales antiguos de CSS/Java/WordPress: pueden ser material de aprendizaje, no una dependencia técnica ni fuente para modificar el producto sin validar vigencia y autoridad.

## Orden para cerrar Win11 Control

1. No añadir alcance funcional por la lista de favoritos.
2. Preservar los cambios concurrentes; obtener responsables/ventana y conciliar Git sin sobrescribir.
3. Conseguir VM desechable autorizada con snapshot y cubrir cada WRITE con UAC, postcondición, interrupción y rollback.
4. Usar Accessibility Insights y NVDA para QA real; completar teclado/contraste y DPI 100/125/150/200% en el dispositivo físico.
5. Ejecutar pruebas visuales rápidas del frontend solo como preflight; conservar mockup aprobado y capturas físicas.
6. Verificar el artefacto exacto, hashes, revisión humana y `release-readiness.ps1`; publicar solo con signoff y autorización.

## Límites de esta revisión

- El listado local es la fuente para identificar favoritos y conserva sus URLs. Se leyeron los apartados relevantes de IA, desarrollo/frontend, pruebas visuales, OCR/documentos y Git.
- Se verificó en páginas oficiales la función general de Accessibility Insights, NVDA, Responsively App y Microsoft MarkItDown. Las otras herramientas se clasificaron por su nombre/categoría del favorito, no se hizo una evaluación exhaustiva de seguridad, privacidad, licencia, versiones ni compatibilidad.
- No se instaló software ni se subieron capturas. No se modificó código, mockups, fuentes ni artefactos existentes.
- Este documento es una propuesta aditiva; no cambia el estado de certificación ni marca criterios como completados.
