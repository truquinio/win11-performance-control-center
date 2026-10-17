// Pure read-only compatibility contract: no Windows settings or evidence are modified.
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const helper = path.join(root, "scripts", "sha256-compat.ps1");
const target = path.join(root, "src", "Frontend", "generated", "action-catalog.json");
const expected = crypto.createHash("sha256").update(fs.readFileSync(target)).digest("hex").toUpperCase();
const quote = (value) => "'" + value.replaceAll("'", "''") + "'";
const shells = process.platform === "win32" ? ["powershell.exe", "pwsh.exe"] : ["pwsh"];
for (const shell of shells) {
  const run = (expression) => spawnSync(shell,
    ["-NoProfile", "-NonInteractive", "-Command", expression],
    { cwd: root, encoding: "utf8", timeout: 15000, windowsHide: true });
  const setup = `$ErrorActionPreference='Stop'; . ${quote(helper)}; `;
  const positive = run(setup + `Get-WpccSha256 -LiteralPath ${quote(target)}`);
  if (positive.error || positive.status !== 0 || positive.stdout.trim() !== expected) {
    throw new Error(`SHA256_COMPAT_FAIL shell=${shell} exit=${positive.status} detail=${String(positive.error || positive.stdout + positive.stderr).slice(0, 500)}`);
  }
  const negative = run(setup + `Get-WpccSha256 -LiteralPath ${quote(target + ".definitely-absent")}`);
  if (negative.error || negative.status === 0) {
    throw new Error(`SHA256_MISSING_FILE_NOT_REJECTED shell=${shell} exit=${negative.status}`);
  }
  console.log(`SHA256_COMPAT_OK shell=${shell} hash_match=true missing_file_rejected=true`);
}
