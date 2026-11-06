namespace Sidecue.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    public string Title { get; } = "Sidecue";

    public string Status { get; } = "Meeting features are not available yet.";

    public string Description { get; } = "This version does not capture audio or connect to AI services.";
}
