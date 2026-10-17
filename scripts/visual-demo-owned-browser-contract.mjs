// Isolated QA browser lifecycle regression: never remove a live browser's temp profile.
// Fake ChildProcess objects only; no browser or user process is launched or stopped.
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { stopOwnedBrowser } from "./visual-demo-process-cleanup.mjs";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const capture = fs.readFileSync(path.join(scriptDir, "visual-demo-capture.mjs"), "utf8");
assert.match(capture, /await stopOwnedBrowser\(child\)/, "Capture must await owned-browser exit.");
assert.match(capture, /if \(browserStopped\)[\s\S]*?fs\.rmSync\(profile/, "Only delete temp profile after confirmed exit.");
assert.doesNotMatch(capture, /child\.kill\(\)/, "Capture must not use unconfirmed kill.");
assert.match(capture, /child\.on\("error"/, "Spawn failure must have an error listener before any await.");
assert.match(capture, /if \(browserLaunchError\)/, "Spawn failure must abort capture.");

const uiSmoke = fs.readFileSync(path.join(scriptDir, "ui-smoke.mjs"), "utf8");
assert.match(uiSmoke, /import \{ stopOwnedBrowser \} from "\.\/visual-demo-process-cleanup\.mjs"/);
assert.match(uiSmoke, /edgeProcess\.once\("error"/);
assert.match(uiSmoke, /if \(browserLaunchError\)/);
assert.match(uiSmoke, /const browserStopped = await stopOwnedBrowser\(edgeProcess\)/);
assert.match(uiSmoke, /UI_SMOKE_BROWSER_STOP_UNCONFIRMED/);
const cleanupStart = uiSmoke.indexOf("async function cleanup(socket)");
const cleanupEnd = uiSmoke.indexOf("\n}\n\nlet socket", cleanupStart);
assert.ok(cleanupStart >= 0 && cleanupEnd > cleanupStart, "UI smoke cleanup block must be present.");
const uiSmokeCleanup = uiSmoke.slice(cleanupStart, cleanupEnd);
assert.match(uiSmokeCleanup, /if \(browserStopped\)[\s\S]*?fs\.rmSync\(profile/);
assert.doesNotMatch(uiSmokeCleanup, /edgeProcess\.kill\(\)|sleep\(300\)/);
function fakeChild(onKill, exitCode = null) {
  const child = new EventEmitter();
  child.exitCode = exitCode;
  child.signalCode = null;
  child.calls = 0;
  child.kill = () => { child.calls++; return onKill(child); };
  return child;
}
const alreadyExited = fakeChild(() => { throw Error("must not call kill"); }, 0);
assert.equal(await stopOwnedBrowser(alreadyExited, 20), true);
assert.equal(alreadyExited.calls, 0);
const normal = fakeChild(child => {
  queueMicrotask(() => { child.exitCode = 0; child.emit("exit", 0, null); });
  return true;
});
assert.equal(await stopOwnedBrowser(normal, 100), true);
assert.equal(normal.calls, 1);
const stuck = fakeChild(() => true);
assert.equal(await stopOwnedBrowser(stuck, 20), false);
assert.equal(stuck.calls, 1);
const denied = fakeChild(() => false);
assert.equal(await stopOwnedBrowser(denied, 20), false);
assert.equal(denied.calls, 1);
console.log("VISUAL_DEMO_OWNED_BROWSER_CONTRACT_OK cases=4 no real process");
