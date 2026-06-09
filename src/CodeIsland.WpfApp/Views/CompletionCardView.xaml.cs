using CodeIsland.WpfApp.ViewModels;

namespace CodeIsland.WpfApp.Views;

public partial class CompletionCardView
{
    public CompletionCardView()
    {
        InitializeComponent();
        MouseEnter += (_, _) => (DataContext as WpfAppState)?.PauseCompletionAutoCollapse();
        MouseLeave += (_, _) => (DataContext as WpfAppState)?.ResumeCompletionAutoCollapse();
    }
}
