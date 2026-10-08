import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const regressions = path.join(root, "evals", "regressions");
const requiredIds = new Set([
  "claude-mcp-survives-cleanup",
  "running-app-skips-cache",
  "recent-vs-old-cache"
]);

if (!fs.existsSync(regressions)) {
  throw new Error("REAL_WORLD_EVAL_CONTRACT_FAIL missing evals/regressions");
}

const files = fs.readdirSync(regressions)
  .filter(name => name.endsWith(".json"))
  .sort();

if (files.length < requiredIds.size) {
  throw new Error(
    "REAL_WORLD_EVAL_CONTRACT_FAIL expected at least " +
    requiredIds.size + " regression manifests"
  );
}

const ids = new Set();
for (const name of files) {
  const full = path.join(regressions, name);
  const scenario = JSON.parse(fs.readFileSync(full, "utf8"));
  const required = [
    "id",
    "kind",
    "description",
    "targetRelativePath",
    "processRunning",
    "expectedDeletedFiles",
    "expectedFailedFiles",
    "runTwice",
    "files"
  ];
  for (const field of required) {
    if (!(field in scenario)) {
      throw new Error(
        "REAL_WORLD_EVAL_CONTRACT_FAIL " + name + " missing " + field
      );
    }
  }

  if (!/^[a-z0-9-]+$/.test(scenario.id)) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL invalid id " + scenario.id
    );
  }
  if (ids.has(scenario.id)) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL duplicate id " + scenario.id
    );
  }
  ids.add(scenario.id);

  if (!Array.isArray(scenario.files) || scenario.files.length === 0) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL " + scenario.id + " has no files"
    );
  }
  const outcomes = new Set(scenario.files.map(file => file.expect));
  if ([...outcomes].some(value =>
      !["deleted", "preserved"].includes(value))) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL " + scenario.id + " invalid outcome"
    );
  }
  if (scenario.kind === "historical_regression" &&
      !outcomes.has("preserved")) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL " + scenario.id +
      " lacks preservation assertion"
    );
  }
}

for (const id of requiredIds) {
  if (!ids.has(id)) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL required regression missing: " + id
    );
  }
}



const processHygieneDir = path.join(root, "evals", "process-hygiene");
const requiredProcessHygieneId = "stale-lab-processes-protect-automation";

if (!fs.existsSync(processHygieneDir)) {
  throw new Error(
    "REAL_WORLD_EVAL_CONTRACT_FAIL missing evals/process-hygiene"
  );
}

const processFiles = fs.readdirSync(processHygieneDir)
  .filter(name => name.endsWith(".json"))
  .sort();

const processIds = new Set();
for (const name of processFiles) {
  const full = path.join(processHygieneDir, name);
  const scenario = JSON.parse(fs.readFileSync(full, "utf8"));
  const required = [
    "id",
    "kind",
    "description",
    "expectedStoppableCategories",
    "expectedProtectedCategories",
    "requiresCurrentFingerprint",
    "workloadGuardRequired",
    "arbitraryPidKillAllowed"
  ];

  for (const field of required) {
    if (!(field in scenario)) {
      throw new Error(
        "REAL_WORLD_EVAL_CONTRACT_FAIL " + name + " missing " + field
      );
    }
  }

  if (scenario.kind !== "process_hygiene_regression") {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL invalid process hygiene kind " +
      scenario.kind
    );
  }

  if (!Array.isArray(scenario.expectedStoppableCategories) ||
      scenario.expectedStoppableCategories.length === 0 ||
      !Array.isArray(scenario.expectedProtectedCategories) ||
      scenario.expectedProtectedCategories.length === 0) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL " + scenario.id +
      " requires stoppable and protected categories"
    );
  }

  if (scenario.requiresCurrentFingerprint !== true ||
      scenario.workloadGuardRequired !== true ||
      scenario.arbitraryPidKillAllowed !== false) {
    throw new Error(
      "REAL_WORLD_EVAL_CONTRACT_FAIL " + scenario.id +
      " weakens process hygiene safety"
    );
  }

  processIds.add(scenario.id);
}

if (!processIds.has(requiredProcessHygieneId)) {
  throw new Error(
    "REAL_WORLD_EVAL_CONTRACT_FAIL required process regression missing: " +
    requiredProcessHygieneId
  );
}

console.log(
  "REAL_WORLD_EVAL_CONTRACT_OK regressions=" + files.length +
  " required=" + requiredIds.size +
  " processHygiene=" + processFiles.length +
  " processRequired=1"
);
