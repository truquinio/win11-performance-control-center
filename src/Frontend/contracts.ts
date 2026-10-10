

export type OperationState = "IDLE" | "ANALYZING" | "OPTIMIZING" | "MAINTENANCE" | "REBOOT_REQUIRED" | "ERROR";
import type { ActionDefinition } from "./generated/action-types";
export type { ActionDefinition, ActionParameterDefinition, ActionCategory, ActionRisk,
  ConnectivityRequirement, ActionMode, ActionParameterType } from "./generated/action-types";
export type EvidenceClassification = "HECHO" | "INDICIO" | "CAUSA_POSIBLE" | "NO_DETERMINADO";

export interface NetworkSnapshot {
  name: string;
  linkMbps: number;
  receiveMegabytesPerSecond: number;
  sendMegabytesPerSecond: number;
}

export interface SystemSnapshot {
  capturedAt: string;
  cpuPercent: number;
  memoryTotalBytes: number;
  memoryUsedBytes: number;
  memoryAvailableBytes: number;
  diskDrive: string;
  diskTotalBytes: number;
  diskFreeBytes: number;
  network: NetworkSnapshot | null;
  integrityStatus: string;
  driverStatus: string;
  activationStatus: string;
  rebootRequired: boolean | null;
  uptimeSeconds: number;
  operationState: OperationState;
}

export interface ReliabilityEvent {
  time: string;
  id: number;
  provider: string;
  classification: EvidenceClassification;
  message: string;
}

export interface ActionResult {
  success: boolean;
  dryRun: boolean;
  message: string;
  data?: Record<string, unknown>;
}

export interface HostResponse<T> {
  requestId: string;
  ok: boolean;
  result?: T;
  error?: string;
}

export interface Provider {
  readonly surface: "local" | "demo";
  snapshot(): Promise<SystemSnapshot>;
  reliability(): Promise<ReliabilityEvent[]>;
  catalog(): Promise<ActionDefinition[]>;
  runAction(id: string, parameters?: Record<string, unknown>): Promise<ActionResult>;
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void;
        addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
      };
    };
  }
}
