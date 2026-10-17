using System.ComponentModel;
using System.Runtime.CompilerServices;
using Igloo.Mascot.Core;

namespace Igloo.Mascot.Wpf;

public sealed class TaskCardViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty, _subtitle = string.Empty;
    private TaskCardState _state;
    private double? _progress = 0;
    private double _durationSeconds = 6;

    public string Title { get => _title; set { if (_title != value) { _title = value; Changed(); } } }
    public string Subtitle { get => _subtitle; set { if (_subtitle != value) { _subtitle = value; Changed(); } } }
    public TaskCardState State { get => _state; set { if (_state != value) { _state = value; Changed(); } } }
    public double? Progress { get => _progress; set { if (_progress != value) { _progress = value.HasValue ? Math.Clamp(value.Value, 0, 1) : null; Changed(); } } }
    public double DurationSeconds { get => _durationSeconds; set { if (_durationSeconds != value) { _durationSeconds = Math.Max(.1, value); Changed(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
