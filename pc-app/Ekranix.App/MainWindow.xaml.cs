using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ekranix.Core;
using Wpf.Ui.Controls;

namespace Ekranix.App;

public partial class MainWindow : FluentWindow
{
    private static readonly (string Name, int Mbps, int Fps, string Hint)[] Presets =
    {
        ("Ултра",       40, 60, "40 Mbps · 60 fps · за кабел или силен 5 GHz Wi-Fi"),
        ("Високо",      25, 60, "25 Mbps · 60 fps · препоръчително"),
        ("Балансирано", 15, 60, "15 Mbps · 60 fps · за по-слаб Wi-Fi"),
        ("Икономично",   8, 30, "8 Mbps · 30 fps · за 2.4 GHz Wi-Fi"),
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FrameworkElement[] _pages = Array.Empty<FrameworkElement>();

    private Task<List<MonitorInfo>>? _monitorsTask;
    private TvDevice? _tv;
    private ControlClient? _client;
    private ScreenStreamer? _streamer;
    private string? _pendingError;
    private DateTime _liveSince;
    private bool _pairing;

    public MainWindow()
    {
        InitializeComponent();
        _pages = new FrameworkElement[] { HomePage, PairPage, SetupPage, LivePage };
        _liveTimer.Tick += (_, _) => UpdateTimer();
    }

    // ───────────── Старт ─────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        QualityBox.ItemsSource = Presets.Select(p => p.Name).ToList();
        QualityBox.SelectedIndex = Math.Clamp(_settings.QualityIndex, 0, Presets.Length - 1);
        CursorToggle.IsChecked = _settings.ShowCursor;

        // Мониторите се откриват на заден план, докато потребителят избира телевизор
        _monitorsTask = LoadMonitorsAsync();
        await ScanAsync();
    }

    private static async Task<List<MonitorInfo>> LoadMonitorsAsync()
    {
        var ffmpeg = Ffmpeg.FindExecutable();
        return ffmpeg is null ? new List<MonitorInfo>() : await Ffmpeg.ListMonitorsAsync(ffmpeg);
    }

    // ───────────── 1. Начало ─────────────

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        RefreshBtn.IsEnabled = false;
        TvItems.ItemsSource = null;
        EmptyState.Visibility = Visibility.Collapsed;
        ScanningState.Visibility = Visibility.Visible;

