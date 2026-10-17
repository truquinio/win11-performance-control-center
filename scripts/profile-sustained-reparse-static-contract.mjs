import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const source = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "profile-sustained-qa.ps1"), "utf8");
const beforeExecutable = source.slice(0, source.indexOf("if (-not (Test-Path -LiteralPath $Executable"));
assert.ok(beforeExecutable.includes("QA_SUSTAINED_OUTPUT_OUTSIDE_ARTIFACTS"), "Lexical containment check must remain");
assert.match(beforeExecutable, /\[IO\.FileAttributes\]::ReparsePoint/, "Existing output ancestors must be inspected for junctions");
assert.match(beforeExecutable, /QA_SUSTAINED_OUTPUT_REPARSE_POINT/, "Reparse evidence paths must be rejected before starting the app");
console.log("QA_SUSTAINED_REPARSE_STATIC_CONTRACT_OK");
