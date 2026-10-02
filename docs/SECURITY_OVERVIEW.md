# Seguridad — resumen público

La versión completa del proyecto se diseñó alrededor de una regla básica: **diagnosticar con el mínimo privilegio posible y elevar sólo acciones concretas**.

## Principios publicados

- diagnóstico sin elevación por defecto;
- acciones allowlisted/identificadas;
- parámetros estructurados;
- confirmación previa para operaciones sensibles;
- UAC puntual;
- ausencia de un canal de comandos arbitrarios desde la UI;
- separación de acciones read-only y acciones con efectos;
- rollback/recovery cuando la operación lo permite.

Los detalles operativos, contratos internos y mecanismos completos de ejecución no forman parte de la edición pública de portfolio.
