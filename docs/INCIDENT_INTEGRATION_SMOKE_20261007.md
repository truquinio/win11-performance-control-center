# Incident: integration smoke hang — 2026-10-07

## Evidence (confirmed)
- 2026-10-07 16:41 local: VSTest hang dump `dotnet_14916_20261007T164127_hangdump.dmp` (original 235.65 MiB; preserved, SHA-256-verified as a compressed archive).
- VSTest sequence XML identifies `IntegrationSmokeTests.NonAdminReadAndPreviewActions_CompleteThroughTypedBridge` as the only unfinished test.
- The archived dump was analyzed locally on 2026-10-08 with Microsoft `dotnet-dump` 10.0.750501 (commands `clrthreads`, `clrstack -all`, `dumpasync`).
- The active worker thread stack shows `Interop.Kernel32.GetFileAttributesExPrivate` → `System.IO.FileSystem.FillAttributeInfo` → `StorageAnalysisService.CountAllBytes` → `RankLocalAsync`.
- Async task chain connects `ActionExecutor.AppDataRankAsync` → `ExecuteAllowedAsync` → `HostBridge.HandleAsync` → `IntegrationSmokeTests`.
- The specific action was `disk.appdata.rank`: a recursive, read-only AppData folder-size ranking, not the bot, Edge, the GPU, or a disk repair.

## Root cause and scope
- The historical implementation could recursively enumerate the host's actual `AppData\Local` from the integration smoke test and had no explicit per-action time budget in the test.
- At dump capture it was waiting in a filesystem metadata query. The exact entry that delayed the call, and whether the storage driver or another process caused additional I/O latency, cannot be determined from this dump alone.
- This was a test-design/runtime-boundary failure, not evidence of lost files, corrupt Windows, or an SSD hardware defect.
- This trace establishes the operation being executed at capture; it does not prove that the same I/O call was blocked forever.

## Previously implemented remedies
1. `95815ff` (2026-10-07 17:47): `AppDataRankBudget = 8s`, stopwatches with partial/incomplete result; `IntegrationSmokeTests` per-action `WaitAsync(15s)` and action-specific diagnostic messages.
2. `dc16961` (2026-10-07 18:37): test `HostBridge.CreateDefault(dataRoot)` uses `StorageFixture` and redirects storage analysis away from real AppData for the test.
3. `58e6abe` (2026-10-08 01:13): the smoke test excludes read actions requiring parameters when none are supplied.
4. Underlying Windows file metadata calls cannot always be forcibly canceled: an elapsed stopwatch budget is cooperative. The watchdog may return while a blocked background operation finishes independently. Avoid presenting the bound as a hard OS-level I/O interrupt.

## Verification on 2026-10-08
- Canonical `scripts/verify.ps1`: `VERIFY_OK`, exit 0 (finished 20:49:44 +02:00).
- Release build: 0 warnings, 0 errors.
- All .NET tests: 231 passed, 0 failed, 0 skipped (9 seconds).
- Real-world evals: passed as part of the canonical gate.
- UI smoke: passed, 63 actions exercised in demo (not writes on the real machine).
- NuGet vulnerable packages: none reported; npm production audit: 0 vulnerabilities.
- `IntegrationSmokeTests` was included and passed in the complete suite.
- The same integration smoke was run in isolation three additional times after the full suite: **3/3 passed**, with reported test durations of 8s, 7s and 8s.
- Forensic temporary tools and the extracted dump (279.1 MiB) were deleted after analysis; the hash-verified ZIP evidence was preserved.

## Operations and retention
- The forensic trace, extracted dump and debugger installation were temporary under ignored `artifacts/forensics/` and have been deleted after the sanitized report was prepared. Do not publish raw dump or memory trace: they can contain private process memory and paths.
- Preserve the verified compressed dump until this historical incident's evidence is no longer needed.
- Only sanitized call stacks and conclusions belong in repository documentation.
