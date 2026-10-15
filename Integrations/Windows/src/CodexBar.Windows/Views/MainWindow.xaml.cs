using System.Windows;
using System.Windows.Input;
using CodexBar.Windows.ViewModels;

namespace CodexBar.Windows.Views;

public partial class MainWindow : Window
{
    private readonly AppViewModel _viewModel;

    public MainWindow(AppViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Root visual used by the headless screenshot renderer. This is the
    /// bordered surface, not the inner grid, so the rendered PNG carries the
    /// panel background instead of transparent black.
    /// </summary>
    public FrameworkElement RenderRoot => WindowSurface;

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.HideCommand.Execute(null);
            e.Handled = true;
        }
    }
}
