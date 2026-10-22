using Citrus.Windows.Updates;

namespace Citrus.Windows;

public partial class App
{
    private async Task<bool> TryCleanupUninstallBackupAsync(IReadOnlyList<string> arguments)
    {
        int? result = await UpdateBackupCleanup.RunUninstallCleanupAsync(arguments);
        if (result is null) return false;
        Shutdown(result.Value);
        return true;
    }
}
