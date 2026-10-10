// Reusable DOM primitives; no source-specific data or side effects.
export function createResultGrid(
  entries: Array<[string, string]>
): HTMLElement {
  const grid = document.createElement("div");
  grid.className = "result-grid";

  for (const [label, value] of entries) {
    const item = document.createElement("div");
    item.className = "result-kv";
    const labelNode = document.createElement("span");
    labelNode.textContent = label;
    const valueNode = document.createElement("strong");
    valueNode.textContent = value;
    item.append(labelNode, valueNode);
    grid.append(item);
  }
  return grid;
}

export function createResultTable(
  headers: string[],
  rows: string[][]
): HTMLElement {
  const wrap = document.createElement("div");
  wrap.className = "result-table-wrap";
  const table = document.createElement("table");
  const head = table.createTHead().insertRow();
  for (const header of headers) {
    const cell = document.createElement("th");
    cell.scope = "col";
    cell.textContent = header;
    head.append(cell);
  }

  const body = table.createTBody();
  for (const values of rows) {
    const row = body.insertRow();
    for (const value of values) {
      row.insertCell().textContent = value;
    }
  }
  wrap.append(table);
  return wrap;
}
