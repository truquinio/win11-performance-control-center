import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const gatePath = path.join(root, "scripts", "release-readiness.ps1");
if (!fs.existsSync(gatePath)) throw new Error("RELEASE_CONTRACT_FAIL no physical-certification gate");
const gate = fs.readFileSync(gatePath, "utf8");
for (const token of [
  "PENDING_VM", "RELEASE_BLOCKED", "action-catalog.json",
  "SNAPSHOT_RESTORED", "VM_ISOLATION_VERIFIED", "UAC_VERIFIED",
  "MANUAL_SIGNOFF", "MOCKUP_APPROVED", "NVDA_VERIFIED",
  "SHA256", "Get-WpccSha256", "sha256-compat.ps1",
  "if ($source.requiresAdmin)", "if ($source.reversible)",
  "uacGranted", "uacDenied", "rollbackVerified",
  "Assert-Evidence $a.uacGrantEvidence", "Assert-Evidence $a.uacDenialEvidence",
  "Assert-Evidence $a.rollbackEvidence"
]) {
  if (!gate.includes(token)) throw new Error("RELEASE_CONTRACT_FAIL missing " + token);
}
// The gate must not follow a junction/symlink from artifacts into private files.
if (!gate.includes("ReparsePoint") || !gate.includes("Get-Item -LiteralPath")) {
  throw new Error("RELEASE_CONTRACT_FAIL reparse-point escape guard missing");
}
const verify = fs.readFileSync(path.join(root, "scripts", "verify.ps1"), "utf8");
if (!verify.includes("release-readiness-contract.mjs"))
  throw new Error("RELEASE_CONTRACT_FAIL canonical gate omits fail-closed release contract");
const shell = process.platform === "win32" ? "powershell.exe" : "pwsh";
const probe = spawnSync(shell, [
  "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", gatePath,
  "-EvidenceManifest", "../outside-certification.json"
], { cwd: root, encoding: "utf8", timeout: 30000 });
if (probe.error || probe.status !== 2 ||
    !probe.stdout.includes("RELEASE_BLOCKED: Evidence path outside project artifacts.")) {
  throw new Error(
    "RELEASE_CONTRACT_FAIL traversal did not fail closed: " +
    String(probe.error || probe.stdout || probe.stderr).slice(0, 300)
  );
}
// A real junction inside artifacts must not allow hashing files in docs.
const junction = path.join(root, "artifacts", `__wpcc_release_link_${process.pid}_${Date.now()}`);
let created = false;
try {
  fs.symlinkSync(path.join(root, "docs"), junction, process.platform === "win32" ? "junction" : "dir");
  created = true;
  const escaped = spawnSync(shell, [
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", gatePath,
    "-EvidenceManifest", path.relative(root, path.join(junction, "ARCHITECTURE.md"))
  ], { cwd: root, encoding: "utf8", timeout: 30000 });
  if (escaped.error || escaped.status !== 2 ||
      !escaped.stdout.includes("RELEASE_BLOCKED: Evidence path uses ReparsePoint:")) {
    throw new Error(
      "RELEASE_CONTRACT_FAIL junction escape did not fail closed: " +
      String(escaped.error || escaped.stdout || escaped.stderr).slice(0, 300)
    );
  }
  console.log("RELEASE_REPARSE_NEGATIVE_OK");
} finally {
  // Remove only our own link, never the target directory or historical artifacts.
  if (created) {
    if (!fs.lstatSync(junction).isSymbolicLink())
      throw new Error("RELEASE_CONTRACT_FAIL QA junction unexpectedly replaced");
    fs.rmdirSync(junction);
  }
}
console.log("RELEASE_READINESS_CONTRACT_OK traversal and junction blocked; no fake WRITE certification");
