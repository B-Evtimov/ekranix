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

        try
        {
            var tvs = await Discovery.ScanAsync(TimeSpan.FromSeconds(2));
            TvList.ItemsSource = tvs;
            StatusText.Text = tvs.Count == 0 ? "Няма намерени телевизори." : $"Намерени: {tvs.Count}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Грешка при търсене.";
            MessageBox.Show(ex.ToString(), "Ekranix: грешка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanBtn.IsEnabled = true;
        }
    }
}