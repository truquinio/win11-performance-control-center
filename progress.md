Plan: docs/plans/2026-10-10-win11-v2.md
Task 1: complete in e8e5b10 — module-scoped results and UI regression.
Task 2: complete in e8e5b10 — demo provenance and cleanup preview invalidation.
Task 3: complete in 2a4be7e — generated C# catalog/TypeScript contracts, modular UI, bundled WebView2 app, partial C# executor, focused and full tests.
Task 4: automated tests, static inventory, packaging, visual captures and four real-host READ checks complete. Physical WRITE/UAC/rollback and mixed-DPI/NVDA require an isolated Windows VM and manual testing. Do not claim universal certification.
Storage rebuild merge: main introduced two READ actions and guarded Recycle Bin cleanup; integrated via current merge with 89 actions (51 READ, 3 DRY_RUN, 35 WRITE), full xUnit 247 and incident evals 33 passing.
Integration: merged branch to main only after conflict-free merge and successful production launch. No automatic GitHub push/release.
