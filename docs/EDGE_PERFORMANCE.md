# Edge Performance Control

Win11 Performance Control Center manages Edge performance without editing the browser profile.

## Actions

### `browsers.edge.performance.audit`

Read-only. Reports:

- Edge process count and visible windows;
- working set and private memory;
- effective Background Mode / Startup Boost;
- Sleeping Tabs and timeout;
- automatic discard of sleeping tabs;
- Efficiency Mode;
- visible Edge launch at Windows startup;
- `MicrosoftEdgeAutoLaunch_*` entries under the current user's Run key;
- rollback availability.

### `browsers.edge.performance.optimize`

Reversible WRITE action. It does **not** close Edge and does not modify cookies, sessions, favorites, extensions or profile JSON.

It stores the previous state once, then applies:

- recommended `BackgroundModeEnabled=0`;
- recommended `StartupBoostEnabled=0`;
- recommended `SleepingTabsEnabled=1`;
- recommended `SleepingTabsTimeout=300` (5 minutes);
- recommended `AutoDiscardSleepingTabsEnabled=1`;
- recommended `EfficiencyModeEnabled=1`;
- recommended `EfficiencyMode=0` (always active);
- mandatory `LaunchEdgeOnWindowsStartupEnabled=0`;
- removes only Run values named `MicrosoftEdgeAutoLaunch_*`, after snapshotting them.

If an administrator already set mandatory Background Mode or Startup Boost policies, those take precedence and the app reports the profile as not fully verified rather than overwriting them.

### `browsers.edge.performance.restore`

Restores exactly the captured policy values and auto-launch entries, then removes the app-owned snapshot.

## Safety properties

- no Edge process is killed;
- no browser database or profile file is edited;
- no extension is disabled;
- Run entries are filtered by exact Microsoft Edge auto-launch prefix;
- registry state is app-owned and reversible;
- repeated Optimize calls do not overwrite the original rollback snapshot;
- evaluation tests operate on synthetic state and never touch the host registry;
- Rollback Center surfaces the Edge Performance snapshot.

## Rationale

The profile uses Microsoft Edge enterprise policies that support dynamic refresh where applicable. Sleeping Tabs and auto-discard reduce background memory pressure while preserving tabs; Efficiency Mode prioritizes foreground work; Background Mode and Startup Boost prevent Edge from remaining resident simply to accelerate later launches.
