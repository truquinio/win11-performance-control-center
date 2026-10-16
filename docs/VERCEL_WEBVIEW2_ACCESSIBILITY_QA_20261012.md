# Vercel-compatible frontend QA for Win11 Performance Control Center — 12/10/2026

**Product:** native Windows 11 WPF/.NET 10 + WebView2 + TypeScript, not a Vercel-hosted React/Next application.

## Skills read and routed
- Vercel **web-design-guidelines** installed at `C:\Users\fedet\.claude\skills\web-design-guidelines\SKILL.md`; retrieved the maintained rules at `https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md` on 12 Oct 2026. Applied keyboard navigation, focus visibility and transitions guidance.
- Vercel **vercel-react-best-practices** inspected. Only generic JS/CSS principles relevant to this WebView2 frontend are candidates; React hooks, Suspense, Server Components and Next Router APIs **do not apply**.
- Vercel **vercel-optimize** inspected; explicitly requires a linked deployed Vercel project and route/usage telemetry and lists this native framework as unsupported. No Vercel app or metrics were fabricated; no Vercel CLI link/deploy/payment invoked.
- `vercel-composition-patterns`, `deploy-to-vercel`, `vercel-cli`, React Native and Next.js skills **not executed** because they target incompatible runtimes/surfaces. Loading every installed skill would waste quota and risk breaking the app.
- Matching general engineering skills applied conceptually: `solve-efficiently`, `test-driven-development`, `automate-ui`, `verification-before-completion`. No external packages installed.

## Changes and reproducible evidence
1. **Keyboard skip link**: `src/Frontend/index.html` gains a first-focusable anchor `Saltar al contenido principal` pointing to `<main id="main-workspace" tabindex="-1">`. `src/Frontend/style.css` exposes it only under `:focus-visible` above the shell. Preserves existing Compact and Developer navigation and action IDs. RED: `node scripts/frontend-contract.mjs` failed with `La navegación por teclado necesita un enlace visible al enfocar para saltar al contenido principal.` GREEN: `FRONTEND_CONTRACT_OK`.
2. **Explicit transitions**: `.nav-item` and `.btn` no longer use implicit `transition:.15s` (which animates all properties); they enumerate visual properties. RED: same contract failed with `Las transiciones deben enumerar propiedades; evitar animar todos los atributos y el layout.` GREEN: same contract passed. Existing `prefers-reduced-motion`, `forced-colors` and `:focus-visible` rules preserved.
3. **Real browser keyboard proof**: `scripts/ui-smoke.mjs` dispatches Chrome DevTools `Tab` + `Enter`, asserts that first Tab focuses `.skip-link` and Enter focuses `#main-workspace`. `npm run test:ui-smoke` exited **0**, `UI_KEYBOARD_SKIPLINK_OK Tab+Enter lleva al main`, all five emulated DPI layout cases, `UI_SMOKE_DEGRADED_OK`, `UI_SMOKE_OK developerButtons=19 compactButtons=7 dashboardActions=10 moduleButtons=65 moduleActionsExecuted=65 contextualFlows=7 writeFlows=3 buttons=53`. Duration 99.49 s. The WRITE flows are DEMO simulations, not physical Windows writes.
4. **Versioned internal QA packaging**: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local.ps1 -SkipTests` exited **0** (`-SkipTests` because the directly affected UI/contract tests already passed); `BUILD_LOCAL=PASS`; Release **0 warnings/0 errors**. ZIP `artifacts/Win11PerformanceControlCenter-win-x64-20261012-085124-355-f9b1b71a73c8.zip` SHA256 `643396C824F5DB5355162AB8FEF5C23563D8F7C5FE0421AC8FB26F09AF2D0FDF`, **33/33 ZIP members hash-match** the versioned directory; embedded `index.html` contains the skip link. **QA candidate only, not public release.**
5. **Sustained performance gate**: tried `scripts/profile-sustained-qa.ps1` with `-Samples 36 -IntervalSeconds 5 -MinimumFreeMiB 2500` against the exact versioned QA executable. Exit **1**, `QA_SUSTAINED_SKIPPED: insufficient memory headroom; other projects take priority.` A free-RAM read earlier showed ~2,708 MiB; by launch it had fallen below the 2,500 MiB guard. **No WPF QA instance started**, no CSV was created and final `WPF_RUNNING=0`. Did not lower threshold or close unrelated apps. Thus no extended stress or leak-free certification.

## Preserved artifacts (append-only)
- `artifacts/ui-a11y-vercel-20261012.log` SHA256 `47D82227226B5732C03D7C81F478725FC3F1BE015466B041325EB5347E1341AA`.
- `artifacts/build-vercel-20261012-0852.log` SHA256 `08FDEF1F65BD33CCDD965FB3244FE9A67125A4E2813AA8118E7697E9FD8F8901`.
- `artifacts/profile-vercel-20261012-0855.log` SHA256 `7F72D74A94648B8CA582418A3858F4E1FEE4FEFA18559D1D1DC0A1BF8A1D2F33`.

## Still not certifiable as 100%
- **No fresh canonical `scripts/verify.ps1` run after this focused frontend change**; focused frontend contract and full UI smoke passed. The historical pre-change 293/293 xUnit and 33/33 evals cannot be represented as a new end-to-end gate on this exact current tree.
- Physical NVDA/contrast and Windows DPI 100/150/200%; original authorized visual mockups parity; sustained interactive stress; remaining 35 WRITE/UAC/rollback scenarios in reversible authorized Windows VM; independent release human signoff and Git coordination.
- Existing concurrent staged/unstaged/untracked main and worktree edits were not reset, cleaned, stashed, committed, pushed, force-deployed or overwritten. No Edge/Claude/browser profile or external project processes were modified in this run.
