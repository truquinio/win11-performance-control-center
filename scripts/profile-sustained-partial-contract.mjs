import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const script = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "profile-sustained-qa.ps1"), "utf8");
const handler = script.match(/}\s*catch\s*\{([\s\S]*?)\}\s*finally\s*\{/);
assert.ok(handler, "QA must handle an interrupted observation before stopping its own process");
assert.match(handler[1], /\$rows\.Count\s*-gt\s*0/, "Partial evidence must only be exported when samples exist");
assert.match(handler[1], /Export-Csv\s+-LiteralPath\s+\$Output[^\r\n]*-NoClobber/, "Never overwrite existing QA evidence");
assert.match(handler[1], /QA_SUSTAINED_PARTIAL_CSV/, "The output must identify partial, uncertified evidence");
assert.match(handler[1], /throw\s+\$originalFailure/, "The original failure must remain a failure");
const finalizerStart = script.lastIndexOf("} finally {");
assert.ok(finalizerStart > 0, "QA finalizer must be present");
assert.ok(script.indexOf("QA_SUSTAINED_OBSERVED") > finalizerStart,
  "Do not announce a successful observation before QA-owned process cleanup succeeds");
console.log("QA_SUSTAINED_PARTIAL_CONTRACT_OK");
