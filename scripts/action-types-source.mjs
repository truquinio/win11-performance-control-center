import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

export function generateActionTypes() {
  const source = fs.readFileSync(path.join(root, "src/App/Models/Contracts.cs"), "utf8");
  const enums = ["ActionCategory", "ActionRisk", "ConnectivityRequirement", "ActionMode", "ActionParameterType"];
  const result = ["// Generated from C# Models/Contracts.cs; never edit manually.", ""];
  for (const name of enums) {
    const match = source.match(new RegExp("public enum " + name + "\\s*\\{([^}]*)\\}"));
    if (!match) throw new Error("C# enum missing: " + name);
    const values = match[1].split(",").map(item => item.trim().match(/^(\w+)/)?.[1]).filter(Boolean);
    if (!values.length) throw new Error("Empty C# enum: " + name);
    result.push("export type " + name + " = " + values.map(JSON.stringify).join(" | ") + ";");
  }
  const mapType = csharp => {
    const nullable = csharp.endsWith("?");
    const core = nullable ? csharp.slice(0, -1) : csharp;
    const list = core.match(/^IReadOnlyList<([^>]+)>$/);
    const inner = list ? list[1] + "[]" : ({ string: "string", bool: "boolean", int: "number" }[core] ?? core);
    return inner + (nullable ? " | null" : "");
  };
  for (const name of ["ActionParameterDefinition", "ActionDefinition"]) {
    const match = source.match(new RegExp("public sealed record " + name + "\\(([\\s\\S]*?)\\);"));
    if (!match) throw new Error("C# record missing: " + name);
    result.push("", "export interface " + name + " {");
    for (const field of match[1].split(",")) {
      const clean = field.trim();
      const parsed = clean.match(/^([\w<>?]+)\s+(\w+)(\s*=\s*null)?$/);
      if (!parsed) throw new Error("Unrecognized C# record field: " + clean);
      const camelName = parsed[2][0].toLowerCase() + parsed[2].slice(1);
      result.push("  " + camelName + (parsed[3] ? "?" : "") + ": " + mapType(parsed[1]) + ";");
    }
    result.push("}");
  }
  return result.join("\n") + "\n";
}
