using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace BeijingStationConverter;

public sealed class DeviceConfigurationWindow : Window
{
    private static readonly IBrush InkBrush = Brush.Parse("#1D302D");
    private static readonly IBrush MutedBrush = Brush.Parse("#6F7F7C");
    private static readonly IBrush TealBrush = Brush.Parse("#23867D");
    private static readonly IBrush GreenBrush = Brush.Parse("#408B69");
    private const decimal KaFrequencyMinMhz = 22600m;
    private const decimal KaFrequencyMaxMhz = 27200m;

    private readonly RadioDevice _device;
    private string _host;
    private readonly Action<string> _onHostSaved;
    private readonly Action<DeviceStatus> _onUpdated;
    private DeviceStatus _status;
    private readonly TextBox _ipAddress = new();
    private readonly Button _saveIpButton = new();
    private readonly TextBlock _feedback = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private NumericUpDown? _sFrequency;
    private NumericUpDown? _sAttenuation;
    private NumericUpDown? _kaFrequency;
    private NumericUpDown? _kaGain;
    private ComboBox? _reference;
    private ComboBox? _intermediateFrequency;

    public DeviceConfigurationWindow(RadioDevice device, string host, DeviceStatus status,
        Action<string> onHostSaved, Action<DeviceStatus> onUpdated)
    {
        _device = device;
        _host = host;
        _onHostSaved = onHostSaved;
        _status = new DeviceStatus
        {
            Online = status.Online,
            DeviceId = status.DeviceId,
            State = status.State,
            IpAddress = status.IpAddress,
            Firmware = status.Firmware,
            FrequencyGHz = status.FrequencyGHz,
            Lo2594GHz = status.Lo2594GHz,
            Lo2594FixedGHz = status.Lo2594FixedGHz,
            Lo2595GHz = status.Lo2595GHz,
            AttenuationDb = status.AttenuationDb,
            Attenuation1Db = status.Attenuation1Db,
            Attenuation2Db = status.Attenuation2Db,
            External10M = status.External10M,
            IfMHz = status.IfMHz,
            Lock2594 = status.Lock2594,
            Lock2594If = status.Lock2594If,
            Lock2595 = status.Lock2595,
            Power2594 = status.Power2594,
            Power2595 = status.Power2595,
            PowerDbm = status.PowerDbm,
            PowerFromControlBoard = status.PowerFromControlBoard,
            ShiftMHz = status.ShiftMHz,
            LastSeen = status.LastSeen
        };
        _onUpdated = onUpdated;

        Title = RadioDeviceInfo.DisplayName(device);
        Width = 560;
        Height = RadioDeviceInfo.IsSDevice(device) ? 540 : 610;
        MinWidth = 520;
        MinHeight = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Content = BuildContent();
        Opened += async (_, _) =>
        {
            await RefreshAsync();
        };
    }

    private Control BuildContent()
    {
        var body = new StackPanel { Spacing = 18 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new StackPanel { Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = RadioDeviceInfo.DisplayName(_device),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = InkBrush
        });
        _ipAddress.Name = "DeviceIpInput";
        _ipAddress.Text = _host;
        _ipAddress.FontSize = 12;
        _ipAddress.Width = 150;
        _ipAddress.MinHeight = 32;
        _ipAddress.Padding = new Thickness(8, 5);
        _ipAddress.Foreground = MutedBrush;
        _ipAddress.VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_ipAddress, "上位机用于读取和下发配置的设备连接 IP。");

