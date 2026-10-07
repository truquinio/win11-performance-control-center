using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class ActionCatalog
{
    private readonly IReadOnlyDictionary<string, ActionDefinition> _actions;

    public ActionCatalog()
    {
        var actions = new[]
        {
            new ActionDefinition("system.health.scan", "Diagnóstico del sistema",
                "Obtiene una línea base segura del equipo.", ActionCategory.System, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("system.integrity.check", "Comprobar integridad de Windows",
                "Ejecuta DISM /CheckHealth en modo lectura; requiere elevación puntual.", ActionCategory.System, ActionRisk.SAFE, true, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("system.reliability.analyze", "Reliability Analyzer",
                "Correlaciona reinicios, apagados y eventos críticos.", ActionCategory.Reliability, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("memory.analyze", "Analizar memoria",
                "Mide RAM física, disponible y presión general.", ActionCategory.Memory, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("memory.trim.preview", "Previsualizar MemoryTrim",
                "Identifica procesos con working set alto sin modificar memoria.", ActionCategory.Memory, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.DRY_RUN),
            new ActionDefinition("memory.trim", "MemoryTrim seleccionado",
                "Recorta working sets sólo de procesos seleccionados y elegibles; el efecto es transitorio.", ActionCategory.Memory, ActionRisk.CAUTION, false, ConnectivityRequirement.OFFLINE, false, ActionMode.WRITE,
                [new ActionParameterDefinition("processIds", ActionParameterType.INTEGER_ARRAY, true, "PIDs seleccionados desde la previsualización"), new ActionParameterDefinition("confirmed", ActionParameterType.BOOLEAN, true, "Confirmación explícita de la operación")]),
            new ActionDefinition("memory.pagefile.analyze", "Analizar archivo de paginación",
                "Lee configuración y uso actual del pagefile sin modificarlo.", ActionCategory.Memory, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("cpu.ecoqos.analyze", "Analizar EcoQoS",
                "Detecta candidatos sin aplicar cambios.", ActionCategory.CPU, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("cpu.ecoqos.apply", "Aplicar EcoQoS seleccionado",
                "Aplica ExecutionSpeed throttling sólo a procesos seleccionados y elegibles.", ActionCategory.CPU, ActionRisk.CAUTION, false, ConnectivityRequirement.OFFLINE, true, ActionMode.WRITE,
                [new ActionParameterDefinition("processIds", ActionParameterType.INTEGER_ARRAY, true, "PIDs seleccionados desde el análisis"), new ActionParameterDefinition("confirmed", ActionParameterType.BOOLEAN, true, "Confirmación explícita de la operación")]),
            new ActionDefinition("cpu.ecoqos.restore", "Restaurar EcoQoS",
                "Restaura el estado previo registrado para procesos seleccionados.", ActionCategory.CPU, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, true, ActionMode.WRITE,
                [new ActionParameterDefinition("processIds", ActionParameterType.INTEGER_ARRAY, true, "PIDs previamente modificados por la app"), new ActionParameterDefinition("confirmed", ActionParameterType.BOOLEAN, true, "Confirmación explícita del rollback")]),
            new ActionDefinition("disk.scan", "Analizar almacenamiento",
                "Mide espacio y temporales regenerables.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.cleanup.safe", "Limpieza segura",
                "Previsualiza la limpieza; no borra archivos en esta fase.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, true, ActionMode.DRY_RUN),
            new ActionDefinition("disk.cleanup.execute", "Eliminar cachés regenerables",
                "Requiere previsualización y confirmación. Solo limpia archivos antiguos de rutas permitidas, nunca extensiones instaladas, perfiles, sesiones ni documentos.", ActionCategory.Storage, ActionRisk.CAUTION, false, ConnectivityRequirement.OFFLINE, false, ActionMode.WRITE,
                [new ActionParameterDefinition("confirmed", ActionParameterType.BOOLEAN, true, "Confirmación explícita tras previsualización")]),
            new ActionDefinition("disk.appdata.rank", "Ranking de AppData Local",
                "Mide las mayores carpetas sin borrar nada ni inferir que sean residuos.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.volumes.audit", "Auditar C: / D: y volúmenes",
                "Mide espacio total/libre y clasifica presión de almacenamiento en todos los volúmenes fijos.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.storage.watch", "Storage Watch",
                "Compara espacio libre contra el baseline anterior y detecta crecimiento anormal sin borrar archivos.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.hotspots.scan", "Buscar hotspots de disco",
                "Escaneo read-only acotado por tiempo en volúmenes con poco espacio; los tamaños parciales se marcan como tales.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.hibernate.status", "Estado de hibernación",
                "Consulta los estados de suspensión e inicio rápido, sin cambios.", ActionCategory.Storage, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("disk.hibernate.reduce", "Hibernación reducida",
                "Con UAC: reduce hiberfil.sys, conserva Inicio rápido pero DESHABILITA la hibernación completa.", ActionCategory.Storage, ActionRisk.CAUTION, true, ConnectivityRequirement.OFFLINE, false, ActionMode.WRITE),
            new ActionDefinition("network.test", "Analizar red",
                "Mide el adaptador activo sin cambiar su configuración.", ActionCategory.Network, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("drivers.analyze", "Analizar drivers",
                "Detecta dispositivos con códigos de problema mediante WMI.", ActionCategory.Drivers, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("system.activation.analyze", "Comprobar activación",
                "Lee el estado de licencia de Windows sin modificarlo.", ActionCategory.System, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("browsers.inventory", "Inventario de navegadores",
                "Detecta navegadores y perfiles locales sin abrirlos ni modificarlos.", ActionCategory.Browsers, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("browsers.extensions.health", "Integridad de extensiones Edge",
                "Detecta instalaciones, datos huérfanos y extensiones unpacked sin borrar ni modificar el perfil.", ActionCategory.Browsers, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("multimedia.inventory", "Inventario multimedia",
                "Enumera dispositivos de audio y vídeo registrados por Windows.", ActionCategory.Multimedia, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("startup.audit", "Auditar inicio y servicios",
                "Enumera startup commands y servicios automáticos sin modificarlos.", ActionCategory.Startup, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("windows.update.audit", "Auditar Windows Update",
                "Resume eventos recientes de instalación y fallo de Windows Update.", ActionCategory.WindowsUpdate, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("apps.inventory", "Inventario de aplicaciones",
                "Enumera aplicaciones instaladas desde el registro sin desinstalar nada.", ActionCategory.Apps, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("privacy.audit", "Auditar privacidad",
                "Lee una selección documentada de configuraciones de privacidad sin juzgarlas ni modificarlas.", ActionCategory.Privacy, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("developer.audit", "Auditar developer tooling",
                "Detecta toolchains comunes y sus versiones desde PATH.", ActionCategory.Developer, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("lab.reliability.status", "Reliability Lab",
                "Verifica regresiones históricas y barreras de seguridad sin ejecutar escenarios destructivos sobre el Windows real.", ActionCategory.Developer, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("thermal.audit", "Auditar energía y temperaturas",
                "Lee el plan de energía activo y sensores ACPI disponibles.", ActionCategory.Thermal, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("boot.audit", "Auditar arranque",
                "Lee eventos de Diagnostics-Performance del arranque.", ActionCategory.Boot, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("sleepresume.audit", "Auditar suspensión y reanudación",
                "Construye una timeline de eventos de sleep/resume.", ActionCategory.SleepResume, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("explorer.audit", "Auditar Explorer",
                "Mide memoria, threads, handles y respuesta de explorer.exe.", ActionCategory.Explorer, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ),
            new ActionDefinition("backup.status", "Estado de backup y rollback",
                "Detecta operaciones incompletas y snapshots de rollback mantenidos por la app.", ActionCategory.Backup, ActionRisk.SAFE, false, ConnectivityRequirement.OFFLINE, false, ActionMode.READ)
        };

        _actions = actions.ToDictionary(action => action.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ActionDefinition> All => [.. _actions.Values];

    public ActionDefinition GetRequired(string id)
    {
        if (!_actions.TryGetValue(id, out var action))
            throw new InvalidOperationException("Action ID no permitida: " + id);
        return action;
    }
}
