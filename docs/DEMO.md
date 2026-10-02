# Static demo

The demo uses the same HTML/CSS/TypeScript frontend as the Windows application.

## Difference from LOCAL
- LOCAL uses the WebView2 bridge and real Windows evidence.
- DEMO uses `DemoProvider` and never inspects the host machine.

Demo values are explicitly simulated. The UI must not present demo telemetry as real measurements.

Build:

    npm run build:demo

Output:

    artifacts/demo

The output uses relative assets and is suitable for static hosting. Publishing is intentionally not performed by local scripts.
