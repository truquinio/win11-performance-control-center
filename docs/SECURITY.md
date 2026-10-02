# Security model

## Principle
The application runs as a standard user by default. Privilege is requested only for an allowlisted action that explicitly requires it.

## IPC boundary
The WebView2 frontend can call only:
- `system.snapshot`
- `system.reliability`
- `actions.catalog`
- `actions.run`

Unknown methods, unknown Action IDs and unknown parameters are rejected. The frontend cannot submit arbitrary PowerShell, cmd.exe or shell text.

## Action safety
Every action declares:
- risk,
- connectivity requirement,
- admin requirement,
- reversibility,
- mode: `READ`, `DRY_RUN` or `WRITE`,
- typed parameters when needed.

WRITE operations require explicit user confirmation. EcoQoS stores process-instance rollback state and validates process start time so a reused PID cannot inherit an old rollback snapshot.

## UAC
The main process is not always-admin. `ElevatedActionClient` starts a constrained helper through Windows `runas`; the helper revalidates the Action Catalog and its own elevation, runs only the requested read-only action and returns the result over a one-shot, current-user-only named pipe whose name is derived from a random token. Parameterised actions are never forwarded to the elevated helper. The interactive Windows consent dialog is intentionally not automated.

## Files and privacy
- Safe Cleanup is DRY_RUN in this release candidate.
- Browser/extension health is read-only.
- Orphaned extension data is reported, never deleted automatically.
- Logs are local JSONL and are rotated only inside the application's own log directory.
- A WRITE action is refused when its audit record cannot be written first; read-only diagnostics keep working without the log.
- EcoQoS rollback snapshots are persisted (write, flush, atomic rename) before a process is touched. If the snapshot cannot be saved, the process is left unchanged.
- External tools are started from fixed system locations (`dism.exe`, `nvidia-smi.exe`), never resolved through the working directory or `PATH`.
- Only one UI instance runs per session, so rollback state has a single writer.
- No telemetry or CDN is required for local operation.
