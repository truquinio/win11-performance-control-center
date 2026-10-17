# Outcome audit — isolated fail-closed worktree (11 October 2026)

Scope: `fix/outcome-evidence-failclosed`, worktree `D:\DOCS\AI\PROYECTOS IA\Win11-outcome-failclosed` based on `f0277c3`. The canonical main worktree had overlapping staged and unstaged changes authored concurrently; **do not merge/cherry-pick/reset without coordinating**.

## Reproduced and fixed
- `OutcomeAuditService` was ignoring malformed JSONL, non-object JSON, missing required fields, inaccessible journals and failures to enumerate rotated journals. It could report zero incomplete executions although a WRITE terminal record was missing.
- Added seven deterministic fixture cases: six invalid current-log records and one invalid rotated log. Red: `artifacts/outcome-audit-red.log` **0/7**, with actual assertion failures. Green: `artifacts/outcome-audit-final-focused.log` **7/7**.
- `Analyze()` now fails closed with an `IOException` containing `Outcome audit unverified`, the journal basename and line number for malformed records. It **does not delete or repair logs**, infer terminal statuses or call Windows WRITE actions. File/permission errors are no longer silently ignored.
- `artifacts/outcome-audit-final-suite.log`: **255/255 xUnit PASS** on this isolated HEAD baseline; `artifacts/outcome-audit-release-build.log`: Release with **0 errors and 0 warnings**. The main worktree meanwhile has independent uncommitted changes and more recent tests; those are not included in these counts.

## Integration caution
- App-level diagnostics bundle currently invokes outcome audit before exporting JSON, so a damaged journal may prevent diagnostic bundle creation. This should be resolved by reporting an explicit UNVERIFIED section while still exporting sanitized diagnostics, **without treating corrupted recovery state as verified**. Do not silently catch this failure and show success.
- Do not alter the concurrent `OperationRecoveryService` / `AppLogger` / `ActionExecutor` or their tests; the primary integrator must rebase/reconcile changes and rerun current canonical gate.
- No real Windows changes, VM UAC tests, release, push or packaging were performed.

## Diagnostic bundle regression closed (isolated branch only)
- Reproduced: `artifacts/outcome-bundle-red.log`, diagnostic action with synthetic malformed rotated JSONL failed instead of generating a troubleshooting ZIP.
- Fixed **only** `src/App/Services/DiagnosticBundleService.cs`: an unreadable audit/rollback journal yields `verificationStatus=UNVERIFIED`, null counts instead of invented zeros, and sanitized bundle creation continues. Healthy audit uses `AUDIT_READABLE`, not a claim that physical WRITE actions passed.
- `tests/App.Tests/OutcomeAuditCorruptionTests.cs` includes one additional end-to-end synthetic IPC fixture, which opens the ZIP and verifies that the `outcomes.json` state is `UNVERIFIED` and its `coveragePercent` is null.
- Green `artifacts/outcome-bundle-green.log`: 1/1; `artifacts/outcome-bundle-full-suite.log`: **256/256 .NET**; `artifacts/outcome-bundle-release-build.log`: **0 errors and 0 warnings**.
- The primary worktree is still concurrently modified and has not been integrated, full canonical verify/UI and physical UAC/WRITE certification are **not claimed**. See coordination notice on main. No real host destructive actions were run.

## Adenda de regresión — 11/10/2026, validación de `operationId` y estados terminales
- Se añadió `OutcomeAudit_NonStringOperationIdFailsClosed` con cinco variantes explícitas de `operationId` JSON no-string: número, booleano, `null`, objeto y array. Línea inválida debe producir `IOException` de auditoría `unverified` con nombre del diario y línea; `operationId` ausente sigue siendo compatible con el formato legacy.
- **RED observado:** `dotnet test tests/App.Tests/App.Tests.csproj --no-restore --filter FullyQualifiedName~OutcomeAudit_NonStringOperationIdFailsClosed --verbosity quiet` desde este worktree: **0/5, exit 1**, fallos de aserción de excepción esperada.
- **GREEN observado tras modificación mínima:** misma orden, **5/5, exit 0**. `OutcomeAuditService` rechaza propiedad `operationId` presente cuyo valor no es string; no altera ni elimina diarios.
- Se incorporó `OutcomeAudit_FailedRejectedAndCancelledAreTerminal` con tres IDs independientes; cubre FAILED, REJECTED y CANCELLED y verifica que no cuentan como incompletos. **Limitación conocida del contrato actual:** CANCELLED se agrega al contador `RejectedRuns`; el informe no distingue ambas causas aunque el estado en el diario sí.
- Focal `--filter FullyQualifiedName~OutcomeAuditCorruptionTests`: **14/14, exit 0**. Suite completa `.NET` desde este worktree con `--no-restore --verbosity quiet`: **262/262, exit 0**. No equivale a gate integrado del `main` con cambios concurrentes.
- Archivos de esta rama cambiados: `src/App/Services/OutcomeAuditService.cs`, `tests/App.Tests/OutcomeAuditCorruptionTests.cs` y esta adenda documental. No se ejecutó WRITE físico/UAC; sin push, release, ni fusión a main.
