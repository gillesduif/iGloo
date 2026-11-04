using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Data;
using Igloo.Community.App.Controls;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class CoverFlowSelectionTests
{
    [Fact]
    public void RebuildingCarouselKeepsSelectionBoundToWizard()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var state = new SelectionState();
                var carousel = new CoverFlow3DControl();
                BindingOperations.SetBinding(carousel, CoverFlow3DControl.SelectedItemProperty,
                    new Binding(nameof(SelectionState.Selected))
                    {
                        Source = state,
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    });

                carousel.ItemsSource = new[] { "debian", "mint" };
                Assert.NotNull(BindingOperations.GetBindingExpression(carousel,
                    CoverFlow3DControl.SelectedItemProperty));
                Assert.Equal("mint", state.Selected);

                carousel.SelectedIndex = 0;
                Assert.Equal("debian", state.Selected);

                carousel.ItemsSource = new[] { "fedora", "mint" };
                Assert.NotNull(BindingOperations.GetBindingExpression(carousel,
                    CoverFlow3DControl.SelectedItemProperty));
                Assert.Equal("mint", state.Selected);
            }
#pragma warning disable CA1031 // Marshal an assertion failure from the STA test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class SelectionState : INotifyPropertyChanged
    {
        private object? _selected;
        public event PropertyChangedEventHandler? PropertyChanged;

        public object? Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }
}
