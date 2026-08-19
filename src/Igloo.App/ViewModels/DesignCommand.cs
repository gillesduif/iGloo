#if DEBUG
using System.Windows.Input;

namespace Igloo.App.ViewModels;

/// <summary>A command that does nothing, so design-time buttons render enabled.</summary>
/// <remarks>
/// Binding to a command the design data does not expose leaves Command null, which
/// disables the button - the preview then shows every action greyed out and reads as
/// broken. This exists only to make that preview honest.
/// </remarks>
internal sealed class DesignCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) { }
}
#endif
