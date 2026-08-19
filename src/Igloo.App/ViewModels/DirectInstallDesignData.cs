#if DEBUG
using System.Windows.Input;
namespace Igloo.App.ViewModels;

/// <summary>Design-time stand-in for <see cref="DirectInstallViewModel"/>.</summary>
/// <remarks>
/// DirectInstallPage shows three mutually exclusive panels - running, complete, error -
/// each collapsed until a DataTrigger fires. The designer instantiates no view model, so
/// none of them fire and the page always previews in the same state. DirectInstallViewModel
/// itself cannot fill the gap: its constructor takes dependencies, so the designer cannot
/// create one.
///
/// Property names mirror the bindings in the page. Flip the flags below to preview a
/// different panel. Referenced only from d:DataContext, which mc:Ignorable strips before
/// the XAML is compiled, and excluded from Release entirely.
/// </remarks>
public sealed class DirectInstallDesignData
{
    public bool IsComplete { get; set; } = true;
    public bool HasError { get; set; }
    public bool HasErrorDetail { get; set; }

    public string PhaseDisplay { get; set; } = "Ready to reboot";
    public double ProgressPercent { get; set; } = 100;
    public bool IsProgressIndeterminate { get; set; }
    public long BytesWritten { get; set; } = 3_221_225_472;
    public long BytesTotal { get; set; } = 3_221_225_472;

    public string ErrorMessage { get; set; } = "Could not shrink the Windows partition.";
    public string ErrorDetail { get; set; } = "diskpart: the volume has unmovable files at the end.";

    public bool LogsExpanded { get; set; } = true;
    public string LogTail { get; } =
        "2026-08-18 21:14:02 [INF] Shrinking Windows partition\n" +
        "2026-08-18 21:14:48 [INF] Created installer partition (2.0 GB)\n" +
        "2026-08-18 21:16:30 [INF] Copying installer image\n" +
        "2026-08-18 21:19:05 [INF] Registering UEFI boot entry\n" +
        "2026-08-18 21:19:06 [INF] Ready to reboot";

    public ICommand OpenLogFolderCommand { get; } = new DesignCommand();
    public ICommand RefreshLogTailCommand { get; } = new DesignCommand();
}
#endif
