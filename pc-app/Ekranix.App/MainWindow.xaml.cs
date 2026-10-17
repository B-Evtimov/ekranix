using System.Windows;
using Ekranix.Core;

namespace Ekranix.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false;
        StatusText.Text = "Търся...";

        var tvs = await Discovery.ScanAsync(TimeSpan.FromSeconds(2));
        TvList.ItemsSource = tvs;

        StatusText.Text = tvs.Count == 0 ? "Няма намерени телевизори." : $"Намерени: {tvs.Count}";
        ScanBtn.IsEnabled = true;
    }
}