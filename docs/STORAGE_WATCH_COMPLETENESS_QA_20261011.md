# Hotspot scan completeness regression — 2026-10-11

Scope: Win11 Performance Control Center only; isolated worktree `Win11-storage-watch-safety`, branch `fix/storage-watch-completeness` from canonical `f0277c3`. This is a focused compatibility fix, **not** a final release or physical destructive-test certificate.

## Defect reproduced

`StorageWatchService.ScanVolume()` previously enumerated `MaxTopLevelFolders = 16` using `Take(16)` and then reported `Complete = true` whenever those 16 directories were measured before the time budget. A 17th directory was silently omitted, so a bounded low-disk hotspot result could misleadingly claim full coverage.

A Windows filesystem fixture under isolated `TestDataRoot` with 17 top-level directories reproduced it:
`artifacts/storage-watch-cap-red.log`: `HotspotScan_CappedTopLevelEnumerationMustBeMarkedIncomplete` **FAIL 0/1** before the fix.

## Minimal correction

A bounded one-item lookahead, `Take(MaxTopLevelFolders + 1)`, detects additional directories without a full disk traversal. If the 17th exists, mark `StorageHotspotVolume.Complete = false`; still analyze no more than 16 directories. `StorageHotspotReport.Partial` propagates this status.

Added fixture for exactly 16 directories to ensure this case remains complete.

## Verification

- `artifacts/storage-watch-cap-green.log`: **6/6 StorageWatchTests PASS**, after the fix.
- `artifacts/storage-watch-full-dotnet.log`: **250/250 .NET tests PASS** on the isolated branch.
- `artifacts/verify-storage-watch-safety.log`: **VERIFY_OK**; full gate includes **250/250 .NET tests**, **33/33 incident evals**, Release with 0 warnings/errors, platform/frontend/UI checks and NuGet/npm checks reporting 0 vulnerable packages.
- All created folders belong to synthetic temporary test fixtures; no real user storage scanning, cleanup, moving files, changes to Windows or other projects.
- Production main remains intentionally untouched because it contains extensive staged/unstaged work from other agents and coordination notices. Do **not** cherry-pick, merge, reset or discard anything until the owner reconciles changes.

Outstanding final release gates remain physical Windows VM/UAC/WRITE recovery, Developer-approved reference, real DPI/NVDA, performance and full current-main verification.
