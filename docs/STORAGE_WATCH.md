# Multi-drive Storage Watch

Win11 Performance Control Center separates storage monitoring into three read-only layers:

1. Volume audit — instant total/free space and pressure status for all fixed volumes.
2. Storage Watch — app-local baseline history (up to 32 captures) and delta detection. A drop of 5 GiB or more between captures is flagged even when the volume still has comfortable free space.
3. Hotspot scan — only for volumes at or below 15% free. The scan is bounded by an 8-second global budget and a 700 ms per-folder budget. Partial measurements are explicitly marked as partial.

The state file is stored under the app's own LocalAppData state directory. No user file is changed or removed by these actions.

Thresholds:
- <= 5% free: CRITICAL
- <= 10% free: LOW
- <= 15% free: WATCH
- >= 5 GiB free-space loss since the previous capture: GROWTH_ALERT

Tests use synthetic volumes and disposable fixtures; they never need to scan the user's real drives.
