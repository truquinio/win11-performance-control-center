import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { loadBackendCatalog, catalogsEqual, generatedDirectory, root } from "./catalog-source.mjs";
import { generateActionTypes } from "./action-types-source.mjs";

const authoritative = loadBackendCatalog();
const jsonPath = path.join(generatedDirectory, "action-catalog.json");
const tsPath = path.join(generatedDirectory, "demo-catalog.ts");
assert.ok(fs.existsSync(jsonPath), "Falta el catálogo generado desde C#");
assert.ok(fs.existsSync(tsPath), "Falta el módulo TypeScript generado");
const generatedTypesPath = path.join(generatedDirectory, "action-types.ts");
assert.ok(fs.existsSync(generatedTypesPath), "Faltan los tipos generados desde los modelos C#");
assert.equal(fs.readFileSync(generatedTypesPath, "utf8"), generateActionTypes(),
  "Los tipos ActionDefinition/enum no coinciden con Models/Contracts.cs");
const generated = JSON.parse(fs.readFileSync(jsonPath, "utf8"));
catalogsEqual(authoritative, generated);
const ts = fs.readFileSync(tsPath, "utf8");
const embeddedJson = ts.match(/export const demoCatalog: ActionDefinition\[\] = (\[[\s\S]*\]);\s*$/)?.[1];
assert.ok(embeddedJson, "El módulo TS no contiene el catálogo tipado");
catalogsEqual(authoritative, JSON.parse(embeddedJson));
const ids = authoritative.map(item => item.id);
assert.equal(ids.length, new Set(ids).size, "Action IDs duplicadas");
const csharpIds = [...fs.readFileSync(path.join(root, "src/App/Core/ActionCatalog.cs"), "utf8")
  .matchAll(/new ActionDefinition\("([^"]+)"/g)].map(match => match[1]);
assert.deepEqual(ids, csharpIds, "No cambiar el orden de acciones del catálogo C#");

// Prove the contract recognizes meaningful drift in an isolated in-memory fixture.
const bad = structuredClone(authoritative);
bad[0].title += " ALTERADO";
assert.throws(() => catalogsEqual(authoritative, bad), /desactualizado|alterado/);
const duplicate = structuredClone(authoritative);
duplicate.push(structuredClone(duplicate[0]));
assert.throws(() => catalogsEqual(authoritative, duplicate), /duplicado/);
const incomplete = structuredClone(authoritative);
const write = incomplete.find(item => item.mode === "WRITE" && item.parameters?.length);
assert.ok(write);
write.parameters[0].type = "UNSAFE";
assert.throws(() => catalogsEqual(authoritative, incomplete), /Parámetro inválido/);

// Ensure every interface action is traced in the canonical C# catalog.
const frontend = fs.readFileSync(path.join(root, "src/Frontend/app.ts"), "utf8");
const html = fs.readFileSync(path.join(root, "src/Frontend/index.html"), "utf8");
const definitions = fs.readFileSync(path.join(root, "src/Frontend/module-definitions.ts"), "utf8");
const definitionBlock = definitions.match(/const moduleDefinitions:[\s\S]*?= \{([\s\S]*?)\r?\n\};/)?.[1] ?? "";
const moduleIds = [...definitionBlock.matchAll(/"([a-z][a-z0-9.-]+)"/g)]
  .map(item => item[1]).filter(id => id.includes("."));
const dashboardIds = [...html.matchAll(/data-action="([^"]+)"/g)].map(item => item[1]);
for (const id of [...moduleIds, ...dashboardIds]) {
  assert.ok(ids.includes(id), "Frontend refiere a acción inexistente: " + id);
}
// Guard visible Windows action messages against repeated UTF-8 mojibake.
const executor = fs.readFileSync(path.join(root, "src/App/Core/ActionExecutor.cs"), "utf8");
const tripleEncodedPrefix = String.fromCharCode(0xc3, 0x192, 0xc2);
assert.ok(!executor.includes(tripleEncodedPrefix),
  "Los mensajes de ActionExecutor contienen texto con codificación dañada");

console.log("CATALOG_CONTRACT_OK actions=" + ids.length +
  " parameterDefs=" + authoritative.reduce((n, action) => n + (action.parameters?.length ?? 0), 0));
