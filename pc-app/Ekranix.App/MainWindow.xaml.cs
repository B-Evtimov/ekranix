using System.Windows;
using System.Windows.Input;
using Ekranix.Core;

namespace Ekranix.App;

public partial class MainWindow : Window
{
    private ControlClient? _client;

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
            if (tvs.Count == 1) TvList.SelectedIndex = 0;
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

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Connect_Click(sender, e);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (TvList.SelectedItem is not TvDevice tv)
        {
            StatusText.Text = "Избери телевизор от списъка.";
            return;
        }

        var code = CodeBox.Text.Replace(" ", "");
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            StatusText.Text = "Кодът трябва да е 6 цифри.";
            return;
        }

        ConnectBtn.IsEnabled = false;
        ScanBtn.IsEnabled = false;
        StatusText.Text = $"Свързване с {tv.Name}...";

        try
        {
            var result = await ControlClient.PairAsync(tv, code, Environment.MachineName);
            if (!result.Success)
            {
                StatusText.Text = result.Error;
                CodeBox.SelectAll();
                CodeBox.Focus();
                return;
            }

            _client = result.Client!;
            _client.Disconnected += reason => { Dispatcher.InvokeAsync(() => ShowList(reason)); };
            _client.Start();
            ShowConnected(tv);
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            ScanBtn.IsEnabled = true;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is { } c) await c.StopAsync();
    }

    private void ShowConnected(TvDevice tv)
    {
        ConnectedText.Text = $"Свързан с {tv.Name}";
        CodeBox.Clear();
        ListPanel.Visibility = Visibility.Collapsed;
        ConnectedPanel.Visibility = Visibility.Visible;
    }

    private void ShowList(string reason)
    {
        _client = null;
        ConnectedPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        StatusText.Text = reason;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Казваме на телевизора, че си тръгваме, иначе ще чака 3 s heartbeat
        _client?.StopAsync().Wait(500);
        base.OnClosed(e);
    }
}