# Health History, Outcomes and Controlled Automation

## Health Score

`system.healthscore.analyze` captures a local health baseline and stores at most 64 records.

The score starts at 100 and only loses points for documented signals:

- memory pressure;
- low free space on the system disk and other fixed volumes;
- integrity states already measured by Windows;
- driver problem state;
- pending reboot;
- WHEA evidence;
- unclean shutdown evidence;
- repeated application hangs;
- a very high CPU sample, with deliberately low weight because it is transient.

Every deduction is returned as a factor with category, status, penalty and evidence. Unknown or not-yet-evaluated states do not silently lose points.

State is written atomically and the previous file is retained as a backup. A corrupt primary file falls back to the backup.

## What changed

`system.changes.analyze` compares the two most recent health captures.

It reports only material differences, including:

- Health Score moves of at least five points;
- RAM moves of at least eight percentage points;
- CPU moves of at least twenty percentage points;
- free-space changes of at least 1 GiB;
- volume pressure-state changes;
- integrity, driver, reboot and active-network state changes.

It does not present normal sample noise as a meaningful regression.

## Outcome coverage

`lab.outcomes.status` enumerates every Action Catalog entry with `mode=WRITE` and classifies its verification model:

- `POSTCHECK`: an explicit postcondition is measured;
- `DIRECT_VERIFIED`: the direct result is verified and no additional postcondition is required;
- `TRANSIENT`: the effect is intentionally non-persistent;
- `REBOOT_REQUIRED`: the final outcome cannot be known until Windows restarts.

The audit also reads app logs and reports completed, failed, rejected and incomplete runs per action. Tests fail if a future WRITE action is added without an outcome policy.

## Controlled automation

Automation is **OFF by default**.

`maintenance.policy.readonly` enables `READ_ONLY_IDLE`. This never expands automatically when new actions are added: the scope is a fixed allowlist and tests assert that every entry remains non-WRITE.

The safe maintenance batch can run only when:

1. policy is `READ_ONLY_IDLE`;
2. Workload Guard reports `IDLE` or explicit `MAINTENANCE`;
3. D: is not below the critical storage guard.

The current fixed batch performs only diagnostics for system health, volumes, drivers, crash evidence, USB, Edge extension health, services, Run/RunOnce startup entries, scheduled tasks and Process Hygiene.

No UAC action, cleanup, reset, service change, device restart, startup mutation or other WRITE is part of the automation batch.
