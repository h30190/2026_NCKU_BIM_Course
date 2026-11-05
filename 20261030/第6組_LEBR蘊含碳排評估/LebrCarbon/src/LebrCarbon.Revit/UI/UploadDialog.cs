using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LebrCarbon.Revit.UI;

/// <summary>「上傳到控制台」前先問快照名稱，並可勾選一併附上 Excel 計算書（控制台計畫書第 7 節）。</summary>
public sealed class UploadDialog : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(4, 3, 4, 3) };
    private readonly CheckBox _excel = new() { Content = "一併附上 Excel 計算書", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };

    public string SnapshotName => _name.Text.Trim();
    public bool AttachExcel => _excel.IsChecked == true;

    public UploadDialog(string defaultName, string backendUrl)
    {
        Title = "上傳到控制台";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft JhengHei UI");
        FontSize = 13;

        _name.Text = defaultName;

        var ok = new Button { Content = "上傳", IsDefault = true, MinWidth = 80, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        ok.Click += (_, _) =>
        {
            if (SnapshotName.Length == 0)
            {
                MessageBox.Show(this, "請輸入快照名稱，例如「方案 B：輕隔間」。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "快照名稱（同一個模型的快照會並列在控制台，方便比較方案）" , TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_name);
        panel.Children.Add(_excel);
        panel.Children.Add(new TextBlock
        {
            Text = $"後端：{backendUrl}\n上傳的是目前「計算」的結果；上傳後不可修改。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