        _saveIpButton.Name = "SaveDeviceIpButton";
        _saveIpButton.Content = "保存设备 IP";
        _saveIpButton.FontSize = _ipAddress.FontSize;
        _saveIpButton.Background = GreenBrush;
        _saveIpButton.Foreground = Brushes.White;
        _saveIpButton.BorderThickness = new Thickness(0);
        _saveIpButton.MinHeight = 32;
        _saveIpButton.Padding = new Thickness(10, 5);
        _saveIpButton.Classes.Add("save-device-ip");
        ToolTip.SetTip(_saveIpButton, "保存上位机使用的设备连接 IP。");
        _saveIpButton.Click += async (_, _) => await SaveHostIpAsync();
        title.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _ipAddress, _saveIpButton }
        });
        heading.Children.Add(title);
        _refreshButton.Content = "读取设备";
        _refreshButton.Background = Brushes.White;
        _refreshButton.Foreground = TealBrush;
        _refreshButton.BorderBrush = Brush.Parse("#B8D5CF");
        _refreshButton.BorderThickness = new Thickness(1);
        _refreshButton.Padding = new Thickness(12, 8);
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        Grid.SetColumn(_refreshButton, 1);
        heading.Children.Add(_refreshButton);
        body.Children.Add(heading);
        body.Children.Add(new Border { Height = 1, Background = Brush.Parse("#E5EBE9") });

        body.Children.Add(RadioDeviceInfo.IsSDevice(_device) ? BuildSForm() : BuildKaForm());

        _feedback.Text = "读取设备参数中…";
        _feedback.TextWrapping = TextWrapping.Wrap;
        _feedback.Foreground = MutedBrush;
        _feedback.FontSize = 12;
        body.Children.Add(_feedback);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "关闭", Padding = new Thickness(16, 8) };
        cancel.Click += (_, _) => Close();
        footer.Children.Add(cancel);
        _saveButton.Content = "保存并下发";
        _saveButton.Background = TealBrush;
        _saveButton.Foreground = Brushes.White;
        _saveButton.BorderThickness = new Thickness(0);
        _saveButton.Padding = new Thickness(18, 8);
        _saveButton.IsEnabled = _status.Online && !string.IsNullOrWhiteSpace(_host);
        _saveButton.Click += async (_, _) => await SaveAsync();
        footer.Children.Add(_saveButton);
        // 长错误信息在正文内滚动，底部操作按钮始终可见。
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(26, 22) };
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        return layout;
    }

    private Control BuildSForm()
    {
        var form = new StackPanel { Spacing = 16 };
        var isUp = _device == RadioDevice.SUp;
        _sFrequency = new NumericUpDown
        {
            Minimum = 2000m,
            Maximum = 2400m,
            Increment = 0.001m,
            FormatString = "0.000",
            Value = (decimal)((_status.FrequencyGHz ?? 2.2) * 1000),
            Width = 220
        };
        form.Children.Add(FormRow("射频频率", WithUnit(_sFrequency, "MHz")));

        _sAttenuation = new NumericUpDown
        {
            Minimum = 0,
            Maximum = isUp ? 31.75m : 63.75m,
            Increment = isUp ? 0.25m : 0.125m,
            FormatString = "0.000",
            Value = (decimal)(_status.AttenuationDb ?? 0),
            Width = 220
        };
        form.Children.Add(FormRow(isUp ? "增益" : "总增益", WithUnit(_sAttenuation, "dB")));

        _reference = new ComboBox
        {
            ItemsSource = new[] { "内部参考源", "外部参考源" },
            SelectedIndex = _status.External10M == true ? 1 : 0,
            Width = 260
        };
        form.Children.Add(FormRow("10 MHz 源", _reference));
        form.Children.Add(FormRow("中频", new TextBlock
        {
            Text = "固定 70 MHz",
            Foreground = InkBrush,
            VerticalAlignment = VerticalAlignment.Center
        }));

        form.Children.Add(new TextBlock
        {
            Text = isUp ? "频率 2000–2400 MHz，步进 1 kHz；增益 0–31.75 dB，步进 0.25 dB。" :
                "频率 2000–2400 MHz，步进 1 kHz；总增益 0–63.75 dB，步进 0.125 dB。",
            Foreground = MutedBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        return form;
    }

    private Control BuildKaForm()
    {
        var form = new StackPanel { Spacing = 16 };
        _kaFrequency = new NumericUpDown
        {
            Minimum = KaFrequencyMinMhz,
            Maximum = KaFrequencyMaxMhz,
            Increment = 0.001m,
            FormatString = "0.000",
            Value = (decimal)Math.Clamp((_status.FrequencyGHz ?? 24.9) * 1000.0,
                (double)KaFrequencyMinMhz, (double)KaFrequencyMaxMhz),
            Width = 220
        };
        form.Children.Add(FormRow("频率", WithUnit(_kaFrequency, "MHz")));
        var (gainMin, gainMax) = RadioDeviceInfo.KaGainRangeDb(_device);
        var gain = _status.AttenuationDb.HasValue
            ? RadioDeviceInfo.KaGainFromAttenuation(_device, _status.AttenuationDb.Value)
            : RadioDeviceInfo.KaGainOffsetDb(_device);
        _kaGain = new NumericUpDown
        {
            Minimum = gainMin,
            Maximum = gainMax,
            Increment = 0.25m,
            FormatString = "0.00",
            Value = Math.Clamp(gain, gainMin, gainMax),
            Width = 220
        };
        form.Children.Add(FormRow("增益", WithUnit(_kaGain, "dB")));
        _reference = new ComboBox
        {
            ItemsSource = new[] { "内部参考源", "外部参考源" },
            SelectedIndex = _status.External10M == true ? 1 : 0,
            Width = 260
        };
        form.Children.Add(FormRow("10 MHz 源", _reference));
        _intermediateFrequency = new ComboBox
        {
            ItemsSource = new[] { "70 MHz", "720 MHz", "1200 MHz" },
            SelectedIndex = _status.IfMHz switch { 70 => 0, 1200 => 2, _ => 1 },
            Width = 260
        };
        form.Children.Add(FormRow("中频选择", _intermediateFrequency));
        form.Children.Add(new TextBlock
        {
            Text = _device == RadioDevice.KaUp
                ? "频率 22600–27200 MHz，步进 1 kHz；增益 5.00–35.00 dB，步进 0.25 dB。"
                : "频率 22600–27200 MHz，步进 1 kHz；增益 15.00–55.00 dB，步进 0.25 dB，设备自动分配到级联 ATT1/ATT2。",
            Foreground = MutedBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        return form;
    }

    private async Task SaveHostIpAsync()
    {
        var ip = _ipAddress.Text?.Trim() ?? string.Empty;
        if (!RadioDeviceInfo.TryParseDeviceIp(ip, out var octets))
        {
            _feedback.Text = "请输入有效 IPv4 地址，最后一段须为 2–254。";
            _feedback.Foreground = Brush.Parse("#A84F46");
            return;
        }

        ip = string.Join(".", octets);
        try
        {
            // 这里只保存上位机的连接目标；设备实际网卡地址由 IP 配置页写入。
            _onHostSaved(ip);
            if (!string.Equals(_host, ip, StringComparison.Ordinal))
                _status = new DeviceStatus { IpAddress = ip };
            _host = ip;
            _ipAddress.Text = ip;
        }
        catch (Exception ex)
        {
            _feedback.Text = $"连接 IP 保存失败：{ex.Message}";
            _feedback.Foreground = Brush.Parse("#A84F46");
            return;
        }

        await RefreshAsync(ip);
    }

    private void SetBusy(bool busy)
    {
        // 一组命令执行期间固定目标地址，避免读取和设置落到不同设备。
        _ipAddress.IsEnabled = !busy;
        _saveIpButton.IsEnabled = !busy;
        _refreshButton.IsEnabled = !busy;
        _saveButton.IsEnabled = !busy && _status.Online &&
                                !string.IsNullOrWhiteSpace(_host);
    }

    private async Task RefreshAsync(string? savedIp = null)
    {
        SetBusy(true);
        var savedNotice = savedIp is null ? string.Empty : $"连接 IP 已保存为 {savedIp}。";
        _feedback.Text = savedNotice + "正在读取设备状态…";
        _feedback.Foreground = MutedBrush;
        try
        {
            var versionResponse = await UdpCommandClient.SendAsync(_host, "$GETVERSION");
            if (UdpCommandClient.HasDeviceError(versionResponse))
                throw new IOException($"设备拒绝版本查询：{versionResponse}");
            var versionFields = UdpCommandClient.ParseFields(versionResponse);
            if (!versionFields.TryGetValue("MODEL", out var model) ||
                !string.Equals(model, RadioDeviceInfo.ModelToken(_device), StringComparison.Ordinal))
                throw new IOException($"设备型号不符，期望 {RadioDeviceInfo.ModelToken(_device)}，收到 {model ?? "无"}。");

            _status.Firmware = versionFields.GetValueOrDefault("FW", "--");
            _status.DeviceId = ParseInteger(versionFields, "DEVICE_ID") ?? RadioDeviceInfo.DeviceId(_device);
            _status.State = ParseInteger(versionFields, "DEVICE_STATE");
            await ReadRfStateAsync();

            if (_sFrequency is not null && _status.FrequencyGHz.HasValue)
                _sFrequency.Value = (decimal)(_status.FrequencyGHz.Value * 1000);
            if (_sAttenuation is not null && _status.AttenuationDb.HasValue)
                _sAttenuation.Value = (decimal)_status.AttenuationDb.Value;
            if (_kaFrequency is not null && _status.FrequencyGHz.HasValue)
                _kaFrequency.Value = (decimal)Math.Clamp(_status.FrequencyGHz.Value * 1000.0,
                    (double)KaFrequencyMinMhz, (double)KaFrequencyMaxMhz);
            if (_kaGain is not null && _status.AttenuationDb.HasValue)
            {
                var (gainMin, gainMax) = RadioDeviceInfo.KaGainRangeDb(_device);
                _kaGain.Value = Math.Clamp(
                    RadioDeviceInfo.KaGainFromAttenuation(_device, _status.AttenuationDb.Value),
                    gainMin, gainMax);
            }
            if (_reference is not null)
                _reference.SelectedIndex = _status.External10M == true ? 1 : 0;
            if (_intermediateFrequency is not null)
                _intermediateFrequency.SelectedIndex = _status.IfMHz switch { 720 => 1, 1200 => 2, _ => 0 };

            _feedback.Text = savedNotice + $"设备在线 · 固件 v{_status.Firmware}";
            _feedback.Foreground = GreenBrush;
            _onUpdated(_status);
        }
        catch (Exception ex)
        {
            _status.Online = false;
            _status.Error = ex.Message;
            _feedback.Text = savedNotice + ex.Message;
            _feedback.Foreground = Brush.Parse("#A84F46");
            _onUpdated(_status);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReadRfStateAsync()
    {
        var response = await UdpCommandClient.SendAsync(_host, "$GETRFSTATE");
        if (UdpCommandClient.HasDeviceError(response))
            throw new IOException($"设备状态读取失败：{response.Trim()}");
        var fields = UdpCommandClient.ParseFields(response);
        if (!fields.ContainsKey("RF") || !fields.ContainsKey("10M"))
            throw new IOException($"设备状态响应不完整：{response.Trim()}");

        // 旧固件的综合状态帧只保留两位衰减小数；专用查询保留完整精度并校验双衰减器字段。
        var attenuationResponse = await UdpCommandClient.SendAsync(_host, "$GETATT");
        var attenuation = UdpCommandClient.ReadAttenuation(_device, attenuationResponse);
        var frequency = ParseNumber(fields, "RF");
        _status.FrequencyGHz = frequency is > 0 ? frequency : null;
        _status.Lo2594GHz = ParseNumber(fields, "LO2594");
        _status.Lo2594FixedGHz = ParseNumber(fields, "LO2594_650");
        _status.Lo2595GHz = ParseNumber(fields, "LO2595");
        _status.IfMHz = ParseInteger(fields, "IF");
        _status.Attenuation1Db = attenuation.Att1;
        _status.Attenuation2Db = attenuation.Att2;
        _status.AttenuationDb = attenuation.Att1 + (attenuation.Att2 ?? 0);
        _status.External10M = ParseInteger(fields, "10M") is int external ? external != 0 : null;
        _status.Lock2594 = ParseInteger(fields, "2594_650");
        _status.Lock2594If = ParseInteger(fields, "2594_IF");
        _status.Lock2595 = ParseInteger(fields, "2595");
        _status.Power2594 = ParseInteger(fields, "POWER2594");
        _status.Power2595 = ParseInteger(fields, "POWER");
        _status.PowerDbm = ParseNumber(fields, "POWER_DBM");
        _status.PowerFromControlBoard = ParseInteger(fields, "POWER_VALID") == 1 || _status.PowerDbm.HasValue;
        _status.ShiftMHz = ParseInteger(fields, "SHIFT");
        _status.IpAddress = _host;
        _status.Online = true;
        _status.Error = null;
        _status.LastSeen = DateTime.Now;
    }

    private async Task SaveAsync()
    {
        if (RadioDeviceInfo.IsKaDevice(_device))
        {
            await SaveKaAsync();
            return;
        }
        if (!RadioDeviceInfo.IsSDevice(_device) || _sFrequency?.Value is null ||
            _sAttenuation?.Value is null || _reference is null)
            return;

        SetBusy(true);
        _feedback.Text = "正在下发参数…";
        _feedback.Foreground = MutedBrush;
        var frequencyMhz = _sFrequency.Value.Value;
        var attenuationDb = _sAttenuation.Value.Value;
        var external = _reference.SelectedIndex == 1;
        try
        {
            // 先选择参考源再触发频综校准，避免沿用此前无信号的外部参考。
            await SendSettingAsync($"$SET10MSEL,{(external ? 1 : 0)}");
            await SendSettingAsync($"$SETFREQ,{((double)frequencyMhz / 1000).ToString("F6", CultureInfo.InvariantCulture)}");
            await SendSettingAsync($"$SETATT,{((double)attenuationDb).ToString("F3", CultureInfo.InvariantCulture)}");
            await ReadRfStateAsync();
            _feedback.Text = "配置已下发，频率和增益已保存到设备。";
            _feedback.Foreground = GreenBrush;
            _onUpdated(_status);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (message.Contains("$>ERR,", StringComparison.Ordinal))
            {
                try
                {
                    // 设置可能只完成部分步骤；读取真实状态，同时保留用户待重试的输入。
                    await ReadRfStateAsync();
                    _onUpdated(_status);
                    if (message.Contains("$SETFREQ,", StringComparison.Ordinal))
                        message += $"\n当前锁定状态：LMX2594 {LockText(_status.Lock2594)}，" +
                                   $"LMX2595 {LockText(_status.Lock2595)}；10 MHz：" +
                                   ReferenceText(_status.External10M);
                }
                catch
                {
                    // 状态诊断失败不能覆盖最初的设备错误。
                }
            }
            _feedback.Text = message;
            _feedback.Foreground = Brush.Parse("#A84F46");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveKaAsync()
    {
        if (_kaFrequency?.Value is null || _kaGain?.Value is null ||
            _reference is null || _intermediateFrequency is null)
            return;

        var frequencyMhz = _kaFrequency.Value.Value;
        var gainDb = _kaGain.Value.Value;
        var external = _reference.SelectedIndex == 1;
        var ifMHz = _intermediateFrequency.SelectedIndex switch { 1 => 720, 2 => 1200, _ => 70 };
        var (gainMin, gainMax) = RadioDeviceInfo.KaGainRangeDb(_device);
        if (frequencyMhz < KaFrequencyMinMhz || frequencyMhz > KaFrequencyMaxMhz)
        {
            _feedback.Text = "Ka 波段频率范围为 22.600–27.200 GHz。";
            _feedback.Foreground = Brush.Parse("#A84F46");
            return;
        }
        if (!RadioDeviceInfo.TryKaAttenuationFromGain(_device, gainDb, out var attenuationDb))
        {
            _feedback.Text = $"增益必须在 {gainMin:0.00}–{gainMax:0.00} dB 范围内，步进 0.25 dB。";
            _feedback.Foreground = Brush.Parse("#A84F46");
            return;
        }
        SetBusy(true);
        _feedback.Text = "正在下发 Ka 波段参数…";
        _feedback.Foreground = MutedBrush;
        try
        {
            // Ka 旧协议 1=内部、0=外部，与 S 波段的参数方向相反。
            await SendSettingAsync($"$SET10MSEL,{(external ? 0 : 1)}");
            await SendSettingAsync($"$SETIFFREQ,{ifMHz}");
            await SendSettingAsync($"$SETFREQ,{((double)frequencyMhz / 1000).ToString("F6", CultureInfo.InvariantCulture)}");
            // Ka 下变频器的总衰减由固件一次分配到级联 ATT1 和 ATT2。
            await SendSettingAsync($"$SETATT,{((double)attenuationDb).ToString("F2", CultureInfo.InvariantCulture)}");
            await ReadRfStateAsync();
            _feedback.Text = "Ka 波段参数已下发并保存到设备。";
            _feedback.Foreground = GreenBrush;
            _onUpdated(_status);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (message.Contains("$>ERR,", StringComparison.Ordinal))
            {
                try
                {
                    await ReadRfStateAsync();
                    _onUpdated(_status);
                    if (message.Contains("$SETFREQ,", StringComparison.Ordinal))
                        message += $"\n当前锁定状态：2594_650 {LockText(_status.Lock2594)}，" +
                                   $"2594_IF {LockText(_status.Lock2594If)}，2595 {LockText(_status.Lock2595)}；" +
                                   "10 MHz：" + ReferenceText(_status.External10M);
                }
                catch
                {
                    // 查询失败不能遮盖设备首先返回的设置错误。
                }
            }
            _feedback.Text = message;
            _feedback.Foreground = Brush.Parse("#A84F46");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SendSettingAsync(string command)
    {
        var response = await UdpCommandClient.SendAsync(_host, command);
        if (UdpCommandClient.HasDeviceError(response))
            throw new IOException(UdpCommandClient.DescribeSettingError(command, response));
    }

    private static string LockText(int? state) => state switch { 1 => "已锁定", 0 => "未锁定", _ => "未知" };

    private static string ReferenceText(bool? external) => external switch
    {
        true => "外部参考。", false => "内部参考。", _ => "未知。"
    };

    private static Control FormRow(string label, Control field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), ColumnSpacing = 12, MinHeight = 40 };
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

    private static Control WithUnit(Control control, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = unit, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static double? ParseNumber(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? ParseInteger(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
}
