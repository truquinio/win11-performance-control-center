# Visual specification

Visual targets are the written acceptance criteria below and the user-supplied portrait tablet screenshot from 12/10/2026. That screenshot is a layout/style reference only: retain Win11 labels, genuine telemetry and real action semantics; do not reuse its Rewards Auditor name, statuses or test data. At tablet widths, use a top app bar with horizontally scrollable navigation, a prominent system status card, grouped two-column actions/metrics, rounded navy panels and clear blue/green states. The checked-in `docs/screenshots/dashboard-compact.png` is a legacy landscape reference; `docs/screenshots/dashboard-developer.png` shows an obsolete Compact layout with broken icons, so **do not use it as evidence of Developer pixel parity**. A pixel-level pass still requires fresh screenshots at target physical sizes.

## Compact mode
- Default mode.
- Desktop keeps the left navigation, title/subtitle, Diagnóstico completo and Modo experto.
- Desktop target: five metric cards in one row, three primary actions in one row, and recovery/storage beside activity/logs.
- Tablet follows the supplied portrait hierarchy: branded top bar, horizontally scrolling destinations, prominent CPU/system status, two-column primary actions, paired secondary metrics, then recovery/activity and interlocks.
- No invented telemetry. Unknown values render as "—", "Sin evaluar" or equivalent.
- Target desktop viewport: 1648×927; also usable at 1366×768, portrait tablet widths and high DPI.

## Developer mode
- Dense expert layout; this is not a third design.
- Full technical sidebar including RAM, CPU/EcoQoS, disk, startup, integrity, drivers, multimedia, network, browsers, Windows Update, apps, privacy, developer performance, energy/thermal, advanced diagnostics, activation, backup/rollback, history and settings.
- Eight metric cards in one row when viewport allows: CPU, RAM, disk, temperature, network, integrity, drivers, activation.
- Six recommended actions in one row when viewport allows.
- Recovery table + activity/logs + interlocks visible in the first working area where practical.
- Reliability and Action Catalog remain developer-only detail sections below.

## Visual rules
- Dark Windows 11 / Fluent-inspired navy surfaces.
- Bright blue primary actions; cyan, green, purple, amber and magenta accents only where semantically useful.
- Compact spacing, clear hierarchy, subtle glow, thin blue borders and rounded corners.
- No horizontal overflow at target widths.
- No fake health, temperature, cleanup or reliability values for the LOCAL surface.
- LOCAL and DEMO use the same DOM/CSS structure; only data provider changes.

## Visual QA gate
A visual pass is not complete until screenshots are captured at 1648×927 for Compact and Developer and compared against these criteria.