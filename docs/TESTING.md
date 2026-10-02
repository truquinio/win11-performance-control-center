# Testing and release gates

## One-command verification

    powershell -ExecutionPolicy Bypass -File scripts\verify.ps1

Gate:
1. TypeScript/frontend build.
2. Release build with warnings treated as errors.
3. xUnit suite.
4. analyzer verification.
5. NuGet vulnerability audit.
6. npm production audit.

## Visual QA

    powershell -ExecutionPolicy Bypass -File scripts\visual-qa.ps1

Canonical target: 1648×927 in Compact and Developer modes. Additional manual/headless checks cover 1366×768 and high-DPI layouts.

## Local package

    powershell -ExecutionPolicy Bypass -File scripts\build-local.ps1

Outputs:
- `artifacts/local-win-x64/`
- `artifacts/Win11PerformanceControlCenter-win-x64.zip`

During QA, launch without stealing focus:

    powershell -ExecutionPolicy Bypass -File scripts\start-local-minimized.ps1

## Current release-candidate evidence
- Release build: 0 warnings, 0 errors.
- Tests: 67 passing.
- NuGet known vulnerabilities: 0.
- npm production vulnerabilities: 0.
- Local package smoke test: process opens and responds.
