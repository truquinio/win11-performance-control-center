# Process Hygiene

Process Hygiene converts a real incident from 2026-10-08 into a permanent, fail-closed maintenance capability.

## Incident encoded as regression

The host had several resource-heavy processes that were no longer useful:

- Spotify Android lab emulators (`spotify_api28_play_control`, `spotify_api28_hooklab2`);
- Edge headless processes using dedicated SIG test profiles;
- Uvicorn development servers whose original launcher process had already disappeared.

At the same time, two similar-looking processes had to remain untouched:

- the live SIG development server on port 8877, whose launcher chain was still alive;
- a Playwright/Chromium headless process owned by PM2 / bot automation.

That distinction is now covered by `ProcessHygieneTests` and the manifest
`evals/process-hygiene/stale-lab-processes-protect-automation.json`.

## Analyze

`processes.hygiene.analyze` is DRY_RUN and reports only process classes with explicit rules.

Currently detected:

- `HEADLESS_SIG_TEST`: root Edge headless process with a dedicated `sig-edge-headless-profileN` user-data directory;
- `ANDROID_LAB`: QEMU Android emulator whose AVD name is explicitly marked as lab/test/hooklab/play_control;
- `ORPHAN_UVICORN`: the Uvicorn worker + Python shim still exist, but the initiating parent process no longer exists;
- `ACTIVE_DEV_SERVER`: Uvicorn has a live launcher chain and is protected;
- `PROTECTED_HEADLESS_BROWSER`: headless Chromium belongs to a protected PM2/bot/automation ancestor.

## Stop

`processes.hygiene.stop` does not accept an arbitrary kill request.

Before stopping anything it requires:

1. a PID from the current preview;
2. the ephemeral fingerprint from that preview;
3. explicit confirmation;
4. a fresh re-analysis;
5. the same PID to still be classified as stoppable;
6. the same fingerprint to still match;
7. Workload Guard to permit a disruptive action.

If any condition changes, the action fails closed.

The fingerprint includes PID, process name, start time and command line so PID reuse or a restarted process invalidates the previous preview.

## Protected automation

Classification protects chains containing signals for:

- PM2 and bot_linkedin;
- Desktop Commander;
- OpenAI / ChatGPT;
- Claude / Cowork;
- MCP;
- Ollama;
- Win11 Performance Control Center.

Unknown processes are not considered cleanup candidates.

## Verification

After a real stop the service verifies that every PID from the selected candidate group is gone. If one remains or respawns, the result is reported as `VERIFY_FAILED`; no additional cleanup is chained automatically.

Evaluation mode uses only synthetic processes and never touches the Windows host.
