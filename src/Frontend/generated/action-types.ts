// Generated from C# Models/Contracts.cs; never edit manually.

export type ActionCategory = "System" | "Reliability" | "Memory" | "CPU" | "Storage" | "Network" | "Integrity" | "Drivers" | "Browsers" | "Multimedia" | "Startup" | "WindowsUpdate" | "Apps" | "Privacy" | "Developer" | "Thermal" | "Boot" | "SleepResume" | "Explorer" | "Backup";
export type ActionRisk = "SAFE" | "CAUTION" | "EXPLICIT_CONFIRMATION";
export type ConnectivityRequirement = "OFFLINE" | "OPTIONAL_ONLINE" | "ONLINE_REQUIRED";
export type ActionMode = "READ" | "DRY_RUN" | "WRITE";
export type ActionParameterType = "STRING" | "INTEGER" | "INTEGER_ARRAY" | "BOOLEAN";

export interface ActionParameterDefinition {
  name: string;
  type: ActionParameterType;
  required: boolean;
  description: string;
}

export interface ActionDefinition {
  id: string;
  title: string;
  description: string;
  category: ActionCategory;
  risk: ActionRisk;
  requiresAdmin: boolean;
  connectivity: ConnectivityRequirement;
  reversible: boolean;
  mode: ActionMode;
  parameters?: ActionParameterDefinition[] | null;
}
