using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace BeijingStationConverter;

public sealed class MainWindow : Window
{
    private sealed record LogEntry(string Text, string? RemoteIp, RadioDevice? Device);
    private sealed record DeviceCardView(TextBlock Ip, Border Badge, TextBlock State,
        TextBlock Footer, TextBlock[] Values);

    private enum Page
    {
        Overview,
        Tftp,
        Network,
        Logs
    }

    private static readonly IBrush CanvasBrush = Brush.Parse("#F2F5F3");
    private static readonly IBrush InkBrush = Brush.Parse("#1D302D");
    private static readonly IBrush MutedBrush = Brush.Parse("#6F7F7C");
    private static readonly IBrush TealBrush = Brush.Parse("#23867D");
    private static readonly IBrush OrangeBrush = Brush.Parse("#CE7A45");
    private static readonly IBrush GreenBrush = Brush.Parse("#408B69");
    private static readonly IBrush GoldBrush = Brush.Parse("#B78C35");
    private const double DeviceCardDesignWidth = 420;
    private const double DeviceCardDesignHeight = 320;

    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly UdpLogListener _logListener = new();
    private readonly DispatcherTimer _statusPollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<RadioDevice, DeviceStatus> _deviceStatus = Enum.GetValues<RadioDevice>()
        .ToDictionary(device => device, _ => new DeviceStatus());
    private readonly Queue<LogEntry> _logEntries = new();
    private readonly Dictionary<RadioDevice, DeviceCardView> _deviceCards = new();
    private readonly Dictionary<string, RadioDevice> _logSources = new(StringComparer.Ordinal);
    private readonly Dictionary<Page, Button> _navigation = new();
    private readonly ContentControl _pageHost = new();
    private readonly TextBlock _logStateText = new();
    private ComboBox? _logDeviceFilter;
    private TextBlock? _logFilterStateText;
    private TextBox? _logTextBox;
    private TextBlock? _logActionStateText;
    private Button? _logScrollButton;
    private RadioDevice? _selectedLogDevice;
    private Control? _overviewPage;
    private Border? _sameIpNotice;
    private Page _currentPage = Page.Overview;
    private bool _logStarted;
    private bool _logAutoScroll = true;
    private bool _statusPollRunning;
    private string? _logStartError;

