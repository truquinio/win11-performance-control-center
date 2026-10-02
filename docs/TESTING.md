# Testing and release gates

## One-command verification

    powershell -ExecutionPolicy Bypass -File scripts\verify.ps1

The script stops at the first failing step. GitHub Actions runs this same script, so local and CI results are comparable.

Gate, in order:
1. TypeScript/frontend build.
2. Frontend contract: views, buttons and Action IDs agree with the backend catalog; the UI action timeout outlives the backend budgets.
3. UI smoke in a headless Chromium/Edge: navigation, keyboard focus across the periodic refresh, accessible names and roles, results staying with the module that requested them, and a degraded start (first snapshot and catalog requests fail) that must recover on its own.
4. Package, resource, CI-parity and WebView security contracts.
5. Platform compatibility contract: PerMonitorV2, physical-pixel/DIP conversion, UAC/runas, current-user IPC, global-per-user single-instance semantics and WMI policy degradation.
6. Visual QA isolation contract.
7. Release build with warnings treated as errors.
8. xUnit suite: catalog and IPC validation, fault injection, concurrency, persistence and rollback, recovery, privilege boundary, window geometry at 100/125/150/200% and negative monitor coordinates, representative end-to-end cultures plus protocol serialization across every .NET specific culture.
9. Whitespace, code-style and analyzer verification.
10. NuGet vulnerability audit.
11. npm production audit.

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

These need a person, a second Windows session, enterprise policy, or extra hardware and are checked manually:
- Approving, cancelling and leaving open the interactive Windows UAC secure-desktop dialog, plus elevation with a different administrator account than the signed-in user. Automation verifies the runas boundary, current-user-only IPC and explicit policy-block errors but does not automate the secure desktop.
- Moving the window between physical monitors with different sizes and mixed 100/125/150/200% scale factors. Automated geometry covers those DPI factors, negative coordinates and small work areas, but one machine cannot prove every physical topology.
- Fast-user-switching/RDP with the same Windows user and with different users. The global-per-user mutex and same-session activation rules are automated; simultaneous independent desktop sessions still require a multi-session host.
- AppLocker/WDAC/Group Policy combinations that actually block WMI, the executable or the elevated helper. CI verifies fail-closed/degraded code paths and explicit policy errors; a real deny policy requires Windows Pro/Enterprise/domain/MDM infrastructure.
- A screen reader session (Narrator/NVDA) and Windows contrast themes.
- Hardware matrices without NVIDIA, without ACPI thermal zones, with multiple GPUs/audio endpoints, unusual firmware providers, or vendor-specific WMI restrictions beyond the degraded-path tests.
