# Architecture — Win11 Performance Control Center

## Decision

The product uses a lightweight hybrid architecture:

`HTML/CSS/TypeScript UI → Provider/IPC → C# Core → Windows services/APIs`

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
- No destructive cleanup in the current vertical slice.
- Safe Cleanup remains `DRY_RUN`.
- Unsupported or inaccessible evidence sources degrade gracefully.
- Writes must be explicit Action Catalog entries with verification and rollback design.
- Logs are local and structured.
