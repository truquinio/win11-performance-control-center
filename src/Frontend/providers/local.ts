import type { Provider, SystemSnapshot, ReliabilityEvent, ActionDefinition, ActionResult, HostResponse } from "../contracts";

export class LocalProvider implements Provider {
  readonly surface = "local" as const;
  private pending = new Map<string, { resolve: (value: unknown) => void; reject: (reason: Error) => void; timer: number }>();

  constructor() {
    window.chrome?.webview?.addEventListener("message", event => {
      const message = event.data as HostResponse<unknown>;
      if (!message?.requestId) return;
      const pending = this.pending.get(message.requestId);
      if (!pending) return;
      window.clearTimeout(pending.timer);
      this.pending.delete(message.requestId);
      if (message.ok) pending.resolve(message.result);
      else pending.reject(new Error(message.error ?? "Error del backend local"));
    });
  }

  private request<T>(
    method: string,
    payload: Record<string, unknown> = {},
    timeoutMs = 20000
  ): Promise<T> {
    const requestId = crypto.randomUUID();
    return new Promise<T>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error("Timeout esperando al backend local"));
      }, timeoutMs);
      this.pending.set(requestId, { resolve: value => resolve(value as T), reject, timer });
      window.chrome?.webview?.postMessage({ type: "request", requestId, method, payload });
    });
  }

  snapshot(): Promise<SystemSnapshot> { return this.request("system.snapshot"); }
  reliability(): Promise<ReliabilityEvent[]> { return this.request("system.reliability"); }
  catalog(): Promise<ActionDefinition[]> { return this.request("actions.catalog"); }
  runAction(id: string, parameters: Record<string, unknown> = {}): Promise<ActionResult> {
    const payload: Record<string, unknown> = { id };
    if (Object.keys(parameters).length > 0) payload.parameters = parameters;
    // The backend can legitimately keep a read operation alive for up to
    // four minutes (and an elevated helper for up to three). The UI timeout
    // must outlive those budgets or it can report a false failure while the
    // global operation interlock is still held.
    const defaultActionTimeoutMs = 270000;
    const integrityRepairTimeoutMs = 60 * 60 * 1000;
    const timeoutMs = id === "system.integrity.repair"
      ? integrityRepairTimeoutMs
      : defaultActionTimeoutMs;
    return this.request("actions.run", payload, timeoutMs);
  }
}
