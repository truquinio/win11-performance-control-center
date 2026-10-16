---
name: win11-lean-qa
description: Ahorra tokens/cuotas al corregir, probar y empaquetar Win11 Performance Control Center mediante pruebas focales, evidencia breve y límites estrictos de Windows WRITE.
---

# Win11 lean QA

Aplicar **solo** en `Win11-repair` y sus worktrees autorizados; no reemplaza `AGENTS.md`.

1. Leer `git status --short --branch`, `AGENTS.md`, `docs/HANDOFF.md` **una vez**; después consultar solo archivos/hunks modificados. No volver a leer grandes historiales que no cambiaron.
2. Fijar una hipótesis concreta y contrato mínimo que falla (**RED**). Corregir lo mínimo y repetir el mismo contrato (**GREEN**). Ampliar tests al módulo afectado; `scripts/verify.ps1` **una sola vez** en cierre de integración.
3. Elegir el modelo más económico que resuelva el trabajo: ligero para localizar/editar/documentar, estándar para pruebas y bugs ordinarios, razonamiento alto solo para ambigüedad, arquitectura o seguridad. No declarar cuotas restantes sin datos del servicio. No solicitar auditorías redundantes a múltiples agentes.
4. Reducir I/O y contexto: git diff acotado, búsquedas con rutas, últimas líneas del log, hash+exit code+resultado. Evitar capturas repetidas, polling agresivo y volcar listas enteras cuando bastan fallos.
5. Si una herramienta o permiso bloquea una operación, registrarlo **una vez** y conservar lo logrado; no eludir el bloqueo ni consumir cuota con intentos idénticos.
6. Nunca ejecutar 35 Windows WRITE/UAC/rollback destructivos en Ephpatha. Solo VM desechable con snapshots comprobados y autorización. Capturas tablet solo de proceso de QA, sin tocar otras apps.
7. Responder como `HECHO | VALIDADO (comando/exit) | BLOQUEADO | SIGUIENTE` y actualizar `docs/HANDOFF.md` por adición, preservando todo original.
