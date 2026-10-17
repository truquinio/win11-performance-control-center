import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = relative => fs.readFileSync(path.join(root, relative), "utf8");
const catalog = JSON.parse(read("src/Frontend/generated/action-catalog.json"));
const writes = catalog.filter(item => item.mode === "WRITE");
const ids = new Set(writes.map(item => item.id));
if (writes.length !== 35 || ids.size !== 35)
  throw new Error("ECO_QA_CONTRACT_FAIL expected 35 unique authoritative WRITE ids");

const source = read("tests/App.Tests/EcoQa35InterlockTests.cs");
for (const expected of [
  "AllWriteIds()",
  "RefusedWrite_NeverStartsOrElevates",
  "MissingConsent_NeverStartsOrElevates",
  "InvalidConsentType_NeverStartsOrElevates",
  "UnexpectedParameter_NeverStartsOrElevates",
  "AssertRejectedWithoutStartAsync",
  'Assert.Equal("REJECTED"',
  '"workload-mode.json"',
  '"pagefile.json"'
]) {
  if (!source.includes(expected))
    throw new Error("ECO_QA_CONTRACT_FAIL missing dynamic guard " + expected);
}
const executor = read("src/App/Core/ActionExecutor.cs");
const validateAt = executor.indexOf("ValidateParameters(action, parameters);");
const rejectAt = executor.indexOf("consent.ValueKind != JsonValueKind.True");
const workloadAt = executor.indexOf("workloadGuardService.ShouldBlock(id)");
if (validateAt < 0 || rejectAt < validateAt || workloadAt < rejectAt ||
    !executor.includes("action.Mode == ActionMode.WRITE")) {
  throw new Error("ECO_QA_CONTRACT_FAIL consent must reject before operation/privilege");
}
const fixture = read("tests/App.Tests/EcoQa35SyntheticFlowTests.cs");
for (const scenario of [
  "WorkloadModes_RoundTripInAnIsolatedFixture",
  "MaintenanceMode_WritesOnlySyntheticPolicyState",
  "EdgeQuarantineAndRestore_RoundTripSyntheticContentExactly",
  "EdgePerformanceProfile_RestoresWithoutChangingUserRegistry"
]) {
  if (!fixture.includes(scenario))
    throw new Error("ECO_QA_CONTRACT_FAIL missing isolated positive case " + scenario);
}
const serviceFixture = read("tests/App.Tests/EcoQa35SyntheticServiceTests.cs");
for (const scenario of [
  "ServiceMode_EvaluationRequiresSavedBaselineForRollback",
  "StartupEntry_DisableRestoreUsesOnlyEvaluationRegistry",
  "ScheduledTask_DisableRestoreUsesOnlySyntheticTask",
  "PowerPlanActions_RoundTripThroughAnInjectedSyntheticCommandRunner",
  "PageFileActions_RoundTripThroughAnInjectedSyntheticBackend",
  "PageFileActions_WriteFailureCanRecoverFromTheSavedSyntheticSnapshot"
]) {
  if (!serviceFixture.includes(scenario))
    throw new Error("ECO_QA_CONTRACT_FAIL missing synthetic service adapter case " + scenario);
}
const diagnostics = read("tests/App.Tests/DeepWindowsEvidenceTests.cs");
if (!/\[Trait\("Layer", "EcoQa35Synthetic"\)\]\s+public async Task DiagnosticBundle_IsSanitizedAndContainsExpectedFiles/.test(diagnostics) ||
    !diagnostics.includes("using var root = new TestDataRoot();") ||
    !diagnostics.includes("Assert.True(bundle.Sanitized);"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing isolated sanitized diagnostic bundle WRITE");
const ecoQos = read("tests/App.Tests/EcoQosRoundTripTests.cs");
if (!ecoQos.includes('[Trait("Layer", "EcoQa35Synthetic")]') ||
    !ecoQos.includes("ApplyThenRestore_RoundTripsOnAnOwnedProcess"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing owned-process EcoQoS round trip");
const hygiene = read("tests/App.Tests/ProcessHygieneTests.cs");
if (!/\[Trait\("Layer", "EcoQa35Synthetic"\)\]\s+public async Task HostBridge_MaintenanceMode_AllowsOnlyPreclassifiedSyntheticStop/.test(hygiene))
  throw new Error("ECO_QA_CONTRACT_FAIL missing synthetic IPC Process Hygiene stop");
const memoryTrim = read("tests/App.Tests/EcoQa35MemoryTrimTests.cs");
if (!memoryTrim.includes("MemoryTrim_OnlyTrimsAnOwnedDisposableChildProcess") ||
    !memoryTrim.includes('[Trait("Layer", "EcoQa35Synthetic")]') ||
    !memoryTrim.includes('Arguments = "127.0.0.1 -n 20 -w 100"') ||
    !memoryTrim.includes("TrimWorkingSetsAsync([target])") ||
    !memoryTrim.includes("process.Kill(entireProcessTree: true)"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing owned-process MemoryTrim round trip");
const systemRemediation = read("src/App/Services/SystemRemediationService.cs");
const commandRunnerCalls = systemRemediation.match(/await commandRunner\(/g) ?? [];
const remediationTests = read("tests/App.Tests/EcoQa35SystemRemediationTests.cs");
if (!systemRemediation.includes("commandRunner ?? RunAsync") ||
    !systemRemediation.includes("serviceRestartRunner ?? RestartServiceAsync") ||
    !systemRemediation.includes("await serviceRestartRunner(name)") ||
    !systemRemediation.includes("explorerRestartRunner ?? RestartExplorerCoreAsync") ||
    commandRunnerCalls.length !== 6 ||
    !remediationTests.includes("GlobalCommandWrites_UseOnlyInjectedRunnerAndReportVerifiedOutcomes") ||
    !remediationTests.includes("InjectedTimeout_FailsClosedAndRetryCanRecover") ||
    !remediationTests.includes("IntegrityRestoreFailure_DoesNotStartSfc") ||
    !remediationTests.includes("ServiceRestartWrites_UseOnlyInjectedRestartRunner") ||
    !remediationTests.includes("ServiceRestartFailure_ReportsPartialAndAttemptsRemainingService") ||
    !remediationTests.includes("HibernateReduction_UsesInjectedPowerCfgAndReportsTimeout") ||
    !remediationTests.includes("ExplorerRestart_UsesOnlyInjectedRunnerAndCanRecover") ||
    !remediationTests.includes('[Trait("Layer", "EcoQa35Synthetic")]') ||
    !read("src/App/Core/ActionExecutor.Storage.cs").includes("systemRemediationService.ReduceHibernateAsync()") ||
    !read("src/App/Core/ActionExecutor.NetworkStartup.cs").includes("systemRemediationService.RestartExplorerAsync()"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing simulated system remediation command coverage");
const usbService = read("src/App/Services/UsbDiagnosticsService.cs");
const usbEvaluation = usbService.indexOf("if (evaluationMode)");
const usbNativeRun = usbService.indexOf("var restart = await RunAsync(");
const usbTests = read("tests/App.Tests/EcoQa35UsbRestartTests.cs");
if (usbEvaluation < 0 || usbNativeRun < usbEvaluation ||
    !usbTests.includes("UsbRestart_EvaluationUsesSyntheticDeviceAndNeverRunsPnPUtil") ||
    !usbTests.includes("UsbRestart_RefusesProtectedAndUnknownSyntheticDevices") ||
    !usbTests.includes("new UsbDiagnosticsService(evaluationMode: true)") ||
    !usbTests.includes("Assert.Equal(\"EVALUATION\", result.Status)"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing synthetic USB restart coverage");
const storageTests = read("tests/App.Tests/StoragePreviewInterlockTests.cs");
if (!storageTests.includes("CleanupWithPreview_ConfirmsAndRecyclesOnlySyntheticFile") ||
    !storageTests.includes('[Trait("Layer", "EcoQa35Synthetic")]') ||
    !storageTests.includes("Assert.Contains(\"previsual\", secondExecution.Error!") ||
    !storageTests.includes("SHA256.HashData(await File.ReadAllBytesAsync(source))") ||
    !read("src/App/Core/HostBridge.cs").includes("storageRecycleFixture"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing isolated storage cleanup positive and rollback");
if (!read("scripts/eco-qa35.ps1").includes("ECO_QA35_GUARDS_OK"))
  throw new Error("ECO_QA_CONTRACT_FAIL missing evidence-producing runner");

console.log("ECO_QA35_CONTRACT_OK catalogWrite=35 rejectionCases=140 positiveGroups=16 positiveWriteIds=35 (storage cleanup isolated recycler; owned-child MemoryTrim; injected system adapters; synthetic USB restart); physicalWritesNotCertified");
