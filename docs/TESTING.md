# Testing and release gates

## One-command verification

    powershell -ExecutionPolicy Bypass -File scripts\verify.ps1

The script stops at the first failing step. GitHub Actions runs this same script, so local and CI results are comparable.

Gate, in order:
1. TypeScript/frontend build.
2. Frontend contract: views, buttons and Action IDs agree with the backend catalog; the UI action timeout outlives the backend budgets.
3. UI smoke in a headless Chromium/Edge: navigation, keyboard focus across the periodic refresh, accessible names and roles, results staying with the module that requested them, and a degraded start (first snapshot and catalog requests fail) that must recover on its own.
4. Package, resource, CI-parity and WebView security contracts.
5. Visual QA isolation contract.
6. Release build with warnings treated as errors.
7. xUnit suite: catalog and IPC validation, fault injection, concurrency, persistence and rollback, recovery, privilege boundary, window geometry across DPI and monitor layouts, culture matrix.
8. Whitespace, code-style and analyzer verification.
9. NuGet vulnerability audit.
10. npm production audit.

The current test count is whatever the CI badge run reports; it is not maintained by hand in this file.

## Visual QA

    powershell -ExecutionPolicy Bypass -File scripts\visual-qa.ps1

Canonical target: 1648×927 in Compact and Developer modes. Additional manual/headless checks cover 1366×768 and high-DPI layouts.

## Local package

    powershell -ExecutionPolicy Bypass -File scripts\build-local.ps1

Outputs:
- `artifacts/local-win-x64/`
- `artifacts/Win11PerformanceControlCenter-win-x64.zip`

During QA, launch without stealing focus:

    powershell -ExecutionPolicy Bypass -File scripts\start-local-minimized.ps1

## What automation does not cover

These need a person or extra hardware and are checked manually:
- Approving the interactive Windows UAC dialog, and elevation with a different administrator account than the signed-in user.
- Moving the window between physical monitors with different sizes or scale factors.
- A screen reader session (Narrator/NVDA) and Windows contrast themes.
- Machines without NVIDIA drivers, without ACPI thermal zones, or with restricted WMI/event-log policies beyond what the degraded-path tests simulate.
