# Action Catalog

`ActionCatalog` is the executable capability allowlist. UI labels are not authority; the backend catalog is.

## Main READ/DRY_RUN capabilities
- system health, integrity and reliability,
- memory/pagefile analysis,
- EcoQoS analysis,
- storage scan and Safe Cleanup preview,
- network analysis,
- driver and activation diagnostics,
- browser inventory and Edge extension health,
- multimedia inventory,
- startup/services,
- Windows Update,
- installed apps,
- privacy,
- developer tooling,
- thermal/energy,
- boot/sleep,
- Explorer,
- backup/recovery status.

## WRITE capabilities
Current WRITE operations are limited to selected-process MemoryTrim and selected-process EcoQoS apply/restore. They require typed process IDs plus explicit confirmation and are protected by the process-safety policy.

## Rules
- No arbitrary Action IDs.
- No untyped parameter bags.
- Maximum PID selection is bounded.
- WRITE and long-running operations are serialized by `OperationCoordinator`.
- New Windows-changing capabilities must first exist as READ/DRY_RUN with tests before promotion to WRITE.
