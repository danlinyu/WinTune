using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinTune.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InnerDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        DataGrid_PreviewMouseWheel(sender, e);

    private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DataGrid dg || e.Handled) return;
        var sv = FindVisualChild<ScrollViewer>(dg);
        if (sv is null) return;
        bool horizontal = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double before = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
        if (horizontal)
            sv.ScrollToHorizontalOffset(before - e.Delta);
        else
            sv.ScrollToVerticalOffset(before - e.Delta);
        sv.UpdateLayout();
        double after = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
        if (Math.Abs(after - before) > 0.001) e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
