using System.Security.Principal;

namespace ZapretManager.Core;

public static class AdminHelper
{
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        var p = new WindowsPrincipal(id);
        return p.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RequireAdmin()
    {
        if (!IsAdmin())
        {
            UI.ConsoleMenu.WriteError("Требуются права администратора. Запустите от имени администратора.");
            Environment.Exit(1);
        }
    }
}
