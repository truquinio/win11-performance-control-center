# Real-World Reliability

Win11 Performance Control Center keeps two different quality signals:

1. Software health — unit/integration/fault/UI tests.
2. Real-world reliability — disposable scenarios that grade final system state.

The second layer exists because a green method return is not proof that Windows or a dependent application still works.

## Initial gate

The first historical regression is the Claude/Windows-MCP cleanup incident. It verifies that cleanup can remove an eligible old cache file while preserving:

- Claude Extensions
- Windows-MCP runtime files
- .venv
- WinGet installed packages
- WhatsApp state
- recent cache files

The executable grader uses a temporary fixture root only. It never points the destructive evaluation at the real user profile.

Additional gates cover:

- app-in-use cleanup refusal;
- old-vs-recent cache behavior;
- cleanup idempotency;
- locked-file preservation/reporting;
- fail-closed rejection of unknown broad production targets.

## CI

scripts/verify.ps1 executes:

- manifest contract validation;
- a separate Layer=RealWorldEval xUnit pass;
- the complete test suite afterward.

A confirmed user-visible bug should become a regression manifest or equivalent executable outcome test before the fix is considered complete.
