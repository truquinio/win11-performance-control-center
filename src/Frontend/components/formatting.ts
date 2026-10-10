// Shared display-safe data formatting for all views.
export function formatBytes(value: number, decimals = 1): string {
  if (!Number.isFinite(value) || value < 0) return "No disponible";
  if (value === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  // Values below one byte would otherwise produce a negative unit index.
  const index = Math.max(0, Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1));
  return (value / 1024 ** index).toFixed(index === 0 ? 0 : decimals) + " " + units[index];
}

export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "No disponible";
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  if (days > 0) return days + " d " + hours + " h";
  const minutes = Math.floor((seconds % 3600) / 60);
  return hours + " h " + minutes + " min";
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

export function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

export function displayValue(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "boolean") return value ? "Sí" : "No";
  if (typeof value === "number") return Number.isFinite(value)
    ? value.toLocaleString()
    : "—";
  return String(value);
}
