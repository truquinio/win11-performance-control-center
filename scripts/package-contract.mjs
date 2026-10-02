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

console.log("PACKAGE_CONTRACT_OK no forced app termination");
