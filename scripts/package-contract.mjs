import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const buildScript = fs.readFileSync(
  path.join(root, "scripts", "build-local.ps1"),
  "utf8"
);

const forceKillPattern =
  /Get-Process\s+Win11PerformanceControlCenter[\s\S]*?Stop-Process\s+-Force/i;

if (forceKillPattern.test(buildScript)) {
  throw new Error(
    "PACKAGE_CONTRACT_FAIL build-local must never force-kill a running app."
  );
}

if (/Remove-Item\s+\$output\b|Remove-Item\s+\$zip\b|Remove-Item\s+\$staging\b/i.test(buildScript)) {
  throw new Error("PACKAGE_CONTRACT_FAIL build-local deletes or overwrites an existing package or staging evidence.");
}
if (!buildScript.includes("NewGuid") ||
    !buildScript.includes("PACKAGE_VERSIONED_PATH=") ||
    !buildScript.includes("PACKAGE_EXISTS_REFUSED")) {
  throw new Error("PACKAGE_CONTRACT_FAIL versioned non-destructive packaging required.");
}
console.log("PACKAGE_CONTRACT_OK no forced app termination or overwritten QA packages");