        try
        {
            var tvs = await Discovery.ScanAsync(TimeSpan.FromSeconds(2));
            TvItems.ItemsSource = tvs;
            EmptyState.Visibility = tvs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowHomeInfo("Грешка при търсене", ex.Message, InfoBarSeverity.Error);
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanningState.Visibility = Visibility.Collapsed;
            RefreshBtn.IsEnabled = true;
        }
    }

    private void TvCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TvDevice tv) return;

        _tv = tv;
        PairTvName.Text = tv.Name;
        CodeBox.Clear();
        PairInfo.IsOpen = false;
        HomeInfo.IsOpen = false;
        Navigate(PairPage);
        Dispatcher.InvokeAsync(() => CodeBox.Focus(), DispatcherPriority.Input);
    }

    private void ShowHomeInfo(string title, string message, InfoBarSeverity severity)
    {
        HomeInfo.Title = title;
        HomeInfo.Message = message;
        HomeInfo.Severity = severity;
        HomeInfo.IsOpen = true;
    }

    // ───────────── 2. Код ─────────────

    private void Back_Click(object sender, RoutedEventArgs e) => Navigate(HomePage);

    private void CodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void CodeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Чистим всичко, което не е цифра (напр. "482 913" при поставяне)
        var digits = new string(CodeBox.Text.Where(char.IsDigit).Take(6).ToArray());
        if (digits != CodeBox.Text)
        {
            CodeBox.Text = digits;
            CodeBox.CaretIndex = digits.Length;
            return;
        }

        PairInfo.IsOpen = false;
        PairBtn.IsEnabled = digits.Length == 6 && !_pairing;

        // Свързваме се автоматично при 6-тата цифра
        if (digits.Length == 6 && !_pairing) Pair_Click(sender, e);
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PairBtn.IsEnabled) Pair_Click(sender, e);
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_tv is null || _pairing) return;
        var code = CodeBox.Text;
        if (code.Length != 6) return;

        _pairing = true;
        PairBtn.IsEnabled = false;
        CodeBox.IsEnabled = false;
        PairProgress.Visibility = Visibility.Visible;

        try
        {
            var result = await ControlClient.PairAsync(_tv, code, Environment.MachineName);
            if (!result.Success)
            {
                PairInfo.Title = "Неуспешно свързване";
                PairInfo.Message = result.Error ?? "";
                PairInfo.IsOpen = true;
                return;
            }

            _client = result.Client!;
            _client.Disconnected += reason => { Dispatcher.InvokeAsync(() => OnDisconnected(reason)); };
            _client.Start();
            await OpenSetupAsync();
        }
        finally
        {
            _pairing = false;
            CodeBox.IsEnabled = true;
            PairProgress.Visibility = Visibility.Collapsed;
            PairBtn.IsEnabled = CodeBox.Text.Length == 6;
            if (PairInfo.IsOpen)
            {
                CodeBox.SelectAll();
                CodeBox.Focus();
            }
        }
    }

    // ───────────── 3. Настройки ─────────────

    private async Task OpenSetupAsync()
    {
        SetupSubtitle.Text = $"Свързан с {_tv!.Name}";
        SetupInfo.IsOpen = false;
        StartBtn.IsEnabled = false;
        MonitorList.ItemsSource = null;
        MonitorLoading.Visibility = Visibility.Visible;
        Navigate(SetupPage);

        var monitors = await (_monitorsTask ??= LoadMonitorsAsync());
        if (monitors.Count == 0)
        {
            // Втори опит: може ffmpeg да е бил инсталиран след стартиране
            _monitorsTask = LoadMonitorsAsync();
            monitors = await _monitorsTask;
        }

        MonitorLoading.Visibility = Visibility.Collapsed;

        if (monitors.Count == 0)
        {
            SetupInfo.Title = "Не мога да прихвана екрана";
            SetupInfo.Message = Ffmpeg.FindExecutable() is null
                ? "Не намирам ffmpeg. Инсталирай го с: winget install Gyan.FFmpeg"
                : "Не открих монитори.";
            SetupInfo.IsOpen = true;
            return;
        }

        MonitorList.ItemsSource = monitors;
        MonitorList.SelectedItem = monitors.FirstOrDefault(m => m.Index == _settings.MonitorIndex) ?? monitors[0];
        StartBtn.IsEnabled = true;
    }

    private void QualityBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QualityBox.SelectedIndex >= 0) QualityHint.Text = Presets[QualityBox.SelectedIndex].Hint;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_tv is null || _client is null || MonitorList.SelectedItem is not MonitorInfo monitor) return;

        var preset = Presets[Math.Max(QualityBox.SelectedIndex, 0)];
        var showCursor = CursorToggle.IsChecked == true;

        _settings.MonitorIndex = monitor.Index;
        _settings.QualityIndex = QualityBox.SelectedIndex;
        _settings.ShowCursor = showCursor;
        _settings.Save();

        LiveTvName.Text = _tv.Name;
        LiveMonitor.Text = $"{monitor.Title} · {monitor.Resolution}";
        LiveQuality.Text = $"{preset.Name} · {preset.Mbps} Mbps · {preset.Fps} fps";
        LiveEncoder.Text = "…";
        LiveTimer.Text = "--:--";
        LiveBadge.Text = "СТАРТИРАНЕ";
        Navigate(LivePage);

        var streamer = new ScreenStreamer();
        streamer.Failed += reason => { Dispatcher.InvokeAsync(() => OnStreamFailed(reason)); };
        _streamer = streamer;

        try
        {
            await streamer.StartAsync(_tv, new StreamOptions(
                monitor.Index, preset.Fps, preset.Mbps, showCursor, monitor.NeedsScaling));

            LiveEncoder.Text = Ffmpeg.FriendlyEncoderName(streamer.Encoder);
            LiveBadge.Text = "НА ЖИВО";
            _liveSince = DateTime.Now;
            UpdateTimer();
            _liveTimer.Start();
            LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        catch (Exception ex)
        {
            OnStreamFailed(ex.Message);
        }
    }

    // ───────────── 4. На живо ─────────────

    private void UpdateTimer()
    {
        var t = DateTime.Now - _liveSince;
        LiveTimer.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is { } c) await c.StopAsync();
    }

    private async void OnStreamFailed(string reason)
    {
        if (_client is { } c)
        {
            _pendingError = reason;
            await c.StopAsync();
        }
        else
        {
            ShowHomeInfo("Предаването спря", reason, InfoBarSeverity.Error);
        }
    }

    private void OnDisconnected(string reason)
    {
        _streamer?.Stop();
        _streamer = null;
        _client = null;
        _liveTimer.Stop();
        LiveDot.BeginAnimation(OpacityProperty, null);

        Navigate(HomePage);
        if (_pendingError is not null)
            ShowHomeInfo("Предаването спря", _pendingError, InfoBarSeverity.Error);
        else
            ShowHomeInfo("Връзката приключи", reason, InfoBarSeverity.Informational);
        _pendingError = null;
    }

    // ───────────── Общи ─────────────

    private void Navigate(FrameworkElement page)
    {
        foreach (var p in _pages) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _streamer?.Stop();
        _client?.StopAsync().Wait(500);
        base.OnClosed(e);
    }
}