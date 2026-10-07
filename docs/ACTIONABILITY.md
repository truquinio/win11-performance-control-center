# Actionability policy

Win11 Performance Control Center is not a read-only dashboard. Its operating pattern is:

detect -> explain -> propose -> confirm -> execute -> verify -> rollback (when feasible).

## Safety rules

- Every WRITE action requires a typed confirmed=true parameter in the backend catalog.
- Administrative writes cross the UAC boundary through an allowlisted action ID plus validated typed parameters; the elevated helper does not expose an arbitrary shell.
- Selection-sensitive actions (processes, services) can only act on identifiers produced by a prior analysis and are revalidated at execution time.
- Windows/system/security/connectivity/automation services are fail-closed and cannot be disabled by the service tuner.
- Long repairs (DISM/SFC) have explicit time budgets and outcome verification.
- Reversible settings (EcoQoS, Edge orphan quarantine, service StartMode, power plan, pagefile) persist rollback state under the app's LocalAppData.
- Irreversible or preference-dependent domains remain diagnostic unless a bounded Windows-native remediation exists.

## Actionable modules

- Integrity: DISM /RestoreHealth + SFC + verification.
- Memory: MemoryTrim selection; pagefile capped profile (C: 512 MB + D: 4–8 GB) + rollback.
- CPU: EcoQoS selection + rollback.
- Storage: bounded cleanup, hibernation reduction, Storage Watch.
- Network: DNS flush and explicit Winsock reset.
- Drivers/hardware: Plug and Play rescan; no arbitrary third-party driver install.
- Browsers: Edge orphan quarantine + rollback.
- Multimedia: Windows Audio restart + verification.
- Startup/services: third-party automatic-service StartMode tuning + rollback; protected services are blocked.
- Windows Update: restart BITS/wuauserv without deleting update history/cache.
- Energy: Balanced / High performance + restore previous plan.
- Explorer: controlled restart + verification.

## Intentionally diagnostic-only areas

Activation/licensing, generic application inventory, privacy preferences, developer tool inventory, boot history and sleep/resume history do not have a single universally safe automatic fix. These views should explain findings and only expose writes after a specific, evidence-backed remediation is implemented.
