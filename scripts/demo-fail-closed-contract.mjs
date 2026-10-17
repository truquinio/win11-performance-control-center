import assert from "node:assert/strict";
import { build } from "esbuild";

const bundled = await build({
  entryPoints: ["src/Frontend/providers/demo.ts"],
  platform: "node", format: "esm", target: "es2022",
  bundle: true, write: false, logLevel: "silent"
});
const source = bundled.outputFiles[0].text;
const { DemoProvider } = await import("data:text/javascript;base64," +
  Buffer.from(source).toString("base64"));
globalThis.window = { setTimeout };
const demo = new DemoProvider();
const missing = await demo.runAction("__not_in_catalog__");
assert.equal(missing.success, false,
  "Unknown action must not simulate a successful operation");
assert.match(missing.message, /no (disponible|implementada|soportada|reconocida)/i,
  "Unknown action must explain why it was rejected");
const unsupported = await demo.runAction("network.flushdns");
assert.equal(unsupported.success, false,
  "Unimplemented catalog actions must not pretend success in DEMO");
const known = await demo.runAction("memory.analyze");
assert.equal(known.success, true, "Explicit fixture should continue working");
const outcome = await demo.runAction("lab.outcomes.status");
const declaredWrites = (await demo.catalog()).filter(action => action.mode === "WRITE").length;
assert.equal(outcome.success, true, "Outcome fixture should remain available");
assert.equal(outcome.data?.writeActionCount, declaredWrites,
  "DEMO outcome must not report a stale number of WRITE capabilities");
console.log("DEMO_FAIL_CLOSED_OK");
