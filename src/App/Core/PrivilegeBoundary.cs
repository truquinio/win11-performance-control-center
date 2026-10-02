using System.Security.Principal;
using Win11PerformanceControlCenter.App.Models;

namespace Win11PerformanceControlCenter.App.Core;

public sealed class PrivilegeBoundary
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Validate(ActionDefinition action)
    {
        if (action.RequiresAdmin && !IsElevated)
            throw new InvalidOperationException(
                "La acción requiere elevación UAC. El helper elevado todavía está deshabilitado en esta fase.");
    }
}
