using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Sidecue.Desktop.ViewModels;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class ShellCompositionTests
{
    [Fact]
    public async Task ShellLoadsCompiledXamlAndResolvesItsViewModelBinding()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    // An unshown Window is collapsed, so lay out its real content
                    // directly and drain WPF's queued binding work on this STA.
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    content.Measure(new Size(640, 480));
                    content.Arrange(new Rect(0, 0, 640, 480));
                    content.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                    var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                    var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));
                    var binding = status.GetBindingExpression(TextBlock.TextProperty);
                    Assert.NotNull(binding);
                    binding.UpdateTarget();

                    Assert.Equal(BindingStatus.Active, binding.Status);
                    Assert.Same(viewModel, binding.DataItem);
                    Assert.Equal(viewModel.Status, status.Text);
                    Assert.True(status.ActualHeight > 0, "The status text must participate in the shell layout.");
                }
                finally
                {
                    window.Close();
                }

                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