    public MainWindow()
    {
        Title = "北京站变频器 v1.0";
        Width = 1280;
        Height = 820;
        MinWidth = 980;
        MinHeight = 680;
        CanResize = true;
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = false;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        Background = CanvasBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildShell();
        _logListener.PacketReceived += OnLogPacket;
        _statusPollTimer.Tick += async (_, _) =>
        {
            UpdateOverviewCards();
            await RefreshAllDevicesAsync();
        };
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            _statusPollTimer.Start();
            await StartLogListenerAsync();
            await RefreshAllDevicesAsync();
        };
        Closed += async (_, _) =>
        {
            _statusPollTimer.Stop();
            await _logListener.DisposeAsync();
        };
    }

    private void FitWindowToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var workArea = screen.WorkingArea;
        var scale = Math.Max(screen.Scaling, 1.0);
        var width = Math.Min(1280, Math.Max(480, workArea.Width / scale - 48));
        var height = Math.Min(820, Math.Max(360, workArea.Height / scale - 48));
        Width = width;
        Height = height;
        MinWidth = Math.Min(980, width);
        MinHeight = Math.Min(680, height);

        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);
        Position = new PixelPoint(
            workArea.X + (workArea.Width - pixelWidth) / 2,
            workArea.Y + (workArea.Height - pixelHeight) / 2);
    }

    private Control BuildShell()
    {
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        shell.Children.Add(BuildSidebar());

        var main = new Grid { RowDefinitions = new RowDefinitions("72,*") };
        Grid.SetColumn(main, 1);
        main.Children.Add(BuildTopBar());
        Grid.SetRow(_pageHost, 1);
        main.Children.Add(_pageHost);
        shell.Children.Add(main);

        ShowPage(Page.Overview);
        return shell;
    }

    private Control BuildSidebar()
    {
        var sidebarContent = new StackPanel
        {
            Spacing = 8
        };
        sidebarContent.Children.Add(new TextBlock
        {
            Text = "北京站变频器",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(2, 4, 2, 0)
        });
        sidebarContent.Children.Add(new TextBlock
        {
            Text = "v1.0",
            FontSize = 12,
            Foreground = Brush.Parse("#B8CBC5"),
            Margin = new Thickness(2, 0, 2, 24)
        });

        AddNavigation(sidebarContent, Page.Overview, "设备总览");
        AddNavigation(sidebarContent, Page.Tftp, "TFTP 升级");
        AddNavigation(sidebarContent, Page.Network, "变频器IP配置");
        AddNavigation(sidebarContent, Page.Logs, "UDP 日志");

        var spacer = new Border { VerticalAlignment = VerticalAlignment.Stretch };
        sidebarContent.Children.Add(spacer);
        sidebarContent.Children.Add(new TextBlock
        {
            Text = "S / Ka 波段 · 上下变频",
            FontSize = 11,
            Foreground = Brush.Parse("#9DB6AF"),
            Margin = new Thickness(2, 14, 2, 2)
        });
        return new Border
        {
            Background = Brush.Parse("#183530"),
            Padding = new Thickness(18, 22),
            Child = sidebarContent
        };
    }

    private void AddNavigation(StackPanel sidebar, Page page, string label)
    {
        var labelText = new TextBlock
        {
            Text = label,
            Foreground = Brush.Parse("#DCE8E4"),
            FontSize = 14
        };
        var button = new Button
        {
            Content = labelText,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 12),
            Background = Brushes.Transparent,
            Foreground = labelText.Foreground,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            FontSize = 14
        };
        button.PointerEntered += (_, _) =>
        {
            button.Background = Brush.Parse(_currentPage == page ? "#3B806F" : "#275B52");
            labelText.Foreground = Brushes.White;
        };
        button.PointerExited += (_, _) =>
        {
            var selected = _currentPage == page;
            button.Background = selected ? Brush.Parse("#2D6F61") : Brushes.Transparent;
            labelText.Foreground = selected ? Brushes.White : Brush.Parse("#DCE8E4");
        };
        button.Click += (_, _) => ShowPage(page);
        _navigation[page] = button;
        sidebar.Children.Add(button);
    }

    private Control BuildTopBar()
    {
        var top = new Grid
        {
            Background = Brushes.White,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 1)
        };
        var title = new TextBlock
        {
            Text = "北京站变频器 v1.0",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(26, 0)
        };
        top.Children.Add(title);

        var logState = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 24, 0)
        };
        logState.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Brush.Parse("#9AA7A4"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _logStateText.Text = "UDP 10080 启动中";
        _logStateText.FontSize = 12;
        _logStateText.Foreground = MutedBrush;
        logState.Children.Add(_logStateText);
        Grid.SetColumn(logState, 1);
        top.Children.Add(logState);
        return top;
    }

    private void ShowPage(Page page)
    {
        _currentPage = page;
        foreach (var (key, button) in _navigation)
        {
            var selected = key == page;
            button.Background = selected ? Brush.Parse("#2D6F61") : Brushes.Transparent;
            button.Foreground = selected ? Brushes.White : Brush.Parse("#DCE8E4");
            if (button.Content is TextBlock label)
                label.Foreground = selected ? Brushes.White : Brush.Parse("#DCE8E4");
        }

        var nextPage = page switch
        {
            Page.Overview => _overviewPage ?? BuildOverviewPage(),
            Page.Tftp => BuildTftpPage(),
            Page.Network => BuildNetworkPage(),
            _ => BuildLogsPage()
        };
        _pageHost.Content = nextPage;
        if (page == Page.Overview)
        {
            // 仅在导航时调整提示布局，周期刷新只更新现有卡片内容。
            if (_sameIpNotice is not null)
                _sameIpNotice.IsVisible = HasDuplicateDeviceIps();
            UpdateOverviewCards();
        }
    }

    private Control BuildOverviewPage()
    {
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 14,
            Margin = new Thickness(26, 22, 26, 26)
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        header.Children.Add(PageHeading("设备总览", "自动轮询四类变频器状态，UDP 10080 接收设备日志"));
        var refresh = ActionButton("刷新全部设备", TealBrush, async () => await RefreshAllDevicesAsync());
        refresh.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        body.Children.Add(header);

        _sameIpNotice = Notice("多台设备当前使用相同 IP。请在变频器IP配置页或设备卡片中确认目标地址，再进行定向设置。", OrangeBrush);
        _sameIpNotice.IsVisible = HasDuplicateDeviceIps();
        Grid.SetRow(_sameIpNotice, 1);
        body.Children.Add(_sameIpNotice);

        var cards = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("*,*"),
            ColumnSpacing = 16,
            RowSpacing = 16
        };
        cards.Children.Add(BuildDeviceCard(RadioDevice.SUp, TealBrush));
        var sDown = BuildDeviceCard(RadioDevice.SDown, OrangeBrush);
        Grid.SetColumn(sDown, 1);
        cards.Children.Add(sDown);
        var kaUp = BuildDeviceCard(RadioDevice.KaUp, GoldBrush);
        Grid.SetRow(kaUp, 1);
        cards.Children.Add(kaUp);
        var kaDown = BuildDeviceCard(RadioDevice.KaDown, GreenBrush);
        Grid.SetRow(kaDown, 1);
        Grid.SetColumn(kaDown, 1);
        cards.Children.Add(kaDown);
        Grid.SetRow(cards, 2);
        body.Children.Add(cards);
        _overviewPage = body;
        return body;
    }

    private Button BuildDeviceCard(RadioDevice device, IBrush accent)
    {
        // 卡片共用紧凑设计尺寸，窗口缩小时连同文字和间距一起等比缩放。
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("40,2,*,20"),
            RowSpacing = 8
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = RadioDeviceInfo.DisplayName(device),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = InkBrush
        });
        var ipText = new TextBlock { FontSize = 11, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        heading.Children.Add(ipText);
        header.Children.Add(heading);
        var stateText = new TextBlock { FontSize = 11 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 5),
            VerticalAlignment = VerticalAlignment.Top,
            Child = stateText
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        content.Children.Add(header);

        var accentLine = new Border { Height = 2, Background = accent, Opacity = 0.85 };
        Grid.SetRow(accentLine, 1);
        content.Children.Add(accentLine);

        var metrics = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("*,*,*,*,*"),
            ColumnSpacing = 14,
            RowSpacing = 6
        };
        var labels = GetCardLabels(device);
        var values = labels.Select((label, index) => AddMetric(metrics, index % 3, index / 3, label)).ToArray();
        Grid.SetRow(metrics, 2);
        content.Children.Add(metrics);
        var footer = new TextBlock
        {
            Foreground = MutedBrush,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(footer, 3);
        content.Children.Add(footer);
        _deviceCards[device] = new DeviceCardView(ipText, badge, stateText, footer, values);

        var cardLayout = new Border
        {
            Width = DeviceCardDesignWidth,
            Height = DeviceCardDesignHeight,
            Padding = new Thickness(16, 12),
            Child = content
        };
        var scalableContent = new Viewbox
        {
            Child = cardLayout,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly
        };
        var cardViewport = new Border { Child = scalableContent };
        // 使用外层可用尺寸，保证等比缩放后的内容仍铺满卡片。
        cardViewport.SizeChanged += (_, args) =>
        {
            var size = args.NewSize;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var scale = Math.Min(1, Math.Min(size.Width / DeviceCardDesignWidth, size.Height / DeviceCardDesignHeight));
            cardLayout.Width = size.Width / scale;
            cardLayout.Height = size.Height / scale;
        };

        var card = new Button
        {
            Content = cardViewport,
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#DDE5E2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0)
        };
        card.Click += (_, _) => OpenDeviceConfiguration(device);
        return card;
    }

    private static string[] GetCardLabels(RadioDevice device)
    {
        if (RadioDeviceInfo.IsKaDevice(device))
            return RadioDeviceInfo.HasSecondAttenuator(device)
                ? new[]
                {
                    "频率", "10 MHz", "中频",
                    "ATT1", "ATT2", "本振 2594_650",
                    "本振 2594", "本振 2595", "2595 偏移",
                    "2594_650", "2594_IF",
                    "2595", "2594 功率码", "2595 功率码"
                }
                : new[]
                {
                    "频率", "10 MHz", "中频",
                    "ATT1", "本振 2594_650", "本振 2594",
                    "本振 2595", "2595 偏移",
                    "2594_650", "2594_IF", "2595",
                    "2594 功率码", "2595 功率码"
                };

        return device == RadioDevice.SUp
            ? new[]
            {
                "频率", "衰减", "10 MHz",
                "中频", "ATT1", "本振 2594",
                "本振 2595", "共同偏移", "2594 锁定",
                "2594 IF 锁定", "2595 锁定", "2594 功率码",
                "2595 功率码"
            }
            : new[]
            {
                "频率", "衰减", "10 MHz",
                "中频", "ATT1", "ATT2",
                "本振 2594", "本振 2595", "共同偏移",
                "2594 锁定", "2594 IF 锁定", "2595 锁定",
                "2594 功率码", "2595 功率码"
            };
    }

    private static string[] GetKaCardValues(RadioDevice device, DeviceStatus status) => RadioDeviceInfo.HasSecondAttenuator(device)
        ? new[]
        {
            status.FrequencyGHz.HasValue ? $"{status.FrequencyGHz.Value * 1000:0.000} MHz" : "-- MHz",
            status.External10M.HasValue ? status.External10M.Value ? "外部" : "内部" : "--",
            status.IfMHz.HasValue ? $"{status.IfMHz} MHz" : "-- MHz",
            status.Attenuation1Db.HasValue ? $"{status.Attenuation1Db.Value:0.000} dB" : "-- dB",
            status.Attenuation2Db.HasValue ? $"{status.Attenuation2Db.Value:0.000} dB" : "-- dB",
            status.Lo2594FixedGHz.HasValue ? $"{status.Lo2594FixedGHz.Value:0.000000} GHz" : "-- GHz",
            status.Lo2594GHz.HasValue ? $"{status.Lo2594GHz.Value:0.000000} GHz" : "-- GHz",
            status.Lo2595GHz.HasValue ? $"{status.Lo2595GHz.Value:0.0000000} GHz" : "-- GHz",
            status.ShiftMHz.HasValue ? $"{status.ShiftMHz} MHz" : "-- MHz",
            FormatLockState(status.Lock2594), FormatLockState(status.Lock2594If), FormatLockState(status.Lock2595),
            FormatPower(status.Power2594), FormatPower(status.Power2595)
        }
        : new[]
        {
            status.FrequencyGHz.HasValue ? $"{status.FrequencyGHz.Value * 1000:0.000} MHz" : "-- MHz",
            status.External10M.HasValue ? status.External10M.Value ? "外部" : "内部" : "--",
            status.IfMHz.HasValue ? $"{status.IfMHz} MHz" : "-- MHz",
            status.Attenuation1Db.HasValue ? $"{status.Attenuation1Db.Value:0.000} dB" : "-- dB",
            status.Lo2594FixedGHz.HasValue ? $"{status.Lo2594FixedGHz.Value:0.000000} GHz" : "-- GHz",
            status.Lo2594GHz.HasValue ? $"{status.Lo2594GHz.Value:0.000000} GHz" : "-- GHz",
            status.Lo2595GHz.HasValue ? $"{status.Lo2595GHz.Value:0.0000000} GHz" : "-- GHz",
            status.ShiftMHz.HasValue ? $"{status.ShiftMHz} MHz" : "-- MHz",
            FormatLockState(status.Lock2594), FormatLockState(status.Lock2594If), FormatLockState(status.Lock2595),
            FormatPower(status.Power2594), FormatPower(status.Power2595)
        };

    private static string[] GetCardValues(RadioDevice device, DeviceStatus status)
    {
        if (RadioDeviceInfo.IsKaDevice(device))
            return GetKaCardValues(device, status);

        return RadioDeviceInfo.HasSecondAttenuator(device)
            ? new[]
            {
                status.FrequencyGHz.HasValue ? $"{status.FrequencyGHz.Value * 1000:0.000} MHz" : "-- MHz",
                status.AttenuationDb.HasValue ? $"{status.AttenuationDb.Value:0.000} dB" : "-- dB",
                status.External10M.HasValue ? status.External10M.Value ? "外部" : "内部" : "--",
                status.IfMHz.HasValue ? $"{status.IfMHz} MHz" : "-- MHz",
                status.Attenuation1Db.HasValue ? $"{status.Attenuation1Db.Value:0.000} dB" : "-- dB",
                status.Attenuation2Db.HasValue ? $"{status.Attenuation2Db.Value:0.000} dB" : "-- dB",
                status.Lo2594GHz.HasValue ? $"{status.Lo2594GHz.Value:0.000000} GHz" : "-- GHz",
                status.Lo2595GHz.HasValue ? $"{status.Lo2595GHz.Value:0.000000} GHz" : "-- GHz",
                status.ShiftMHz.HasValue ? $"{status.ShiftMHz} MHz" : "-- MHz",
                FormatLockState(status.Lock2594), FormatLockState(status.Lock2594If), FormatLockState(status.Lock2595),
                FormatPower(status.Power2594), FormatPower(status.Power2595)
            }
            : new[]
            {
                status.FrequencyGHz.HasValue ? $"{status.FrequencyGHz.Value * 1000:0.000} MHz" : "-- MHz",
                status.AttenuationDb.HasValue ? $"{status.AttenuationDb.Value:0.000} dB" : "-- dB",
                status.External10M.HasValue ? status.External10M.Value ? "外部" : "内部" : "--",
                status.IfMHz.HasValue ? $"{status.IfMHz} MHz" : "-- MHz",
                status.Attenuation1Db.HasValue ? $"{status.Attenuation1Db.Value:0.000} dB" : "-- dB",
                status.Lo2594GHz.HasValue ? $"{status.Lo2594GHz.Value:0.000000} GHz" : "-- GHz",
                status.Lo2595GHz.HasValue ? $"{status.Lo2595GHz.Value:0.000000} GHz" : "-- GHz",
                status.ShiftMHz.HasValue ? $"{status.ShiftMHz} MHz" : "-- MHz",
                FormatLockState(status.Lock2594), FormatLockState(status.Lock2594If), FormatLockState(status.Lock2595),
                FormatPower(status.Power2594), FormatPower(status.Power2595)
            };
    }

    private void UpdateOverviewCards()
    {
        foreach (var (device, view) in _deviceCards)
        {
            var status = _deviceStatus[device];
            var online = status.Online && (DateTime.Now - status.LastSeen).TotalSeconds < 12;
            view.Ip.Text = _settings.GetIp(device);
            view.State.Text = online ? "在线" : "未连接";
            view.State.Foreground = online ? GreenBrush : Brush.Parse("#6F7F7C");
            view.Badge.Background = Brush.Parse(online ? "#E6F3EC" : "#EFF2F1");
            view.Footer.Text = status.Firmware != "--" && !string.IsNullOrWhiteSpace(status.Firmware)
                ? $"固件 v{status.Firmware}" : status.Error ?? "";
            ToolTip.SetTip(view.Footer, status.Error ?? view.Footer.Text);
            var values = GetCardValues(device, status);
            for (var index = 0; index < values.Length; index++)
            {
                view.Values[index].Text = values[index];
                ToolTip.SetTip(view.Values[index], values[index]);
            }
        }
    }

    private static TextBlock AddMetric(Grid grid, int column, int row, string label)
    {
        var panel = new StackPanel { Spacing = 3 };
        var labelText = new TextBlock { Text = label, FontSize = 11, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(labelText, label);
        panel.Children.Add(labelText);
        var valueText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = InkBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        panel.Children.Add(valueText);
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
        return valueText;
    }

    private static string FormatLockState(int? state) => state switch
    {
        1 => "已锁定",
        0 => "未锁定",
        _ => "--"
    };

    private static string FormatPower(int? power) => power.HasValue ? power.Value.ToString(CultureInfo.InvariantCulture) : "--";

    private Control BuildTftpPage()
    {
        var body = PageBody();
        body.Children.Add(PageHeading("TFTP 升级", "选择设备和 .bin 固件文件，确认后通过网络写入设备"));
        var form = new StackPanel { Spacing = 18, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        var tftpDevices = new[] { "S 波段上变频器", "S 波段下变频器", "Ka 波段上变频器", "Ka 波段下变频器" };
        var deviceCombo = new ComboBox
        {
            ItemsSource = tftpDevices,
            SelectedIndex = 0,
            Width = 300,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var ipBox = new TextBox
        {
            Text = _settings.GetIp(RadioDevice.SUp),
            Width = 300,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        deviceCombo.SelectionChanged += (_, _) => ipBox.Text = _settings.GetIp(DeviceFromIndex(deviceCombo.SelectedIndex));
        form.Children.Add(FormRow("目标设备", deviceCombo));
        form.Children.Add(FormRow("设备 IP", ipBox));

        var fileBox = new TextBox { Watermark = "选择 .bin 固件文件", IsReadOnly = true, MinWidth = 420 };
        var browse = ActionButton("选择文件", TealBrush, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("固件二进制") { Patterns = new[] { "*.bin" } } }
            });
            if (files.Count > 0)
            {
                fileBox.Text = files[0].TryGetLocalPath() ?? files[0].Name;
            }
        });
        var fileRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        fileRow.Children.Add(fileBox);
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(browse);
        form.Children.Add(FormRow("固件文件", fileRow));

        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, Width = 600, HorizontalAlignment = HorizontalAlignment.Left };
        var progressLabel = new TextBlock { Text = "就绪", Foreground = MutedBrush, FontSize = 12 };
        Button? upload = null;
        upload = ActionButton("开始升级", OrangeBrush, async () =>
        {
            if (string.IsNullOrWhiteSpace(fileBox.Text) || !File.Exists(fileBox.Text))
            {
                progressLabel.Text = "请选择有效的固件文件。";
                return;
            }

            if (new FileInfo(fileBox.Text).Length > TftpUploader.MaxImageBytes)
            {
                progressLabel.Text = "固件文件不能超过 128 KB。";
                return;
            }

            var target = DeviceFromIndex(deviceCombo.SelectedIndex);
            var host = ipBox.Text?.Trim() ?? string.Empty;
            if (!IPAddress.TryParse(host, out _))
            {
                progressLabel.Text = "设备 IP 地址无效。";
                return;
            }

            progressLabel.Text = "正在核对设备型号…";
            try
            {
                await VerifyDeviceModelAsync(target, host);
            }
            catch (Exception ex)
            {
                progressLabel.Text = ex.Message;
                AppendLog($"TFTP 目标校验失败：{host} {ex.Message}");
                return;
            }

            if (!await ConfirmAsync("升级会先清空设备的固件存储区，再写入所选文件。请确认目标设备和固件文件正确。是否继续？"))
                return;

            upload!.IsEnabled = false;
            browse.IsEnabled = false;
            progress.Value = 0;
            progressLabel.Text = "正在发送固件…";
            var reporter = new Progress<double>(value =>
            {
                progress.Value = value * 100;
                progressLabel.Text = $"已写入 {value:P0}";
            });
            try
            {
                await TftpUploader.UploadAsync(host, fileBox.Text, reporter, CancellationToken.None);
                progress.Value = 100;
                progressLabel.Text = "固件已传输完成；请重启设备，再读取固件版本。";
                AppendLog($"上位机完成 TFTP 写入：{RadioDeviceInfo.DisplayName(target)} {host} {Path.GetFileName(fileBox.Text)}");
            }
            catch (Exception ex)
            {
                progressLabel.Text = ex.Message;
                AppendLog($"TFTP 失败：{host} {ex.Message}");
            }
            finally
            {
                upload!.IsEnabled = true;
                browse.IsEnabled = true;
            }
        });
        var controls = new StackPanel { Spacing = 12 };
        controls.Children.Add(progress);
        controls.Children.Add(progressLabel);
        controls.Children.Add(upload);
        form.Children.Add(FormRow("传输", controls));
        form.Children.Add(new TextBlock
        {
            Text = "固件文件最大 128 KB。写入完成后重启设备以加载新版本。",
            Foreground = MutedBrush,
            FontSize = 12
        });
        body.Children.Add(form);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Control BuildNetworkPage()
    {
        var body = PageBody();
        body.Children.Add(PageHeading("变频器IP配置", "修改设备地址。子网掩码固定为 255.255.255.0，网关自动跟随新 IP 所在网段的 .1。"));
        var form = new StackPanel { Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var deviceCombo = new ComboBox
        {
            ItemsSource = new[] { "S 波段上变频器", "S 波段下变频器", "Ka 波段上变频器", "Ka 波段下变频器" },
            SelectedIndex = 0,
            Width = 300,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var currentIp = new TextBlock
        {
            Text = _settings.GetIp(RadioDevice.SUp),
            Foreground = InkBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var targetIp = new TextBox
        {
            Text = _settings.GetIp(RadioDevice.SUp),
            Width = 300,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var netmaskText = new TextBlock { Text = "255.255.255.0", Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center };
        var gatewayText = new TextBlock { Text = "192.168.1.1", Foreground = InkBrush, VerticalAlignment = VerticalAlignment.Center };
        UpdateGatewayText(targetIp.Text, gatewayText);
        var state = new TextBlock
        {
            Text = "在设备总览中点击设备卡片，可保存上位机连接 IP；此页把新地址写入设备，重启后生效。",
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        };
        deviceCombo.SelectionChanged += (_, _) =>
        {
            var selected = DeviceFromIndex(deviceCombo.SelectedIndex);
            currentIp.Text = _settings.GetIp(selected);
            targetIp.Text = _settings.GetIp(selected);
            UpdateGatewayText(targetIp.Text, gatewayText);
            state.Text = "在设备总览中点击设备卡片，可保存上位机连接 IP；此页把新地址写入设备，重启后生效。";
            state.Foreground = MutedBrush;
        };
        targetIp.TextChanged += (_, _) => UpdateGatewayText(targetIp.Text, gatewayText);

        Button? save = null;
        save = ActionButton("保存网络参数", TealBrush, async () =>
        {
            var device = DeviceFromIndex(deviceCombo.SelectedIndex);
            var host = _settings.GetIp(device);
            var nextIp = targetIp.Text?.Trim() ?? string.Empty;
            if (!RadioDeviceInfo.TryParseDeviceIp(nextIp, out var octets))
            {
                state.Text = "请输入有效 IPv4 地址，最后一段须为 2–254。";
                state.Foreground = Brush.Parse("#A84F46");
                return;
            }

            nextIp = string.Join(".", octets);
            var gateway = $"{octets[0]}.{octets[1]}.{octets[2]}.1";
            var targetAddress = string.Join(",", octets);
            save!.IsEnabled = false;
            state.Text = $"正在向 {host} 写入网络参数…";
            state.Foreground = MutedBrush;
            try
            {
                await VerifyDeviceModelAsync(device, host);
                await SendSettingAsync(host, "$SETNETMASK,255,255,255,0");
                await SendSettingAsync(host, $"$SETGATEWAY,{gateway.Replace('.', ',')}");
                await SendSettingAsync(host, $"$SETIP,{targetAddress}");
                SaveConnectionIp(device, nextIp);
                state.Text = $"设备已保存：IP {nextIp}，子网掩码 255.255.255.0，网关 {gateway}。请重启设备后使用新地址。";
                state.Foreground = GreenBrush;
                AppendLog($"写入网络参数：{RadioDeviceInfo.DisplayName(device)} IP={nextIp} MASK=255.255.255.0 GW={gateway}");
                currentIp.Text = nextIp;
            }
            catch (Exception ex)
            {
                state.Text = ex.Message;
                state.Foreground = Brush.Parse("#A84F46");
            }
            finally
            {
                save!.IsEnabled = true;
            }
        });

        form.Children.Add(FormRow("设备", deviceCombo));
        form.Children.Add(FormRow("当前连接 IP", currentIp));
        form.Children.Add(FormRow("要设置的新 IP", targetIp));
        form.Children.Add(FormRow("子网掩码", netmaskText));
        form.Children.Add(FormRow("默认网关", gatewayText));
        form.Children.Add(save);
        form.Children.Add(state);
        body.Children.Add(form);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Control BuildLogsPage()
    {
        // 日志框只占用窗口剩余空间，避免固定高度把底边和滚动条挤出窗口。
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            RowSpacing = 14,
            Margin = new Thickness(26, 22, 26, 26)
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var heading = PageHeading("UDP 日志", "按设备查看 UDP 10080 日志");
        heading.Margin = new Thickness(0, 0, 24, 4);
        header.Children.Add(heading);
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        _logScrollButton = ActionButton(_logAutoScroll ? "暂停滚动" : "开始滚动", TealBrush, ToggleLogAutoScroll);
        actions.Children.Add(_logScrollButton);
        actions.Children.Add(ActionButton("复制选中", TealBrush, CopySelectedLogAsync));
        actions.Children.Add(ActionButton("保存日志", TealBrush, SaveLogsAsync));
        actions.Children.Add(ActionButton("导出日志", GreenBrush, ExportLogsAsync));
        var clear = ActionButton("清空日志", TealBrush, () =>
        {
            _logEntries.Clear();
            if (_logTextBox is not null) _logTextBox.Text = string.Empty;
            SetLogActionState("日志已清空");
        });
        actions.Children.Add(clear);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        body.Children.Add(header);
        var filterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 10
        };
        filterRow.Children.Add(new TextBlock
        {
            Text = "设备",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        _logDeviceFilter = new ComboBox
        {
            ItemsSource = new[] { "全部设备", "S 波段上变频器", "S 波段下变频器", "Ka 波段上变频器", "Ka 波段下变频器" },
            SelectedIndex = _selectedLogDevice switch
            {
                RadioDevice.SUp => 1,
                RadioDevice.SDown => 2,
                RadioDevice.KaUp => 3,
                RadioDevice.KaDown => 4,
                _ => 0
            },
            Width = 260
        };
        _logDeviceFilter.SelectionChanged += (_, _) =>
        {
            _selectedLogDevice = _logDeviceFilter.SelectedIndex switch
            {
                1 => RadioDevice.SUp,
                2 => RadioDevice.SDown,
                3 => RadioDevice.KaUp,
                4 => RadioDevice.KaDown,
                _ => null
            };
            UpdateLogFilterState();
            RefreshLogView();
        };
        Grid.SetColumn(_logDeviceFilter, 1);
        filterRow.Children.Add(_logDeviceFilter);
        _logFilterStateText = new TextBlock
        {
            Foreground = MutedBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(_logFilterStateText, 2);
        filterRow.Children.Add(_logFilterStateText);
        UpdateLogFilterState();
        Grid.SetRow(filterRow, 1);
        body.Children.Add(filterRow);
        var listenerState = new TextBlock
        {
            Text = _logStartError is null ? "监听端口 10080" : $"监听失败：{_logStartError}",
            Foreground = _logStartError is null ? GreenBrush : Brush.Parse("#A84F46"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(listenerState, 2);
        body.Children.Add(listenerState);
        _logActionStateText = new TextBlock
        {
            Text = "日志文件默认保存为 UTF-8 文本",
            Foreground = MutedBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_logActionStateText, 3);
        body.Children.Add(_logActionStateText);
        _logTextBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = "Consolas, Menlo, monospace",
            FontSize = 12,
            Background = Brushes.White,
            Foreground = InkBrush,
            BorderBrush = Brush.Parse("#A7B5B1"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CaretBrush = InkBrush
        };
        _logTextBox.Classes.Add("udp-log-text");
        ScrollViewer.SetVerticalScrollBarVisibility(_logTextBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_logTextBox, ScrollBarVisibility.Auto);
        _logTextBox.Text = FormatLogEntries();
        Grid.SetRow(_logTextBox, 4);
        body.Children.Add(_logTextBox);
        return body;
    }

    private void ToggleLogAutoScroll()
    {
        _logAutoScroll = !_logAutoScroll;
        if (_logScrollButton is not null)
            _logScrollButton.Content = _logAutoScroll ? "暂停滚动" : "开始滚动";

        SetLogActionState(_logAutoScroll
            ? "日志滚动已开始，正在显示最新日志"
            : "日志滚动已暂停，新日志继续接收并暂不刷新显示区");
        if (_logAutoScroll)
            RefreshLogView(scrollToEnd: true);
    }

    private async Task CopySelectedLogAsync()
    {
        var selectedText = _logTextBox?.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            SetLogActionState("请先在日志框中选中要复制的日志行");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetLogActionState("复制失败：当前窗口没有可用剪贴板");
            return;
        }

        await clipboard.SetTextAsync(selectedText);
        SetLogActionState($"已复制 {selectedText.Split('\n').Length} 行日志");
    }

    private void SetLogActionState(string message)
    {
        if (_logActionStateText is not null)
            _logActionStateText.Text = message;
    }

    private async Task SaveLogsAsync()
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeijingStationConverter",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"udp-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, FormatLogEntries(), Encoding.UTF8);
            SetLogActionState($"日志已保存：{path}");
        }
        catch (Exception ex)
        {
            SetLogActionState($"保存日志失败：{ex.Message}");
        }
    }

    private async Task ExportLogsAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"udp-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("文本日志") { Patterns = new[] { "*.txt" } }
                }
            });
            if (file is null)
                return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetLogActionState("导出失败：目标位置不是本地文件");
                return;
            }

            await File.WriteAllTextAsync(path, FormatLogEntries(), Encoding.UTF8);
            SetLogActionState($"日志已导出：{path}");
        }
        catch (Exception ex)
        {
            SetLogActionState($"导出日志失败：{ex.Message}");
        }
    }

    private StackPanel PageBody() => new()
    {
        Spacing = 18,
        Margin = new Thickness(26, 22, 26, 26)
    };

    private static Control PageHeading(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = InkBrush });
        panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static Control FormRow(string label, Control field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 12, MinHeight = 38 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = MutedBrush,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }

    private static Border Notice(string text, IBrush accent)
    {
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 12),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = InkBrush, FontSize = 12 }
        };
    }

    private static Button ActionButton(string label, IBrush color, Action action)
    {
        var button = new Button
        {
            Content = label,
            Background = color,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(15, 9),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ActionButton(string label, IBrush color, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            Background = color,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(15, 9),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task StartLogListenerAsync()
    {
        if (_logStarted) return;
        try
        {
            await _logListener.StartAsync();
            _logStarted = true;
            _logStateText.Text = "UDP 10080 正在监听";
            _logStateText.Foreground = GreenBrush;
        }
        catch (Exception ex)
        {
            _logStartError = ex.Message;
            _logStateText.Text = "UDP 10080 不可用";
            _logStateText.Foreground = Brush.Parse("#A84F46");
        }

    }

    private void OnLogPacket(string text, IPEndPoint remote)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ParseStatusFrame(text, remote);
            var device = ResolveLogDevice(text, remote);
            AppendLog($"{DateTime.Now:HH:mm:ss.fff}  {remote.Address}:{remote.Port}  {text}",
                device, remote.Address.ToString());
        });
    }

    private void AppendLog(string entry, RadioDevice? device = null, string? remoteIp = null)
    {
        _logEntries.Enqueue(new LogEntry(entry, remoteIp, device));
        while (_logEntries.Count > 1200) _logEntries.Dequeue();
        if (_currentPage == Page.Logs && _logTextBox is not null && _logAutoScroll)
        {
            RefreshLogView(scrollToEnd: true);
        }
    }

    private string FormatLogEntries() => string.Join(Environment.NewLine,
        _logEntries
            .Where(LogMatchesSelectedDevice)
            .Select(entry => entry.Text));

    private void RefreshLogView(bool scrollToEnd = false)
    {
        if (_logTextBox is null) return;
        _logTextBox.Text = FormatLogEntries();
        if (scrollToEnd)
            _logTextBox.CaretIndex = _logTextBox.Text?.Length ?? 0;
    }

    private bool LogMatchesSelectedDevice(LogEntry entry)
    {
        if (_selectedLogDevice is null)
            return true;

        var selectedIp = GetDeviceLogIp(_selectedLogDevice.Value);
        if (!string.IsNullOrWhiteSpace(selectedIp) && !string.IsNullOrWhiteSpace(entry.RemoteIp))
        {
            // 设备筛选以 UDP 包来源 IP 为准，避免同一设备的普通运行日志因缺少型号字段被漏掉。
            if (!string.Equals(entry.RemoteIp, selectedIp, StringComparison.OrdinalIgnoreCase))
                return false;

            // 两台设备临时共用同一 IP 时，状态帧中已有的身份信息继续承担二次区分。
            return entry.Device is null || entry.Device == _selectedLogDevice;
        }

        return entry.Device == _selectedLogDevice;
    }

    private string? GetDeviceLogIp(RadioDevice device)
    {
        var configuredIp = _settings.GetIp(device);
        if (!string.IsNullOrWhiteSpace(configuredIp))
            return configuredIp;
        var statusIp = _deviceStatus[device].IpAddress;
        if (!string.IsNullOrWhiteSpace(statusIp))
            return statusIp;

        return _logSources.FirstOrDefault(pair => pair.Value == device).Key;
    }

    private void UpdateLogFilterState()
    {
        if (_logFilterStateText is null)
            return;

        if (_selectedLogDevice is null)
        {
            _logFilterStateText.Text = "显示全部来源日志";
            return;
        }

        var ip = GetDeviceLogIp(_selectedLogDevice.Value);
        _logFilterStateText.Text = string.IsNullOrWhiteSpace(ip)
            ? "该设备尚未记录来源 IP，将按设备身份筛选"
            : $"来源 IP：{ip}";
    }

    private RadioDevice? ResolveLogDevice(string text, IPEndPoint remote)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("$STATUS,", StringComparison.Ordinal) &&
                !trimmed.StartsWith("$VERSION,", StringComparison.Ordinal) &&
                !trimmed.StartsWith("$BOOT,", StringComparison.Ordinal)) continue;
            var fields = UdpCommandClient.ParseFields(trimmed);
            fields.TryGetValue("MODEL", out var model);
            var deviceId = ParseInt(fields, "DEVICE_ID");
            var identifiedDevice = RadioDeviceInfo.FromIdentity(model, deviceId);
            if (identifiedDevice.HasValue)
            {
                _logSources[remote.Address.ToString()] = identifiedDevice.Value;
                return identifiedDevice.Value;
            }
        }

        var address = remote.Address.ToString();
        if (_logSources.TryGetValue(address, out var knownDevice)) return knownDevice;
        var matches = Enum.GetValues<RadioDevice>()
            .Where(device => string.Equals(address, _settings.GetIp(device), StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void ParseStatusFrame(string text, IPEndPoint remote)
    {
        var updated = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("$STATUS,", StringComparison.Ordinal)) continue;
            var fields = UdpCommandClient.ParseFields(trimmed);
            fields.TryGetValue("MODEL", out var model);
            var device = RadioDeviceInfo.FromIdentity(model, ParseInt(fields, "DEVICE_ID"));
            if (device is null) continue;

            var status = _deviceStatus[device.Value];
            status.Online = true;
            status.LastSeen = DateTime.Now;
            status.Error = null;
            status.DeviceId = ParseInt(fields, "DEVICE_ID") ?? RadioDeviceInfo.DeviceId(device.Value);
            status.State = ParseInt(fields, "DEVICE_STATE") ?? ParseInt(fields, "STATE");
            status.Firmware = fields.GetValueOrDefault("FW", status.Firmware);
            var rfFrequency = ParseDouble(fields, "RF_GHZ");
            status.FrequencyGHz = rfFrequency is > 0 ? rfFrequency : null;
            status.Lo2594GHz = ParseDouble(fields, "LO2594_GHZ");
            status.Lo2594FixedGHz = ParseDouble(fields, "LO2594_650_GHZ");
            status.Lo2595GHz = ParseDouble(fields, "LO2595_GHZ");
            status.IfMHz = ParseInt(fields, "IF_MHZ");
            status.Attenuation1Db = ParseDouble(fields, "ATT1_DB");
            status.Attenuation2Db = RadioDeviceInfo.HasSecondAttenuator(device.Value)
                ? ParseDouble(fields, "ATT2_DB") : null;
            if (status.Attenuation1Db.HasValue)
                status.AttenuationDb = status.Attenuation1Db.Value + (status.Attenuation2Db ?? 0);
            status.External10M = ParseInt(fields, "10M") is int external ? external != 0 : null;
            status.Lock2594 = ParseInt(fields, "LOCK2594");
            status.Lock2594If = ParseInt(fields, "LOCK2594_IF");
            status.Lock2595 = ParseInt(fields, "LOCK2595");
            status.Power2594 = ParseInt(fields, "POWER2594");
            status.Power2595 = ParseInt(fields, "POWER2595");
            status.ShiftMHz = ParseInt(fields, "SHIFT");
            var ip = fields.GetValueOrDefault("IP", remote.Address.ToString());
            if (IPAddress.TryParse(ip, out _))
            {
                status.IpAddress = ip;
                if (!string.Equals(_settings.GetIp(device.Value), ip, StringComparison.Ordinal))
                {
                    _settings.SetIp(device.Value, ip);
                    SettingsStore.Save(_settings);
                }
            }
            updated = true;
        }
        if (updated)
        {
            UpdateLogFilterState();
            UpdateOverviewCards();
        }
    }

    private async Task RefreshAllDevicesAsync()
    {
        if (_statusPollRunning) return;
        _statusPollRunning = true;
        try
        {
            await Task.WhenAll(
                RefreshDeviceAsync(RadioDevice.SUp),
                RefreshDeviceAsync(RadioDevice.SDown),
                RefreshDeviceAsync(RadioDevice.KaUp),
                RefreshDeviceAsync(RadioDevice.KaDown));
            UpdateOverviewCards();
        }
        finally
        {
            _statusPollRunning = false;
        }
    }

    private async Task RefreshDeviceAsync(RadioDevice device)
    {
        var host = _settings.GetIp(device);
        try
        {
            var status = await ReadDeviceStatusAsync(device, host);
            // 轮询尚未完成时连接 IP 可能已改动，旧地址的结果不能覆盖新设备状态。
            if (string.Equals(host, _settings.GetIp(device), StringComparison.Ordinal))
                _deviceStatus[device] = status;
        }
        catch (Exception ex)
        {
            if (string.Equals(host, _settings.GetIp(device), StringComparison.Ordinal))
            {
                _deviceStatus[device].Online = false;
                _deviceStatus[device].Error = ex.Message;
            }
        }
    }

    private async Task<DeviceStatus> ReadDeviceStatusAsync(RadioDevice device, string host)
    {
        var versionResponse = await UdpCommandClient.SendAsync(host, "$GETVERSION");
        var versionFields = ParseAndVerifyDeviceModel(device, host, versionResponse);
        var stateResponse = await UdpCommandClient.SendAsync(host, "$GETRFSTATE");
        // 与配置弹窗使用同一精度，避免综合状态帧截断 0.25 dB 步进值。
        var attenuationResponse = await UdpCommandClient.SendAsync(host, "$GETATT");
        var attenuationFields = UdpCommandClient.ParseFields(attenuationResponse);
        if (UdpCommandClient.HasDeviceError(attenuationResponse) ||
            !attenuationFields.ContainsKey("ATT1") ||
            (RadioDeviceInfo.HasSecondAttenuator(device) && !attenuationFields.ContainsKey("ATT2")))
            throw new IOException($"设备衰减查询失败：{attenuationResponse.Trim()}");
        if (UdpCommandClient.HasDeviceError(stateResponse))
            throw new IOException($"设备状态查询失败：{stateResponse}");

        var stateFields = UdpCommandClient.ParseFields(stateResponse);
        var status = new DeviceStatus();
        status.Firmware = versionFields.GetValueOrDefault("FW", status.Firmware);
        status.DeviceId = ParseInt(versionFields, "DEVICE_ID") ?? RadioDeviceInfo.DeviceId(device);
        status.State = ParseInt(versionFields, "DEVICE_STATE");
        status.IpAddress = host;
        var rfFrequency = ParseDouble(stateFields, "RF");
        status.FrequencyGHz = rfFrequency is > 0 ? rfFrequency : null;
        status.Lo2594GHz = ParseDouble(stateFields, "LO2594");
        status.Lo2594FixedGHz = ParseDouble(stateFields, "LO2594_650");
        status.Lo2595GHz = ParseDouble(stateFields, "LO2595");
        status.IfMHz = ParseInt(stateFields, "IF");
        status.Attenuation1Db = ParseDouble(attenuationFields, "ATT1");
        status.Attenuation2Db = RadioDeviceInfo.HasSecondAttenuator(device)
            ? ParseDouble(attenuationFields, "ATT2") : null;
        status.AttenuationDb = status.Attenuation1Db.HasValue
            ? status.Attenuation1Db.Value + (status.Attenuation2Db ?? 0)
            : null;
        status.External10M = ParseInt(stateFields, "10M") is int external ? external != 0 : null;
        status.Lock2594 = ParseInt(stateFields, "2594_650");
        status.Lock2594If = ParseInt(stateFields, "2594_IF");
        status.Lock2595 = ParseInt(stateFields, "2595");
        status.Power2595 = ParseInt(stateFields, "POWER");
        status.Power2594 = ParseInt(stateFields, "POWER2594");
        status.ShiftMHz = ParseInt(stateFields, "SHIFT");
        status.Online = true;
        status.Error = null;
        status.LastSeen = DateTime.Now;
        return status;
    }

    private static async Task VerifyDeviceModelAsync(RadioDevice device, string host)
    {
        var response = await UdpCommandClient.SendAsync(host, "$GETVERSION");
        _ = ParseAndVerifyDeviceModel(device, host, response);
    }

    private static Dictionary<string, string> ParseAndVerifyDeviceModel(RadioDevice device, string host, string response)
    {
        if (UdpCommandClient.HasDeviceError(response))
            throw new IOException($"设备拒绝版本查询：{response}");
        var fields = UdpCommandClient.ParseFields(response);
        if (!fields.TryGetValue("MODEL", out var model))
            throw new IOException($"设备未返回型号信息：{response}");
        if (!string.Equals(model, RadioDeviceInfo.ModelToken(device), StringComparison.Ordinal))
            throw new IOException($"地址 {host} 返回型号 {model}，与所选设备 {RadioDeviceInfo.ModelToken(device)} 不符。");
        return fields;
    }

    private static async Task SendSettingAsync(string host, string command)
    {
        var response = await UdpCommandClient.SendAsync(host, command);
        if (UdpCommandClient.HasDeviceError(response))
            throw new IOException(UdpCommandClient.DescribeSettingError(command, response));
    }

    private void SaveConnectionIp(RadioDevice device, string ip)
    {
        var previousIp = _settings.GetIp(device);
        _settings.SetIp(device, ip);
        try
        {
            SettingsStore.Save(_settings);
        }
        catch
        {
            _settings.SetIp(device, previousIp);
            throw;
        }

        if (!string.Equals(previousIp, ip, StringComparison.Ordinal))
            _deviceStatus[device] = new DeviceStatus { IpAddress = ip };
        UpdateOverviewCards();
        UpdateLogFilterState();
        if (_currentPage == Page.Logs)
            RefreshLogView();
    }

    private void OpenDeviceConfiguration(RadioDevice device)
    {
        var host = _settings.GetIp(device);
        var dialog = new DeviceConfigurationWindow(device, host, _deviceStatus[device],
            ip => SaveConnectionIp(device, ip), status =>
        {
            _deviceStatus[device] = status;
            UpdateOverviewCards();
        });
        _ = dialog.ShowDialog(this);
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new Window
        {
            Title = "确认 TFTP 升级",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = InkBrush, FontSize = 14 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var confirmed = false;
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 8) };
        cancel.Click += (_, _) => dialog.Close();
        var proceed = new Button { Content = "开始升级", Background = OrangeBrush, Foreground = Brushes.White, Padding = new Thickness(14, 8) };
        proceed.Click += (_, _) => { confirmed = true; dialog.Close(); };
        actions.Children.Add(cancel);
        actions.Children.Add(proceed);
        content.Children.Add(actions);
        dialog.Content = content;
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private static void UpdateGatewayText(string? ipText, TextBlock gatewayText)
    {
        var parts = ipText?.Split('.');
        gatewayText.Text = parts is { Length: 4 } && parts.Take(3).All(part => byte.TryParse(part, out _))
            ? $"{parts[0]}.{parts[1]}.{parts[2]}.1"
            : "输入有效的 IPv4 地址后自动生成";
    }

    private static double? ParseDouble(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static RadioDevice DeviceFromIndex(int index) => index switch
    {
        1 => RadioDevice.SDown,
        2 => RadioDevice.KaUp,
        3 => RadioDevice.KaDown,
        _ => RadioDevice.SUp
    };

    private bool HasDuplicateDeviceIps() =>
        _settings.GetIp(RadioDevice.SUp) == _settings.GetIp(RadioDevice.SDown) ||
        _settings.GetIp(RadioDevice.SUp) == _settings.GetIp(RadioDevice.KaUp) ||
        _settings.GetIp(RadioDevice.SUp) == _settings.GetIp(RadioDevice.KaDown) ||
        _settings.GetIp(RadioDevice.SDown) == _settings.GetIp(RadioDevice.KaUp) ||
        _settings.GetIp(RadioDevice.SDown) == _settings.GetIp(RadioDevice.KaDown) ||
        _settings.GetIp(RadioDevice.KaUp) == _settings.GetIp(RadioDevice.KaDown);
}
