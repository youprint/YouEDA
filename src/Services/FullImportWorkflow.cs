namespace EasyEdaAltiumGrabber.Services;

/// <summary>Never begin conversion until lookup/pricing has joined and permits continuation.</summary>
public static class FullImportWorkflow
{
    public static async Task RunAsync(Func<Task<bool>> lookup, Func<Task> export)
    {
        if (!await lookup())
        {
            ImportDiagnostics.Record("workflow.stopped-before-export");
            return;
        }
        ImportDiagnostics.Record("workflow.lookup-finished-starting-export");
        await export();
        ImportDiagnostics.Record("workflow.returned");
    }
}
