# Win11 Control — 100% acceptance matrix (12 Oct 2026)

**Current classification: NOT FULLY CERTIFIED.** These three axes are acceptance gates, not arbitrary percentages. No item may be marked 100% based on static action presence, a DEMO fixture or a single smoke test.

| User axis | Verified evidence so far | What prevents 100% |
| --- | --- | --- |
| Preserve and harden core | WPF/.NET 10/WebView2; 89 authoritative actions, 49 parameter definitions; fail-closed outcome audit, journal recovery, partial scans and typed IPC; 288/288 xUnit passed in the most recent full gate | **35 WRITE actions lack real isolated Windows VM, UAC denial, postchecks, interruptions, and snapshot/rollback evidence**; some Windows APIs are not cancellable |
| Refactor frontend and contracts | Compact + Developer implemented, common action contracts, generated metadata, frontend smoke with 65 module actions, responsive DPI emulation, **both modes photographed on real spacedesk tablet at 125%** | Approved CURRENT Developer mockup unavailable, full physical DPI 100/150/200%, NVDA/high contrast and complete physical interaction/keyboard audit not certified |
| Expand real QA and publication | Latest `VERIFY_OK`; 33/33 real-world incident evals; binary ZIP of 33 files hash-verified; screenshot QA; CI; bounded 93-second idle telemetry; versioned non-overwriting QA builds | Long interactive load/leak tests, physical GPU/device permutations, a verified isolated Windows VM, real rollback, manual signoff and final Git reconciliation. No public release should be published |

## Hard publication rule

`scripts/release-readiness.ps1` is **read-only** and independently blocks release without physical certification. It validates the generated action catalog SHA-256, one evidence record for each WRITE ID, successful VM/postcondition/interruption/snapshot evidence, SHA-256 hashes for every referenced artifact (within this project's `artifacts/`), physical DPI, mockup approval, NVDA/contrast, sustained load and a manual signoff. A passing `scripts/verify.ps1` only certifies the automated suite; a `BUILD_LOCAL=PASS` is a QA artifact, not release permission.

In the current environment `scripts/release-readiness.ps1` is expected to return `RELEASE_BLOCKED`: physical certification evidence does not yet exist. **Do not change gate criteria to get a green percentage; obtain the evidence instead.** `scripts/vm-qa-manifest.ps1` generates the pending 35-case matrix without executing real WRITE operations.

## Next actions to reach actual completion

1. Arrange an authorized Windows 11 disposable VM with rollback snapshot (including actual UAC behavior and a separate credential space); no destructive WRITE on Ephpatha.
2. For all 35 WRITE IDs collect positive/negative results, confirmation refusal, privilege denial, interruption and rollback where applicable, with reproducible logs/hashes and human review. Record non-applicable cases with reasons; never treat missing evidence as PASS.
3. Obtain current approved UI references and test Developer/Compact physically at each required DPI, including NVDA/high contrast, keyboard navigation, and long interactive stress. Keep immutable captures.
4. Reconcile existing concurrently modified `main` and worktrees without deleting contributions; freeze exact revision, rerun canonical gate, publish only the artifact physically tested and manually approved.

`AGENTS.md` additionally requires Codex/Claude to prefer the actual `@desktop-commander-local` connector over remote Desktop Commander **when genuinely available**, with remote as fallback and no fabrication of local tool availability.
