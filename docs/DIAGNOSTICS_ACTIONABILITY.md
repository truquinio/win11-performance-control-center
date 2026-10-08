# Diagnostics Actionability v2

This tranche extends the control-loop pattern to crashes, USB devices and startup entries.

## Crash Intelligence

`system.crash.analyze` groups factual reliability events into patterns:

- WHEA hardware evidence;
- application crashes and hangs;
- unclean shutdowns / Kernel-Power;
- Windows Error Reporting.

Each insight includes a recommended **diagnostic** action. It does not claim a root cause from a single event.

## USB Repair Center

`drivers.usb.analyze` lists USB devices and Windows problem codes.

`drivers.usb.restart` is available only when all of the following are true:

- the device currently exists in the USB inventory;
- Windows reports a non-zero problem code;
- the instance ID is USB;
- the device is not a hub, keyboard, mouse, HID, storage, network, Bluetooth or controller class.

The restart uses `pnputil /restart-device`, rescans Plug and Play and verifies the post-action problem code. It is also protected by **PC in use** mode.

## Run / RunOnce startup entries

`startup.entries.preview` inspects a fixed allowlist of Run/RunOnce registry locations.

Entries associated with Windows, security software, OpenAI/ChatGPT, Claude, Desktop Commander, MCP, Ollama, PM2, Node, Python, NSSM or OneDrive fail closed as protected.

`startup.entry.disable`:

1. re-reads the exact entry;
2. refuses protected/unresolved entries;
3. stores an app-owned snapshot;
4. removes only that registry value;
5. verifies absence;
6. does **not** kill the currently running process.

`startup.entry.restore` restores the exact saved value and verifies it. Startup-entry snapshots are also exposed through Rollback Center.

## Testing

Evaluation mode uses synthetic USB devices and synthetic startup entries. No test disables real startup items or restarts real USB devices.


## Android development workload protection

MemoryTrim and EcoQoS candidate selection fail closed for Android tooling processes used by the local emulator lab:

- `adb`
- `emulator`
- `qemu-system-x86_64`
- `qemu-system-x86_64-headless`

Crash Intelligence recognizes QEMU/Android Emulator application failures as an Android-development incident and recommends resource analysis without asserting a root cause from a single Windows event.
