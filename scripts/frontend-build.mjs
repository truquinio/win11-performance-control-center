import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { loadBackendCatalog, writeGenerated } from "./catalog-source.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const src = path.join(root, "src", "Frontend");
const dist = path.join(src, "dist");
const demo = path.join(root, "artifacts", "demo");
const command = process.argv[2] ?? "build";

function cleanDirectory(directory) {
  fs.rmSync(directory, { recursive: true, force: true });
}
async function buildFrontend() {
  const catalog = loadBackendCatalog();
  writeGenerated(catalog);
  cleanDirectory(dist);
  fs.mkdirSync(dist, { recursive: true });
  const tsc = path.join(root, "node_modules", "typescript", "bin", "tsc");
  execFileSync(process.execPath, [tsc, "-p", path.join(root, "tsconfig.json"), "--noEmit"], {
    stdio: "inherit", windowsHide: true
  });
  await build({
    entryPoints: [path.join(src, "app.ts")],
    bundle: true,
    format: "esm",
    platform: "browser",
    target: "es2022",
    outfile: path.join(dist, "app.js"),
    logLevel: "warning"
  });
  for (const file of ["index.html", "style.css"]) {
    fs.copyFileSync(path.join(src, file), path.join(dist, file));
  }
  const icons = path.join(src, "icons");
  if (fs.existsSync(icons)) {
    fs.cpSync(icons, path.join(dist, "icons"), { recursive: true });
  }
  console.log("FRONTEND_BUILD_OK generated=" + catalog.length + " app.js=" +
    fs.statSync(path.join(dist, "app.js")).size);
}
if (command === "clean") {
  cleanDirectory(dist);
  cleanDirectory(demo);
  process.exit(0);
}
await buildFrontend();
if (command === "demo") {
  cleanDirectory(demo);
  fs.mkdirSync(path.dirname(demo), { recursive: true });
  fs.cpSync(dist, demo, { recursive: true });
  console.log("Static demo build:", demo);
}
