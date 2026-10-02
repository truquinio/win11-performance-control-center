# Visual specification

Authoritative visual references are the two user-supplied screenshots.

## Compact mode
- Default mode.
- Left navigation: Dashboard, Rendimiento, Limpieza, Sistema, Red, Herramientas, Configuración.
- Main header: product title, subtitle, Diagnóstico completo, Modo experto.
- Five metric cards in one row: CPU, RAM, system disk, active network, integrity.
- Three primary actions in one row: safe cleanup, system diagnostic, EcoQoS/performance.
- Lower area: advanced recovery/storage table on the left; activity/logs on the right.
- No invented telemetry. Unknown values render as "—", "Sin evaluar" or equivalent.
- Target viewport: 1648×927; must also remain usable at 1366×768 and high DPI.

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