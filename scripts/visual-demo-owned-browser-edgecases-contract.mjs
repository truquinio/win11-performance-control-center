// Additional isolated lifecycle cases; no browser or host process is started.
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { stopOwnedBrowser } from "./visual-demo-process-cleanup.mjs";

function fake(kill) {
  const child = new EventEmitter();
  child.exitCode = null;
  child.signalCode = null;
  child.kill = () => kill(child);
  return child;
}
const errored = fake(child => {
  queueMicrotask(() => child.emit("error", new Error("launch/termination failure")));
  return true;
});
assert.equal(await stopOwnedBrowser(errored, 100), false);
assert.equal(errored.listenerCount("exit"), 0);
assert.equal(errored.listenerCount("error"), 0);

const throwing = fake(() => { throw new Error("permission denied"); });
assert.equal(await stopOwnedBrowser(throwing, 100), false);
assert.equal(throwing.listenerCount("exit"), 0);
assert.equal(throwing.listenerCount("error"), 0);

const timeout = fake(() => true);
assert.equal(await stopOwnedBrowser(timeout, 10), false);
assert.equal(timeout.listenerCount("exit"), 0);
assert.equal(timeout.listenerCount("error"), 0);
timeout.exitCode = 0;
timeout.emit("exit", 0, null); // A late exit cannot retroactively certify cleanup.

const signaled = fake(() => { throw new Error("already exited"); });
signaled.signalCode = "SIGTERM";
assert.equal(await stopOwnedBrowser(signaled, 100), true);
console.log("VISUAL_DEMO_BROWSER_EDGECASES_OK cases=4 fake-only");
