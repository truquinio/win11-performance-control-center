import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");

const manifest = read("src", "App", "app.manifest");
for (const token of [
  'level="asInvoker"',
  'uiAccess="false"',
  ">PerMonitorV2<"
]) {
  if (!manifest.includes(token))
    throw new Error("PLATFORM_CONTRACT_FAIL manifest missing: " + token);
}

const elevated = read("src", "App", "Core", "ElevatedActionClient.cs");
for (const token of [
  'UseShellExecute = true',
  'Verb = "runas"',
  'PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly',
  'ex.NativeErrorCode == 1223',
  'ex.NativeErrorCode is 1260 or 577'
]) {
  if (!elevated.includes(token))
    throw new Error("PLATFORM_CONTRACT_FAIL elevation boundary missing: " + token);
}

const app = read("src", "App", "App.xaml.cs");
if (!app.includes('Global\\Win11PerformanceControlCenter.') ||
    !app.includes('identity.User?.Value')) {
  throw new Error(
    "PLATFORM_CONTRACT_FAIL single-instance mutex is not global per user"
  );
}

const mainWindow = read("src", "App", "MainWindow.xaml.cs");
for (const token of [
  "TransformFromDevice",
  "WmDpiChanged",
  "MonitorFromWindow"
]) {
  if (!mainWindow.includes(token))
    throw new Error("PLATFORM_CONTRACT_FAIL monitor/DPI handling missing: " + token);
}

const servicesDir = path.join(root, "src", "App", "Services");
const wmiViolations = [];
for (const name of fs.readdirSync(servicesDir)) {
  if (!name.endsWith(".cs")) continue;
  const source = fs.readFileSync(path.join(servicesDir, name), "utf8");
  if (!source.includes("ManagementObjectSearcher")) continue;

  const required = [
    "ManagementException",
    "COMException",
    "UnauthorizedAccessException"
  ];
  const missing = required.filter(token => !source.includes(token));
  if (missing.length)
    wmiViolations.push(name + " missing " + missing.join(", "));
}

if (wmiViolations.length) {
  throw new Error(
    "PLATFORM_CONTRACT_FAIL WMI policy degradation: " +
    wmiViolations.join(" | ")
  );
}

console.log(
  "PLATFORM_CONTRACT_OK PerMonitorV2 UAC current-user IPC multi-session WMI-policy"
);
