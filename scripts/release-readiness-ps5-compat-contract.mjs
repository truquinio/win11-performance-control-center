// Synthetic negative fixture only: never certifies Windows WRITE or release readiness.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const artifacts = path.join(root, "artifacts");
const catalog = path.join(root, "src", "Frontend", "generated", "action-catalog.json");
const gate = path.join(root, "scripts", "release-readiness.ps1");
const manifest = path.join(artifacts, `__wpcc_ps5_compat_${process.pid}_${crypto.randomBytes(6).toString("hex")}.json`);
const sha256 = crypto.createHash("sha256").update(fs.readFileSync(catalog)).digest("hex").toUpperCase();
const fixture = { schema: "wpcc-release-physical-cert-v1", catalogSha256: sha256, actions: [], checks: {} };
try {
  fs.writeFileSync(manifest, JSON.stringify(fixture), { flag: "wx" });
  const expected = "RELEASE_BLOCKED: WRITE evidence count mismatch: 0/35";
  const failures = [];
  for (const shell of process.platform === "win32" ? ["powershell.exe", "pwsh.exe"] : ["pwsh"]) {
    const result = spawnSync(shell, [
      "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", gate,
      "-EvidenceManifest", path.relative(root, manifest)
    ], { cwd: root, encoding: "utf8", timeout: 15000, windowsHide: true });
    if (result.error || result.status !== 2 || !result.stdout.includes(expected)) {
      failures.push(`shell=${shell} exit=${result.status} output=${String(result.error || result.stdout + result.stderr).trim().slice(0, 500)}`);
      continue;
    }
    console.log(`PS5_COMPAT_NEGATIVE_OK shell=${shell} exit=2`);
  }
  if (failures.length) throw new Error("PS5_COMPAT_RED " + failures.join(" | "));

} finally {
  // Only remove this invocation's unique synthetic manifest, never user evidence.
  if (fs.existsSync(manifest)) fs.unlinkSync(manifest);
}
