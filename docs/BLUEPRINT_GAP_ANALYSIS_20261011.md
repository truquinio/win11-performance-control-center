# Win11 Control: comparison against "Win11 Control Center desde cero"

Source: user's nine-section rebuild blueprint. This is an engineering inventory, **not a release certificate**. Validate against current HEAD before declaring completion.

| Blueprint requirement | Status | Evidence / follow-up |
| --- | --- | --- |
| Keep WPF + .NET 10 + WebView2 + TypeScript, no Electron | Implemented | `src/App`, `src/Frontend`, `docs/ARCHITECTURE.md` |
| One allowlisted catalog, generated TS contracts, no duplicate action IDs | Implemented | **89 actions** (51 READ / 3 DRY_RUN / 35 WRITE); 49 parameter definitions; `scripts/catalog-contract.mjs` |
| Typed IPC, privilege boundaries and single operation coordinator | Implemented in code | `HostBridge`, `OperationCoordinator`, `PrivilegeBoundary`; verify destructive paths in isolated VM |
| Backend organized by responsibilities | Substantially implemented | Specialized services and partial executor; storage moved to `ActionExecutor.Storage.cs`, but core dispatcher still spans multiple domains |
| SQLite transactional state/history proposed in blueprint | Not implemented as specified | Existing local logs/state and rollback records use file-backed services and journals. Do not migrate to SQLite without a tested transaction/migration/backup design |
| Per-action cancellation and watchdog | Partially implemented / not certified | OperationCoordinator serializes actions and enforces read budgets; systematic cancellation and slow-I/O verification of each operation is still missing |
| Frontend modular, shared components/providers and local/demo parity | Substantially implemented | `components/action-results.ts`, `formatting.ts`, `result-primitives.ts`, `module-definitions.ts`; `app.ts` holds lifecycle, not 1,700-line result renderer |
| Every visible button functional | Partially verified | 65 module actions, 7 contextual paths, 3 DEMO WRITE scenarios tested; not all 89 real-host paths end-to-end certified |
| Detect / preview / confirm / execute / verify / record / rollback | Partial | Safety interlocks/preview/confirm/evidence in core. Physical WRITE postchecks and reversible rollback need controlled verification; Recycle Bin restores remain manual |
| DEMO fixtures / injected failures / real Windows READ | Partial | 247 .NET xUnit, 33 incident evals, DEMO fail-closed, degraded frontend and 8 real-host READs without elevation |
| Windows VM snapshot + UAC positive/negative + restart + cancellation | Blocked, not certified | Disposable authorized VM test environment and physical manual evidence required; must not run destructive tests on active work PC |
| Compact/Developer design consistent with approved mockups | Partial | Visual QA images at 1648x927 and 1366x768, original icon assets; checked-in Developer reference is obsolete, cannot certify pixel parity |
| DPI 100/125/150/200, multi-monitor, NVDA, high-contrast, keyboard | Partial | UI smoke checks accessible control names and tab order; physical environments/screen reader not certified |
| AppData stalls, bounded scans, recovery and isolated fixtures | Substantially implemented | `StorageForensicsService`, bounded work; regression tests include storage safety and recycle manifests |
| Profile CPU/RAM/handles/startup under load | Partially measured | Three short QA sessions now include WPF and only its descendant WebView2 processes (`docs/QA_2026-10-11.md`); real workload CPU/disk load, leak monitoring, shared RAM effects and a reproducible before/after baseline remain unverified |
| CI / reproducible packaging / hashes / release notes | Implemented for local QA | `.github/workflows/ci.yml`, `scripts/verify.ps1`, `scripts/build-local.ps1`; hashes vary per build |
| User-approved final publication | Not done, by instruction | No push, release, public artifact or declaration of 100% completeness |

Outstanding acceptance blockers: physical WRITE/UAC/recovery validation in disposable VM, reliable authoritative Developer mockup, DPI and screen-reader checks, actual application performance benchmarking. These are evidence gaps, not grounds to add fake results or change the catalog.

Never alter unrelated repositories, chat histories, user files or working browser/emulator processes. Preserve `docs/COORDINATION_NOTICE_STORAGE_20261010.md` untracked; coordinate before adding or changing it.
