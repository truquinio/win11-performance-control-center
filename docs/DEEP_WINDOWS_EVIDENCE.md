# Deep Windows Evidence v4

This layer expands diagnostics without turning the application into a registry cleaner or an unsafe system tweaker.

## Scheduled tasks

`startup.tasks.preview` enumerates scheduled tasks and classifies them fail-closed.

Protected by default:

- every task below `\Microsoft\`;
- a task that is currently running;
- Windows/security/cloud/AI/automation tasks;
- PM2, Node, Python, NSSM, OpenAI, ChatGPT, Claude, Desktop Commander, MCP and Ollama;
- tasks whose executable cannot be resolved safely;
- tasks whose executable is under Windows;
- stale tasks whose target executable no longer exists.

`startup.task.disable` writes an app-owned snapshot before calling `schtasks /Change /Disable`, then verifies the state. It never terminates an already-running process.

`startup.task.restore` enables only a task previously disabled by this app and verifies the result. If the task no longer exists, the app does not recreate it from partial metadata.

## Service dependencies

Automatic-service preview now includes dependent-service evidence.

A service with dependents is protected from startup-mode changes. If WMI cannot determine the dependency graph, the classification fails closed instead of assuming that no dependencies exist.

`startup.service.dependencies` exposes the dependency list read-only.

## Registry evidence

`system.registry.evidence` reads a bounded set of high-signal locations:

- AppInit DLL configuration;
- Winlogon Shell/Userinit;
- AppCertDlls;
- IFEO Debugger values.

The action reports evidence only. It never deletes values and is intentionally not a generic registry cleaner.

## COM evidence

`system.com.evidence` performs a bounded scan of 32-bit and 64-bit CLSID registrations and reports local InprocServer32/LocalServer32 paths that point to missing files.

The scan is read-only, capped, and marked partial when the registry contains more classes than the budget. No COM registration is removed automatically.

## Certificate evidence

`system.certificates.audit` opens only the personal certificate stores read-only and reports certificates that are expired, not yet valid, or expire within 30 days.

It does not export private keys and never deletes certificates.

## Sanitized diagnostic bundle

`diagnostics.bundle.create` creates a local ZIP under the app's Exports directory and retains at most 10 bundles.

The bundle contains 10 JSON files covering system, storage, drivers/USB, reliability, startup, health history, outcome coverage, Windows evidence summaries and recovery.

It deliberately excludes:

- Run/RunOnce commands;
- scheduled-task executable paths and arguments;
- PnP and USB instance IDs;
- product keys;
- cookies and sessions;
- raw application logs;
- raw registry/COM/certificate details.

The evidence file contains only summary counts by status. The bundle is local-only and is never uploaded automatically.
