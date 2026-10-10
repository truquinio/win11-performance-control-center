# Win11 Performance Control Center — v0.2.0-preview.1

## Implemented
- Preserved Windows desktop host (WPF/.NET 10/WebView2), Compact/Developer UI, original icons, typed allowlist and per-action security boundary.
- Moved frontend types, local and simulated providers, and module configuration out of the original monolithic app.ts.
- Generated action metadata from the live C# ActionCatalog and generated TypeScript ActionDefinition/enums from C# model declarations. No independent manually maintained list of 87 actions.
- Bundled modular TypeScript into a single compatible web/app.js via esbuild; Node and esbuild are build-only dependencies.
- Split ActionExecutor into partial class sections without changing dispatch, validation, interlocks or rollback semantics.
- Preserved results across navigation, invalidated old cleanup previews after each WRITE attempt and labelled DEMO evidence correctly.
- Improved 1366px Compact RAM legibility; Developer still shows used and total RAM.

## Verification and safety
- Canonical gate: scripts/verify.ps1, including contract drift/duplicate test, headless browser smoke, 231 .NET tests and 33 real-world incident regression evaluations.
- Visual captures: artifacts/visual-qa/compact-1366x768.png, developer-1366x768.png, compact-1648x927.png and developer-1648x927.png.
- Packaging: scripts/build-local.ps1 -> artifacts/local-win-x64 and artifacts/Win11PerformanceControlCenter-win-x64.zip.
- Verified 2026-10-10: VERIFY_OK, Release 0 warnings/0 errors; xUnit 231/231, incident evals 33/33, UI smoke 63 actions and 3 simulated WRITE follow-ups; NuGet/npm production audits reported no vulnerabilities.
- Packaged build: BUILD_LOCAL=PASS; 33 files, 3.36 MiB deployed, 1.47 MiB ZIP; SHA-256 `3FA30E1F904C0D9C1AC28C65C543AFE8002DC31F215C427D202E3D74F1AF151E` (worktree package).
- Real-host nonadmin READ bridge smoke (no UAC/WRITE): system.workload.status, memory.analyze, network.test and thermal.audit all returned actionSuccess=True. These four checks do not certify the remaining actions.
- Always distinguish a declared or simulated action from a physically verified Windows operation. 87 actions remain catalogued, not all real-host certified.
- Do not conduct unattended destructive WRITE/UAC tests on the user's machine; use a disposable VM and manual UAC/firmware/hardware verification.

## Non-goals and remaining validation
- No Electron, background server, cloud dependence or arbitrary command execution through the UI.
- No automatic clean-up of browsers, bots, emulators, personal files or old incident evidence.
- Enterprise policies, physical mixed-DPI multi-monitor sessions, NVDA/Narrator and vendor-specific hardware/UAC scenarios require a dedicated test environment.
- GitHub push/release publication requires explicit user authorization; local build and Git commit do not publish the project.
