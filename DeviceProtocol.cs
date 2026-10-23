using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeijingStationConverter;

public enum RadioDevice
{
    SUp,
    SDown,
    KaUp,
    KaDown
}

public static class RadioDeviceInfo
{
    public static string DisplayName(RadioDevice device) => device switch
    {
        RadioDevice.SUp => "S 波段上变频器",
        RadioDevice.SDown => "S 波段下变频器",
        RadioDevice.KaUp => "Ka 波段上变频器",
        _ => "Ka 波段下变频器"
    };

    public static string ModelToken(RadioDevice device) => device switch
    {
        RadioDevice.SUp => "S_UP",
        RadioDevice.SDown => "S_DOWN",
        RadioDevice.KaUp => "KA_UP",
        _ => "KA_DOWN"
    };

    public static int DeviceId(RadioDevice device) => device switch
    {
        RadioDevice.SUp => 1,
        RadioDevice.SDown => 2,
        RadioDevice.KaUp => 3,
        _ => 4
    };

    public static RadioDevice? FromIdentity(string? model, int? deviceId)
    {
        var fromId = deviceId switch
        {
            1 => RadioDevice.SUp,
            2 => RadioDevice.SDown,
            3 => RadioDevice.KaUp,
            4 => RadioDevice.KaDown,
            _ => (RadioDevice?)null
        };
        var fromModel = model?.ToUpperInvariant() switch
        {
            "S_UP" => RadioDevice.SUp,
            "S_DOWN" => RadioDevice.SDown,
            "KA_UP" => RadioDevice.KaUp,
            "KA_DOWN" => RadioDevice.KaDown,
            _ => (RadioDevice?)null
        };
        // 新固件的编号和型号必须一致，旧固件缺少编号时仍按型号识别。
        if (deviceId.HasValue && (fromId is null || fromModel.HasValue && fromId != fromModel))
            return null;
        return fromId ?? fromModel;
    }

    public static bool IsSDevice(RadioDevice device) => device is RadioDevice.SUp or RadioDevice.SDown;

    public static bool IsKaDevice(RadioDevice device) => device is RadioDevice.KaUp or RadioDevice.KaDown;

    /// <summary>返回 S 波段设备允许的增益范围，单位 dB。</summary>
    public static (decimal Min, decimal Max) SGainRangeDb(RadioDevice device) =>
        device == RadioDevice.SDown ? (0m, 40m) : (5m, 35m);

    /// <summary>返回 S 波段设备增益设置的硬件步进，单位 dB。</summary>
    public static decimal SGainStepDb(RadioDevice device) => device == RadioDevice.SDown ? 0.125m : 0.25m;

    /// <summary>返回 S 波段设备的最大增益基准，单位 dB。</summary>
    public static decimal SGainOffsetDb(RadioDevice device) => device == RadioDevice.SDown ? 40m : 35m;

    /// <summary>按 S 波段设备基准将控制板衰减换算为用户可见增益，单位 dB。</summary>
    public static decimal SGainFromAttenuation(RadioDevice device, double attenuationDb)
    {
        var (min, max) = SGainRangeDb(device);
        return Math.Clamp(SGainOffsetDb(device) - (decimal)attenuationDb, min, max);
    }

    /// <summary>校验 S 波段增益并换算为控制板衰减，单位 dB。</summary>
    public static bool TrySAttenuationFromGain(RadioDevice device, decimal gainDb, out decimal attenuationDb)
    {
        var (min, max) = SGainRangeDb(device);
        var step = SGainStepDb(device);
        attenuationDb = SGainOffsetDb(device) - gainDb;
        return IsSDevice(device) && gainDb >= min && gainDb <= max &&
               gainDb % step == 0m && attenuationDb >= 0m;
    }

    /// <summary>按设备波段限制功率读数显示范围，单位 dBm。</summary>
    public static string FormatPowerDbm(RadioDevice device, bool valid, double? powerDbm)
    {
        if (!valid || !powerDbm.HasValue || !double.IsFinite(powerDbm.Value))
            return "--dBm";

        if (powerDbm.Value < -30.0)
            return "<-30dBm";
        var maxPower = IsSDevice(device) ? 15.0 : 16.0;
        if (powerDbm.Value > maxPower)
            return IsSDevice(device) ? ">15dBm" : ">16dBm";

        return $"{powerDbm.Value:+0.00;-0.00;0} dBm";
    }

    public static bool HasSecondAttenuator(RadioDevice device) =>
        device is RadioDevice.SDown or RadioDevice.KaDown;

    /// <summary>返回显示板使用的 Ka 增益互补基准，单位 dB。</summary>
    public static decimal KaGainOffsetDb(RadioDevice device) => device == RadioDevice.KaDown ? 55m : 35m;

    /// <summary>返回显示板允许的 Ka 增益范围，单位 dB。</summary>
    public static (decimal Min, decimal Max) KaGainRangeDb(RadioDevice device) =>
        device == RadioDevice.KaDown ? (15m, 55m) : (5m, 35m);

    /// <summary>按显示板规则将控制板衰减换算为用户可见增益，单位 dB。</summary>
    public static decimal KaGainFromAttenuation(RadioDevice device, double attenuationDb)
    {
        var (min, max) = KaGainRangeDb(device);
        var gain = KaGainOffsetDb(device) - (decimal)attenuationDb;
        return Math.Clamp(gain, min, max);
    }

