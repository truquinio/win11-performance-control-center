# Windows isolated WRITE/UAC certification — execution plan

## Safety and eligibility
- User approved preparation of an **isolated Windows test environment** on 2026-10-11. This is NOT authorization to run system-changing actions on the daily-use Dell G7 7588.
- First command on any candidate host: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/vm-qa-preflight.ps1`. It only reads OS, tools, free space and available memory.
- A disposable Windows 11 guest, with a clean snapshot and explicit guest access, is required. For a dedicated external guest target at least 6 GiB RAM and 64 GiB virtual disk (sizing recommendations, not a claim of provisioned capacity). Do not use personal user accounts or install unrelated software.
- Confirm console/remote control access, snapshot creation and restore, installation/app startup, and time-limited guest credentials before starting tests. Do not send credentials into logs.
- On the current host: Windows 11 Home; Hyper-V VM management and Windows Sandbox are not available as a confirmed supported runner; an elevated feature query is not available. Free resources are insufficient for a safely isolated guest alongside existing active work. **Do not enable virtualization features, reboot, install a VM hypervisor or exhaust RAM/disk on this machine.**
- WSL2 and an Android emulator are not substitutes for real Windows UAC, SCM, Explorer, drivers, registry or recovery behavior.

## Mandatory per-action evidence
Use `src/Frontend/generated/action-catalog.json` generated from C# `ActionCatalog.All` as the sole authoritative catalog. Before visiting the guest, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/vm-qa-manifest.ps1` to generate a **pending-only** JSON checklist from that catalog (currently 35 WRITE, 16 require elevation, 15 claim reversibility). This generator never executes an action or claims certification. Record independently for each:
1. Available and correctly named in UI, with documented parameters/risk; backend allowlist checks parameter validation and privileged boundary.
2. Denied/invalid confirmation, denied UAC, missing privileges, concurrency rejection and user cancellation cause NO unexpected host changes; journal/error status consistent.
3. Prepared guest snapshot, baseline inventory, manual approval and explicit `WRITE` execution inside disposable guest only.
4. Measure intended vs actual system change and verify postconditions; test partial failure, restore/rollback for reversible actions, reboot-needed states, and correct user-visible uncertainty where rollback is not guaranteed.
5. Recover to the clean snapshot after *every* destructive scenario; compare baseline fingerprints, logs and rollback artifacts; note unrecoverable side effects separately. Do not assume `Success=false` proves the action had no effects.
6. Save timestamps, action ID, guest build, test mode, result, snapshot identifier, before/after hashes and evidence references without sensitive personal data.

## Gate to final release
- Complete all 35 WRITE rows with PASS, SKIP only for deliberately unsupported functions with clear scope, or BLOCKED with reason; no invented pass.
- Test negative/positive UAC, app restart, power loss/cancellation and exception recovery without touching the real workstation.
- Execute the canonical `scripts/verify.ps1`, app packaging and Windows UI smoke tests, physical DPI/high contrast/NVDA where possible and compare with the actual approved Developer reference.
- Verify ZIP and executable hashes from the exact reviewed commit. Release once, only after sign-off. Until then keep QA outputs local and call them not certified.

## Current resource assessment
Read-only assessment on Ephpatha returned `BLOCKED_FOR_LOCAL_VM`:
Windows 11 Home; 15.79 GiB total RAM, 4.63 GiB free at the time of measurement; 33.72 GiB free on D:; no confirmed Windows VM provider (Get-VM, VirtualBox, QEMU); WindowsSandbox.exe absent; process not elevated. An external disposable guest must be attached as another authorized Desktop Commander machine or provided through an existing accessible Windows VM. No cloud VM was created or purchased.

This file is a protocol, **not** evidence that VM operations were performed.
