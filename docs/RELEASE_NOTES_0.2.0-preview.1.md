# Win11 Performance Control Center — v0.2.0-preview.1

## Implemented
- Preserved Windows desktop host (WPF/.NET 10/WebView2), Compact/Developer UI, original icons, typed allowlist and per-action security boundary.
- Moved frontend types, local and simulated providers, and module configuration out of the original monolithic app.ts.
- Generated action metadata from the live C# ActionCatalog and generated TypeScript ActionDefinition/enums from C# model declarations. No independently maintained list of 89 actions.
- Bundled modular TypeScript into a single compatible web/app.js via esbuild; Node and esbuild are build-only dependencies.
- Split ActionExecutor into partial class sections without changing dispatch, validation, interlocks or rollback semantics.
- Preserved results across navigation, invalidated old cleanup previews after each WRITE attempt and labelled DEMO evidence correctly.
- Improved 1366px Compact RAM legibility; Developer still shows used and total RAM.

## Verification and safety
- Canonical gate: scripts/verify.ps1, including catalog drift/duplicate protection, headless browser smoke, 247 .NET tests and 33 incident regression evaluations.
- Visual captures: artifacts/visual-qa/compact-1366x768.png, developer-1366x768.png, compact-1648x927.png and developer-1648x927.png.
- Packaging: scripts/build-local.ps1 -> artifacts/local-win-x64 and artifacts/Win11PerformanceControlCenter-win-x64.zip.
- Verified 2026-10-10: VERIFY_OK, Release 0 warnings/0 errors; xUnit 247/247, incident evals 33/33, UI smoke 65 module actions and 3 simulated WRITE flows; NuGet/npm production audits reported no vulnerabilities.
- Packaged build: BUILD_LOCAL=PASS; 33 files, 3.40 MiB deployed, 1.49 MiB ZIP; SHA-256 `E6A3A71E9074CECC4A8F9DFC9118B37B20213E684BD00DEAB93DF31E8E641053` (worktree package).
- Real-host nonadmin READ bridge smoke (no UAC/WRITE): system.workload.status, memory.analyze, network.test and thermal.audit all returned actionSuccess=True. These four checks do not certify the remaining actions.
- Always distinguish a declared or simulated action from a physically verified Windows operation. 89 actions remain catalogued (51 READ, 3 DRY_RUN, 35 WRITE), not all real-host certified.
- Do not conduct unattended destructive WRITE/UAC tests on the user's machine; use a disposable VM and manual UAC/firmware/hardware verification.

## Non-goals and remaining validation
- No Electron, background server, cloud dependence or arbitrary command execution through the UI.
- No automatic clean-up of browsers, bots, emulators, personal files or old incident evidence.
- Enterprise policies, physical mixed-DPI multi-monitor sessions, NVDA/Narrator and vendor-specific hardware/UAC scenarios require a dedicated test environment.
- GitHub push/release publication requires explicit user authorization; local build and Git commit do not publish the project.