    /// <summary>按显示板规则将用户输入的 Ka 增益换算为控制板衰减，单位 dB。</summary>
    public static bool TryKaAttenuationFromGain(RadioDevice device, decimal gainDb, out decimal attenuationDb)
    {
        var (min, max) = KaGainRangeDb(device);
        attenuationDb = KaGainOffsetDb(device) - gainDb;
        return IsKaDevice(device) && gainDb >= min && gainDb <= max &&
               gainDb % 0.25m == 0m && attenuationDb >= 0m;
    }

    /// <summary>校验设备 IPv4 主机地址，并返回固件网络命令所需的四段字节。</summary>
    public static bool TryParseDeviceIp(string text, out byte[] octets)
    {
        octets = Array.Empty<byte>();
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(part => !byte.TryParse(part, NumberStyles.None,
                CultureInfo.InvariantCulture, out _)))
            return false;
        octets = parts.Select(part => byte.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return octets[0] is > 0 and < 224 && octets[3] is >= 2 and <= 254;
    }
}

public sealed class DeviceStatus
{
    public bool Online { get; set; }
    public int? DeviceId { get; set; }
    public int? State { get; set; }
    public string? IpAddress { get; set; }
    public string Firmware { get; set; } = "--";
    public double? FrequencyGHz { get; set; }
    public double? Lo2594GHz { get; set; }
    public double? Lo2594FixedGHz { get; set; }
    public double? Lo2595GHz { get; set; }
    public double? AttenuationDb { get; set; }
    public double? Attenuation1Db { get; set; }
    public double? Attenuation2Db { get; set; }
    public bool? External10M { get; set; }
    public int? IfMHz { get; set; }
    public int? Lock2594 { get; set; }
    public int? Lock2594If { get; set; }
    public int? Lock2595 { get; set; }
    public int? Power2594 { get; set; }
    public int? Power2595 { get; set; }
    public double? PowerDbm { get; set; }
    public bool PowerFromControlBoard { get; set; }
    public int? ShiftMHz { get; set; }
    public DateTime LastSeen { get; set; }
    public string? Error { get; set; }
}

public sealed class AppSettings
{
    public string SUpIp { get; set; } = "192.168.1.10";
    public string SDownIp { get; set; } = "192.168.1.10";
    public string KaUpIp { get; set; } = "192.168.1.10";
    public string KaDownIp { get; set; } = "192.168.1.10";

    public string GetIp(RadioDevice device) => device switch
    {
        RadioDevice.SUp => SUpIp,
        RadioDevice.SDown => SDownIp,
        RadioDevice.KaUp => KaUpIp,
        _ => KaDownIp
    };

    public void SetIp(RadioDevice device, string ip)
    {
        if (device == RadioDevice.SUp) SUpIp = ip;
        if (device == RadioDevice.SDown) SDownIp = ip;
        if (device == RadioDevice.KaUp) KaUpIp = ip;
        if (device == RadioDevice.KaDown) KaDownIp = ip;
    }
}

public static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BeijingStationConverter",
        "devices.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                    ?? new AppSettings();
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class UdpCommandClient
{
    public const int CommandPort = 10071;

    public static async Task<string> SendAsync(string host, string command, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(host, out var address))
            throw new ArgumentException("设备 IP 地址无效。", nameof(host));

        using var client = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(address, CommandPort);
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await client.SendAsync(bytes, endpoint, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var response = await client.ReceiveAsync(timeout.Token);
                if (!response.RemoteEndPoint.Address.Equals(address))
                    continue;

                var text = Encoding.ASCII.GetString(response.Buffer).Trim();
                if (string.Equals(text, command, StringComparison.Ordinal))
                    continue;

                return text;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"等待设备 {host}:{CommandPort} 响应超时。命令：{command}");
        }
    }

    public static bool HasDeviceError(string response) => response.Contains("$>ERR,", StringComparison.Ordinal);

    /// <summary>校验衰减查询的必要字段，缺失或无效值不能按 0 dB 显示。</summary>
    public static (double Att1, double? Att2) ReadAttenuation(RadioDevice device, string response)
    {
        var fields = ParseFields(response);
        static double? ReadValue(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0 ? value : null;

        var att1 = ReadValue(fields, "ATT1");
        var att2 = RadioDeviceInfo.HasSecondAttenuator(device) ? ReadValue(fields, "ATT2") : null;
        if (HasDeviceError(response) || !att1.HasValue ||
            (RadioDeviceInfo.HasSecondAttenuator(device) && !att2.HasValue))
            throw new IOException($"设备衰减响应不完整或无效：{response.Trim()}");
        return (att1.Value, att2);
    }

    /// <summary>保留失败指令和设备原始响应，避免多条设置指令共用含糊的错误提示。</summary>
    public static string DescribeSettingError(string command, string response)
    {
        var reason = response.Contains("$>ERR,ROLLBACK", StringComparison.Ordinal)
            ? "新配置失败，旧配置也未能恢复。"
            : response.Contains("$>ERR,LOCK", StringComparison.Ordinal)
                ? "频综未锁定。"
                : response.Contains("$>ERR,FLASH", StringComparison.Ordinal)
                    ? "设备参数保存校验失败。"
                    : string.Empty;
        return $"指令 {command} 下发失败：{response.Trim()}" +
               (reason.Length > 0 ? $"\n{reason}" : string.Empty);
    }

    public static Dictionary<string, string> ParseFields(string response)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in response.Replace("\r", string.Empty).Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator > 0)
                fields[token[..separator].Trim().TrimStart('$')] = token[(separator + 1)..].Trim();
        }
        return fields;
    }

    public static double? ReadDouble(string response, string key)
    {
        var fields = ParseFields(response);
        return fields.TryGetValue(key, out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
