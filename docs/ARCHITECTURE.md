# Architecture — Win11 Performance Control Center

## Decision

The product uses a lightweight hybrid architecture:

`HTML/CSS/TypeScript UI → Provider/IPC → C# Core → Windows services/APIs`

The TypeScript frontend is modular: `contracts.ts`, `providers/local.ts`, `providers/demo.ts`, `module-definitions.ts`, and `components/{formatting,result-primitives,action-results}.ts` are bundled by esbuild into one `web/app.js`. The renderer receives its execution callback and the current catalog through explicit dependency injection; it has no second copy of ActionCatalog. `app.ts` owns the UI state and lifecycle rather than a 1,700-line action-results conditional. Action metadata originates from C# `ActionCatalog.All` via `tools/CatalogExporter`; TypeScript enums/record types are generated from `Models/Contracts.cs`. A contract test rejects drift in action IDs, parameters, action ordering and generated model types. The browser never compiles or evaluates backend C#.

`ActionExecutor` retains a single allowlisted dispatch boundary, with platform/startup, system/memory, and storage operations moved into separate partial class files (`ActionExecutor.Storage.cs` for storage/hibernation). The corresponding Windows services stay responsible for OS access. The executor does not expose a generic command endpoint.

The same frontend is used by both targets:

- **Local:** WPF + WebView2 + C# backend with real local data.
- **Static Demo:** browser-only build with explicitly simulated data.

Electron and bundled Chromium are intentionally excluded.

## Boundaries

### Frontend

The frontend renders state and requests typed operations. It does not execute Windows commands, manipulate the registry, enumerate files or decide privilege elevation.

### IPC bridge

`HostBridge` exposes a small method allowlist:

- `system.snapshot`
- `system.reliability`
- `actions.catalog`
- `actions.run`

Unknown IPC methods and unknown Action IDs are rejected.

### Core

`ActionCatalog` is the source of truth for executable capabilities. Each action has typed category, risk, connectivity requirement, privilege requirement, reversibility and mode.

`OperationCoordinator` prevents overlapping operations that could produce ambiguous measurements or conflicting writes.

`PrivilegeBoundary` validates per-action privilege requirements. The application is not designed to run permanently elevated.

### Windows services

Services collect real evidence through .NET/Win32/Event Log APIs. They return `NOT_EVALUATED` when a capability has not actually been measured instead of inventing a healthy status.

Current services cover system snapshot, reliability evidence, integrity, storage, memory/process tuning, pagefile, drivers, activation, browser and extension health, multimedia, startup, Windows Update, installed apps, privacy, developer tooling, thermal/energy, boot/sleep, Explorer and recovery state.

## Reliability evidence model

Reliability is evidence-first. Provider + Event ID pairs are validated before entering the timeline.

Kernel-Power 41 is an `INDICIO`, not a root-cause diagnosis. WHEA, application failures, update events and shutdown/boot events are reported as facts about what Windows recorded, without automatically claiming causality.

## Build model

Node.js and TypeScript are development dependencies only. They are not required by the installed application.

The local package is framework-dependent and reuses the installed .NET Windows Desktop Runtime and Edge WebView2 Runtime. This avoids shipping a private browser engine or full SDK.

Build outputs:

- `artifacts/local-win-x64` — framework-dependent Windows x64 application.
- `artifacts/Win11PerformanceControlCenter-win-x64.zip` — compressed local package.
- `artifacts/demo` — static shared frontend.
- `artifacts/visual-qa` — Compact/Developer visual QA captures.

## Safety invariants

- No arbitrary shell execution from frontend input.
- `disk.cleanup.safe` is `DRY_RUN`. The separate `disk.cleanup.execute` is a guarded `WRITE` with an explicit preview and confirmation; it only targets allowlisted, regenerable caches and excludes profiles, sessions and user documents.
- Other `WRITE` actions (e.g. EcoQoS, pagefile and Edge extension orphan quarantine) require their own parameter, privilege and interlock guards; rollback is available only where supported.
- Unsupported or inaccessible evidence sources degrade gracefully.
- Writes must be explicit Action Catalog entries with verification and rollback design where applicable.
- Logs are local, structured and rotated.
