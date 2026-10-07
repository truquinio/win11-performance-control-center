import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflow = fs.readFileSync(
  path.join(root, ".github", "workflows", "ci.yml"),
  "utf8"
);

if (!/scripts[\\/]verify\.ps1/i.test(workflow)) {
  throw new Error(
    "CI_CONTRACT_FAIL GitHub Actions must execute the canonical verify.ps1 gate."
  );
}

const verify = fs.readFileSync(
  path.join(root, "scripts", "verify.ps1"),
  "utf8"
);

if (!/test:real-world-evals/i.test(verify) ||
    !/Layer=RealWorldEval/i.test(verify)) {
  throw new Error(
    "CI_CONTRACT_FAIL canonical gate must execute manifest and executable real-world evals."
  );
}

console.log(
  "CI_CONTRACT_OK workflow uses canonical verification gate + real-world evals"
);
