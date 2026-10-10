import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { generateActionTypes } from "./action-types-source.mjs";

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const generatedDirectory = path.join(root, "src", "Frontend", "generated");
const exporter = path.join(root, "tools", "CatalogExporter", "CatalogExporter.csproj");
export function loadBackendCatalog() {
  const stdout = execFileSync("dotnet", ["run", "--project", exporter,
    "-c", "Release", "--no-launch-profile", "--verbosity", "quiet"], {
      cwd: root, encoding: "utf8", timeout: 120000, windowsHide: true
    }).trim();
  const catalog = JSON.parse(stdout.split(/\r?\n/).at(-1));
  validateCatalog(catalog);
  return catalog;
}
export function validateCatalog(catalog) {
  if (!Array.isArray(catalog) || !catalog.length) throw new Error("Catálogo vacío");
  const ids = new Set();
  for (const item of catalog) {
    if (!item || typeof item.id !== "string" || !item.id ||
        !item.title || !item.description || !["READ","DRY_RUN","WRITE"].includes(item.mode) ||
        typeof item.requiresAdmin !== "boolean" ||
        (item.parameters !== null && !Array.isArray(item.parameters))) {
      throw new Error("Acción inválida: " + JSON.stringify(item));
    }
    if (ids.has(item.id)) throw new Error("Action ID duplicado: " + item.id);
    ids.add(item.id);
    for (const param of item.parameters ?? []) {
      if (!param.name || !["STRING","INTEGER","INTEGER_ARRAY","BOOLEAN"].includes(param.type) ||
          typeof param.required !== "boolean") {
        throw new Error("Parámetro inválido en " + item.id);
      }
    }
  }
}
export function catalogsEqual(expected, actual) {
  validateCatalog(expected);
  validateCatalog(actual);
  if (JSON.stringify(expected) !== JSON.stringify(actual)) {
    throw new Error("Catálogo TypeScript desactualizado o alterado respecto al catálogo C#");
  }
}
export function writeGenerated(catalog) {
  fs.mkdirSync(generatedDirectory, { recursive: true });
  fs.writeFileSync(path.join(generatedDirectory, "action-catalog.json"),
    JSON.stringify(catalog, null, 2) + "\n");
  fs.writeFileSync(path.join(generatedDirectory, "demo-catalog.ts"),
    'import type { ActionDefinition } from "../contracts";\n' +
    "// Generated from C# ActionCatalog.All; do not edit manually.\n" +
    "export const demoCatalog: ActionDefinition[] = " +
    JSON.stringify(catalog, null, 2) + ";\n");
  fs.writeFileSync(path.join(generatedDirectory, "action-types.ts"), generateActionTypes());
}
