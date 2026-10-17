// Wait for the exact QA-owned ChildProcess to exit before deleting its temporary profile.
// No process-name lookup, global kill, user-browser interaction, or filesystem writes.
export async function stopOwnedBrowser(child, timeoutMs = 5000) {
  if (child.exitCode !== null || child.signalCode !== null) return true;
  return await new Promise(resolve => {
    let settled = false;
    const finish = confirmed => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      child.off("exit", onExit);
      child.off("error", onError);
      resolve(confirmed);
    };
    const onExit = () => finish(true);
    const onError = () => finish(false);
    child.once("exit", onExit);
    child.once("error", onError);
    const timer = setTimeout(() => finish(false), timeoutMs);
    try {
      if (!child.kill()) finish(child.exitCode !== null || child.signalCode !== null);
    } catch {
      finish(false);
    }
  });
}
