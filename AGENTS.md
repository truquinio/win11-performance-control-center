# Win11 Performance Control Center — Agent Working Rules

## Mission
Ship a reliable, lean, safe Windows 11 desktop app. Existing WPF/.NET 10/WebView2/TypeScript implementation is the starting point; reuse functioning code before replacing it. Compact and Developer are the approved modes. Preserve original icon assets and attribution.

## Evidence and completion
- Never infer a working feature from a button, a successful compile, or a simulated DEMO response.
- For each action, record catalog/dispatch alignment, parameters, previews, confirmations, permissions, physical execution evidence, result, cancellation behavior, and rollback when applicable.
- Clearly distinguish DEMO fixtures, integration fakes, Windows VM evaluation and real-host verification.
- A regression fix needs an observable failing test first, a minimal change, then a passing focused test. Run the entire verify gate only at integration/release milestones.
- Keep tests deterministic: no real AppData walks in routine integration fixtures; bounded operations and explicit partial-result states for slow filesystem APIs.
- Record test commands, exit codes, and limitations; do not claim bug-free or pixel-perfect status without evidence.

## Architecture and UX
- No Electron and no extra runtime services unless justified by measurements.
- ActionCatalog C# remains the authoritative allowlist. Prevent untested divergence with frontend/demo actions.
- Keep backend operations serialized; cancel where supported and communicate non-cancellable Windows API work precisely.
- Never leave a clickable control with no observable effect. Disabled features must explain what they need.
- Results must remain attached to the originating module across navigation; never display stale selection controls during a new action.
- Preserve keyboard focus on lightweight polling; avoid rebuilding action controls on each snapshot.
- Support DPI 100/125/150/200 %, small desktops, multimonitor and keyboard/screen-reader use.

## Windows safety
- Default READ/DRY_RUN. WRITE requires a typed allowlisted action, explicit confirmation, interlocks, privilege escalation only when needed, post-check and rollback where supported.
- Never modify drivers, BIOS/firmware, security protections, browser session data, pagefile or installed user apps by incidental cleanup.
- Never delete documents, backups, browser profiles, extensions, project directories, models, logs needed for recovery, or runtime state based on age alone.
- Do not close active browsers, bots, emulators or other projects while inspecting this app.
- For testing launch UI minimized, headless where possible; terminate only QA-owned browser processes on exit.
- Do not auto-restart Windows, publish a release or push Git without explicit authorization.

## Efficiency and handoff
- Inspect git status and existing tests before edits. Keep changes focused and avoid simultaneous writers to the same file.
- Use separate worktrees for independent coding workers and central review for integration.
- Ignore generated node_modules, bin/obj, WebView2 state; do not repack or delete forensic evidence without need.
- Canonical gate: powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
- Packaging: powershell -ExecutionPolicy Bypass -File scripts/build-local.ps1
- Real release criteria: contract, .NET tests, UI smoke, screenshot QA, privilege/hardware manual checks, version/hash, rollback and truthful changelog.
