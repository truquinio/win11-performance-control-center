import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = fs.readFileSync(
  path.join(root, "src", "App", "MainWindow.xaml.cs"),
  "utf8"
);

const required = [
  "core.PermissionRequested += OnPermissionRequested;",
  "CoreWebView2PermissionState.Deny",
  "core.DownloadStarting += OnDownloadStarting;",
  "e.Cancel = true;"
];

const missing = required.filter(token => !source.includes(token));
if (missing.length) {
  throw new Error(
    "WEBVIEW_SECURITY_CONTRACT_FAIL missing: " + missing.join(" | ")
  );
}

console.log("WEBVIEW_SECURITY_CONTRACT_OK permissions and downloads fail closed");
