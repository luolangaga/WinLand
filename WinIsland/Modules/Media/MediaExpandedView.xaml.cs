using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinIsland.Modules.Media;

public sealed partial class MediaExpandedView : UserControl
{
    internal MediaViewModel ViewModel { get; }

    public MediaExpandedView(MediaViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => ViewModel.SkipPrevious?.Invoke();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayPause?.Invoke();

    private void Next_Click(object sender, RoutedEventArgs e) => ViewModel.SkipNext?.Invoke();
}
