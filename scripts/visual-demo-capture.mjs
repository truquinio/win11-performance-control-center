// Isolated DEMO screenshot QA. Never attaches to user browsers or WPF sessions.
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import crypto from "node:crypto";
import { stopOwnedBrowser } from "./visual-demo-process-cleanup.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const demo = path.join(root, "artifacts", "demo");
const output = path.join(root, "artifacts", "visual-qa");
const shellRoot = path.join(process.env.LOCALAPPDATA ?? "", "ms-playwright");
const browser = fs.readdirSync(shellRoot).filter(n => n.startsWith("chromium_headless_shell-"))
  .map(n => path.join(shellRoot, n, "chrome-headless-shell-win64", "chrome-headless-shell.exe"))
  .find(fs.existsSync);
if (!browser || !fs.existsSync(path.join(demo, "index.html")) || !fs.existsSync(output)) {
  throw Error("VISUAL_QA_MISSING_BROWSER_OR_DEMO_OR_OUTPUT");
}
const freeMiB = () => Math.round(os.freemem() / 1048576);
if (freeMiB() < 3000) throw Error("VISUAL_QA_LOW_MEMORY_PRELAUNCH");
const server = http.createServer((req, res) => {
  const pathname = new URL(req.url, "http://127.0.0.1").pathname;
  const relative = pathname === "/" ? "index.html" : pathname.slice(1);
  const target = path.resolve(demo, relative);
  if (!target.startsWith(demo + path.sep) || !fs.existsSync(target)) {
    res.writeHead(404).end(); return;
  }
  const ext = path.extname(target);
  res.setHeader("Content-Type", ({".js":"text/javascript",".css":"text/css",".svg":"image/svg+xml",".png":"image/png",".ico":"image/x-icon"})[ext] ?? "text/html");
  fs.createReadStream(target).pipe(res);
});
const listen = s => new Promise(resolve => s.listen(0, "127.0.0.1", resolve));
const close = s => new Promise(resolve => s.close(resolve));
await listen(server);
const probe = http.createServer();
await listen(probe);
const debugPort = probe.address().port;
await close(probe);
const origin = `http://127.0.0.1:${server.address().port}/`;
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "wpcc-visual-demo-"));
const child = spawn(browser, ["--headless=new","--disable-gpu","--no-first-run","--disable-background-networking",
  `--remote-debugging-port=${debugPort}`,`--user-data-dir=${profile}`,origin+"?mode=compact"], {stdio:"ignore"});
let browserLaunchError = null;
child.on("error", error => { browserLaunchError = error; });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let socket;
let requestId = 0;
const pending = new Map();
function send(method, params = {}) {
  const id = ++requestId;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {pending.delete(id); reject(Error("CDP_TIMEOUT "+method));}, 12000);
    pending.set(id, msg => {clearTimeout(timer); msg.error ? reject(Error(JSON.stringify(msg.error))) : resolve(msg.result);});
    socket.send(JSON.stringify({id,method,params}));
  });
}
try {
  let target;
  for (let i=0; i<160; i++) {
    if (browserLaunchError) throw Error("VISUAL_QA_BROWSER_LAUNCH_FAILED "+browserLaunchError.message);
    if (child.exitCode !== null) throw Error("BROWSER_EXITED "+child.exitCode);
    try {
      const pages = await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json();
      target = pages.find(p => p.type === "page" && p.url.startsWith(origin));
      if (target?.webSocketDebuggerUrl) break;
    } catch {}
    await sleep(150);
  }
  if (!target?.webSocketDebuggerUrl) throw Error("CDP_NOT_READY");
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, {once:true});
    socket.addEventListener("error", reject, {once:true});
  });
  socket.addEventListener("message", event => {
    const msg = JSON.parse(event.data);
    if (msg.id && pending.has(msg.id)) {pending.get(msg.id)(msg); pending.delete(msg.id);}
  });
  await send("Page.enable");
  for (const [mode,width,height] of [["compact",900,1600],["developer",900,1600],["compact",1648,927],["developer",1648,927]]) {
    if (freeMiB() < 2500) throw Error("VISUAL_QA_LOW_MEMORY_MIDRUN");
    await send("Emulation.setDeviceMetricsOverride",{width,height,deviceScaleFactor:1,mobile:false});
    await send("Page.navigate",{url:origin+"?mode="+mode});
    let ready = false;
    for (let i=0; i<50; i++) {
      const v = await send("Runtime.evaluate",{expression:"({ready:document.readyState,mode:document.getElementById('mode-status')?.textContent||'',width:document.documentElement.scrollWidth})",returnByValue:true});
      const data = v.result?.value ?? {};
      if (data.ready === "complete" && String(data.mode).includes(mode === "compact" ? "compacta" : "experta")) {ready=true;break;}
      await sleep(120);
    }
    if (!ready) throw Error("VISUAL_QA_MODE_NOT_READY "+mode);
    if (mode === "developer") {
      const measured = await send("Runtime.evaluate", {expression:"(()=>{const row=document.querySelector('tr.developer-only'); const head=document.querySelector('.recovery-table thead tr'); return {display:getComputedStyle(row).display,firstCellWidth:row.cells[0].getBoundingClientRect().width,headerCellWidth:head.cells[0].getBoundingClientRect().width};})()",returnByValue:true});
      const table = measured.result?.value ?? {};
      if (table.display !== "table-row" || Math.abs(table.firstCellWidth-table.headerCellWidth) > 2) {
        throw Error("VISUAL_QA_RECOVERY_TABLE_ROW_MISALIGNED "+JSON.stringify(table));
      }
    }
    await sleep(400);
    const result = await send("Page.captureScreenshot",{format:"png",captureBeyondViewport:false,fromSurface:true});
    const bytes = Buffer.from(result.data,"base64");
    if (bytes.length < 5000) throw Error("VISUAL_QA_EMPTY_SCREENSHOT "+mode);
    const filename = `demo-${mode}-${width}x${height}-${Date.now()}-${crypto.randomBytes(4).toString("hex")}.png`;
    const destination = path.join(output,filename);
    fs.writeFileSync(destination,bytes,{flag:"wx"});
    console.log("VISUAL_DEMO_CAPTURE_OK",filename,"bytes="+bytes.length,"sha256="+crypto.createHash("sha256").update(bytes).digest("hex"));
  }
} finally {
  try {socket?.close();} catch {}
  const browserStopped = await stopOwnedBrowser(child);
  await close(server);
  if (browserStopped) {
    try {fs.rmSync(profile,{recursive:true,force:true,maxRetries:3,retryDelay:100});}
    catch {console.warn("VISUAL_QA_TEMP_PROFILE_RETAINED",profile);}
  } else {
    console.error("VISUAL_QA_BROWSER_STOP_UNCONFIRMED",child.pid,"temporary profile retained:",profile);
    process.exitCode = 1;
  }
}
