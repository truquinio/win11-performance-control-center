# Control Loop v2

Win11 Performance Control Center aplica un ciclo operativo explícito:

**detectar → explicar → proponer → ejecutar → verificar → rollback**

## Plan de acción

`system.actionplan.preview` combina señales rápidas y de bajo impacto:

- presión de almacenamiento;
- RAM/CPU;
- estado de red;
- pagefile;
- servicios automáticos revisables;
- residuos de extensiones Edge;
- operaciones incompletas y rollbacks disponibles.

El plan no ejecuta WRITE en lote. Cada recomendación enlaza con una Action ID allowlisted y conserva las confirmaciones/UAC de esa acción.

## PC en uso

La app mantiene tres modos:

- **AUTO**: permite mantenimiento pesado solo tras suficiente inactividad.
- **IN_USE**: bloquea acciones pesadas o disruptivas mientras el usuario trabaja.
- **MAINTENANCE**: habilita explícitamente esas acciones.

D: por debajo del 3% libre bloquea mantenimiento pesado incluso en MAINTENANCE.

Entre las acciones protegidas se incluyen reparación DISM/SFC, limpieza real, reset Winsock, rescan de drivers, reinicio de audio/Windows Update/Explorer y scans de hotspots.

## Rollback Center

`backup.rollback.center` reúne estados creados por la propia app:

- EcoQoS;
- pagefile;
- plan de energía;
- StartMode de servicios;
- cuarentena de Edge.

El centro no inventa rollback para cambios que no lo soportan y nunca restaura sin confirmación.

## Tests

Los tests de control loop utilizan `dataRoot`/fixtures. El test de modo PC en uso comprueba que una acción pesada queda bloqueada antes de tocar el fixture.
