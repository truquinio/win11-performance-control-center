import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const services = path.join(root, "src", "App", "Services");
const violations = [];

for (const name of fs.readdirSync(services)) {
  if (!name.endsWith(".cs")) continue;
  const file = path.join(services, name);
  const source = fs.readFileSync(file, "utf8");
  if (!source.includes("ManagementObjectSearcher")) continue;

  const lines = source.split(/\r?\n/);
  lines.forEach((line, index) => {
    if (/foreach\s*\(.*\bin\b.*\.Get\(\)\s*\)/.test(line)) {
      violations.push(`${name}:${index + 1}`);
    }
  });
}

if (violations.length) {
  throw new Error(
    "RESOURCE_CONTRACT_FAIL undisposed WMI collections: " + violations.join(", ")
  );
}

console.log("RESOURCE_CONTRACT_OK WMI collections are explicitly disposed");
