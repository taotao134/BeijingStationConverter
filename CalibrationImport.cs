using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BeijingStationConverter;

public sealed record CalibrationPoint(
    uint RfMHz,
    ushort IfMHz,
    short InputCdbm,
    ushort VoutMv,
    short PoutCdbm,
    ushort Att2BaseQdb,
    int SourceLine);

public sealed record CalibrationGroupPreview(
    uint RfMHz,
    ushort IfMHz,
    ushort Att2BaseQdb,
    int PointCount,
    int FitPointCount,
    double SlopeDbmPerMv,
    double InterceptDbm);

public sealed class CalibrationDocument
{
    public required string BoardUid { get; init; }
    public required IReadOnlyList<CalibrationPoint> Points { get; init; }
    public required IReadOnlyList<CalibrationGroupPreview> Groups { get; init; }
    public required IReadOnlyList<string> SkippedRows { get; init; }
    public required uint Crc32 { get; init; }
}

public static class CalibrationCsvTemplate
{
    public const string Header =
        "format_version,board_uid,rf_ghz,if_mhz,input_dbm,vout_mv,pout_dbm,att2_base_db";

    private static readonly int[] DefaultInputDbm = { -67, -60, -50, -40, -36 };

    public static string Build()
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");
        foreach (var rf in CalibrationCsvParser.RfPointsMHz)
        {
            var att2BaseDb = rf switch
            {
                23182 or 23200 => 6m,
                25520 => 1m,
                _ => 0m
            };
            foreach (var ifMHz in CalibrationCsvParser.IfPointsMHz)
            {
                foreach (var inputDbm in DefaultInputDbm)
                {
                    builder.Append("1,,")
                        .Append((rf / 1000m).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                        .Append(ifMHz.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(inputDbm.ToString("0.00", CultureInfo.InvariantCulture))
                        .Append(",,,")
                        .Append(att2BaseDb.ToString("0.00", CultureInfo.InvariantCulture))
                        .Append("\r\n");
                }
            }
        }
        return builder.ToString();
    }
}

public static class CalibrationCsvParser
{
    public static readonly uint[] RfPointsMHz = { 23182, 23200, 25520, 25920, 26400 };
    public static readonly ushort[] IfPointsMHz = { 70, 720, 1200 };

    private static readonly string[] Columns =
    {
        "format_version", "board_uid", "rf_ghz", "if_mhz", "input_dbm",
        "vout_mv", "pout_dbm", "att2_base_db"
    };

    public static CalibrationDocument ParseFile(string path)
    {
        var encoding = new UTF8Encoding(false, true);
        return Parse(File.ReadAllText(path, encoding));
    }

    public static CalibrationDocument Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        csv = csv.TrimStart('\uFEFF');
        using var reader = new StringReader(csv);
        var lineNumber = 0;
        string? line;
        List<string>? header = null;
        string? boardUid = null;
        var points = new List<CalibrationPoint>();
        var skippedRows = new List<string>();

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line, lineNumber);
            if (header is null)
            {
                header = fields.Select(value => value.Trim()).ToList();
                if (!header.SequenceEqual(Columns, StringComparer.Ordinal))
                    throw new FormatException("CSV 表头必须按 v1 规定包含 8 列，且列名和顺序完全一致。");
                continue;
            }

            if (fields.Count != Columns.Length)
                throw new FormatException($"第 {lineNumber} 行有 {fields.Count} 列，应为 {Columns.Length} 列。");
            fields = fields.Select(value => value.Trim()).ToList();
            if (fields.All(string.IsNullOrEmpty))
                continue;
            if (fields.Any(string.IsNullOrEmpty))
            {
                skippedRows.Add($"第 {lineNumber} 行：测量或分组字段不完整，已跳过。");
                continue;
            }
            if (fields[0] != "1")
                throw new FormatException($"第 {lineNumber} 行的 format_version 必须为 1。");

            var rowUid = fields[1].ToUpperInvariant();
            if (rowUid.Length != 24 || !rowUid.All(Uri.IsHexDigit))
                throw new FormatException($"第 {lineNumber} 行的 board_uid 必须是 24 位十六进制 STM32 UID。");
            if (boardUid is null)
                boardUid = rowUid;
            else if (!string.Equals(boardUid, rowUid, StringComparison.Ordinal))
                throw new FormatException($"第 {lineNumber} 行的 board_uid 与前面行不一致。");

            var rfMHz = ParseScaledUInt(fields[2], 1000m, "rf_ghz", lineNumber);
            var ifMHz = ParseInteger(fields[3], "if_mhz", lineNumber);
            var inputCdbm = ParseScaledInt16(fields[4], 100m, "input_dbm", lineNumber);
            var voutMv = ParseInteger(fields[5], "vout_mv", lineNumber);
            var poutCdbm = ParseScaledInt16(fields[6], 100m, "pout_dbm", lineNumber);
            var att2Qdb = ParseScaledUInt(fields[7], 4m, "att2_base_db", lineNumber);

            if (inputCdbm is < -10000 or > 10000 || poutCdbm is < -10000 or > 10000)
                throw new FormatException($"第 {lineNumber} 行的 input_dbm 和 pout_dbm 必须在 -100 至 100 dBm。");

            if (!RfPointsMHz.Contains(rfMHz))
                throw new FormatException($"第 {lineNumber} 行的射频频点 {rfMHz} MHz 不在 v1 标定频点内。");
            if (ifMHz > ushort.MaxValue || !IfPointsMHz.Contains((ushort)ifMHz))
                throw new FormatException($"第 {lineNumber} 行的中频 {ifMHz} MHz 不受支持。");
            if (voutMv > 3300)
                throw new FormatException($"第 {lineNumber} 行的 vout_mv 超过 3300 mV。");
            if (att2Qdb > 87)
                throw new FormatException($"第 {lineNumber} 行的 ATT2 基准不得超过 21.75 dB。");

            points.Add(new CalibrationPoint(rfMHz, (ushort)ifMHz, inputCdbm, (ushort)voutMv,
                poutCdbm, (ushort)att2Qdb, lineNumber));
        }

        if (header is null || boardUid is null)
            throw new FormatException("CSV 中没有表头或校准数据行。");

        var normalized = points
            .OrderBy(point => point.RfMHz)
            .ThenBy(point => point.IfMHz)
            .ThenBy(point => point.VoutMv)
            .ThenBy(point => point.InputCdbm)
            .ThenBy(point => point.PoutCdbm)
            .ToArray();
        var groups = BuildAndValidateGroups(normalized);
        return new CalibrationDocument
        {
            BoardUid = boardUid,
            Points = normalized,
            Groups = groups,
            SkippedRows = skippedRows,
            Crc32 = CalculateCrc32(normalized)
        };
    }

    public static uint CalculateCrc32(IEnumerable<CalibrationPoint> points)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var point in points)
        {
            UpdateUInt32(ref crc, point.RfMHz);
            UpdateUInt16(ref crc, point.IfMHz);
            UpdateUInt16(ref crc, unchecked((ushort)point.InputCdbm));
            UpdateUInt16(ref crc, point.VoutMv);
            UpdateUInt16(ref crc, unchecked((ushort)point.PoutCdbm));
            UpdateUInt16(ref crc, point.Att2BaseQdb);
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static IReadOnlyList<CalibrationGroupPreview> BuildAndValidateGroups(
        IReadOnlyList<CalibrationPoint> points)
    {
        var groups = new List<CalibrationGroupPreview>(RfPointsMHz.Length * IfPointsMHz.Length);
        foreach (var rf in RfPointsMHz)
        {
            ushort? rfAtt2 = null;
            foreach (var ifMHz in IfPointsMHz)
            {
                var group = points.Where(point => point.RfMHz == rf && point.IfMHz == ifMHz).ToArray();
                if (group.Length is < 2 or > 8)
                    throw new FormatException($"{rf} MHz / {ifMHz} MHz 需要 2 至 8 个完整测量点，当前为 {group.Length}。");

                var att2 = group[0].Att2BaseQdb;
                if (group.Any(point => point.Att2BaseQdb != att2))
                    throw new FormatException($"{rf} MHz 的 ATT2 基准必须在所有中频组和测量行中一致。");
                if (rfAtt2.HasValue && rfAtt2.Value != att2)
                    throw new FormatException($"{rf} MHz 的 ATT2 基准必须在所有中频组中一致。");
                rfAtt2 = att2;

                var fit = group.Where(point => point.VoutMv >= 79).ToArray();
                if (fit.Select(point => point.VoutMv).Distinct().Count() < 2)
                    throw new FormatException($"{rf} MHz / {ifMHz} MHz 至少需要两个电压不同且 Vout 不低于 79 mV 的点。");
                var (slope, intercept) = Fit(fit);
                groups.Add(new CalibrationGroupPreview(rf, ifMHz, att2, group.Length,
                    fit.Length, slope, intercept));
            }
        }
        if (points.Count is < 30 or > 120)
            throw new FormatException("完整校准表需包含 30 至 120 个测量点。");
        return groups;
    }

    private static (double Slope, double Intercept) Fit(IReadOnlyList<CalibrationPoint> points)
    {
        var averageX = points.Average(point => (double)point.VoutMv);
        var averageY = points.Average(point => point.PoutCdbm / 100.0);
        var numerator = points.Sum(point => (point.VoutMv - averageX) *
            (point.PoutCdbm / 100.0 - averageY));
        var denominator = points.Sum(point => Math.Pow(point.VoutMv - averageX, 2));
        var slope = numerator / denominator;
        return (slope, averageY - slope * averageX);
    }

    private static List<string> ParseCsvLine(string line, int lineNumber)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (quoted)
            {
                if (character == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == ',' )
            {
                result.Add(field.ToString());
                field.Clear();
            }
            else if (character == '"')
            {
                if (field.Length != 0)
                    throw new FormatException($"第 {lineNumber} 行的 CSV 引号位置无效。");
                quoted = true;
            }
            else
            {
                field.Append(character);
            }
        }
        if (quoted)
            throw new FormatException($"第 {lineNumber} 行的 CSV 引号未闭合。");
        result.Add(field.ToString());
        return result;
    }

    private static uint ParseScaledUInt(string text, decimal scale, string name, int line)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed) || parsed < 0 ||
            parsed * scale != decimal.Truncate(parsed * scale) || parsed * scale > uint.MaxValue)
            throw new FormatException($"第 {line} 行的 {name} 数值或单位格式无效。");
        return (uint)(parsed * scale);
    }

    private static short ParseScaledInt16(string text, decimal scale, string name, int line)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed) ||
            parsed * scale != decimal.Truncate(parsed * scale) ||
            parsed * scale < short.MinValue || parsed * scale > short.MaxValue)
            throw new FormatException($"第 {line} 行的 {name} 数值或小数精度无效。");
        return (short)(parsed * scale);
    }

    private static uint ParseInteger(string text, string name, int line)
    {
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"第 {line} 行的 {name} 必须是非负整数。");
        return parsed;
    }

    private static void UpdateUInt16(ref uint crc, ushort value)
    {
        UpdateByte(ref crc, (byte)value);
        UpdateByte(ref crc, (byte)(value >> 8));
    }

    private static void UpdateUInt32(ref uint crc, uint value)
    {
        UpdateUInt16(ref crc, (ushort)value);
        UpdateUInt16(ref crc, (ushort)(value >> 16));
    }

    private static void UpdateByte(ref uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }
}

public static class CalibrationTransferClient
{
    private const int MaximumRetries = 3;

    public static async Task ImportAsync(
        string host,
        CalibrationDocument document,
        Func<string, string, CancellationToken, Task<string>> send,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(send);
        var version = await SendWithRetry(host, "$GETVERSION", send, cancellationToken);
        var model = UdpCommandClient.ParseFields(version).GetValueOrDefault("MODEL");
        if (!string.Equals(model, "KA_DOWN", StringComparison.Ordinal))
            throw new IOException($"目标设备型号为 {model ?? "未知"}，只允许导入 KA_DOWN。");

        var infoResponse = await SendWithRetry(host, "$CAL,INFO", send, cancellationToken);
        RequireResponse(infoResponse, "$CAL,INFO,");
        var info = UdpCommandClient.ParseFields(infoResponse);
        if (!string.Equals(info.GetValueOrDefault("VERSION"), "1", StringComparison.Ordinal))
            throw new IOException($"目标显示板校准协议版本 {info.GetValueOrDefault("VERSION", "缺失")} 不受支持。");
        if (!info.TryGetValue("UID", out var deviceUid) ||
            !string.Equals(deviceUid, document.BoardUid, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"CSV UID {document.BoardUid} 与目标显示板 UID {deviceUid ?? "缺失"} 不匹配。");

        var session = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var beginAttempted = false;
        try
        {
            var begin = $"$CAL,BEGIN,S={session},N={document.Points.Count},CRC={document.Crc32:X8}";
            beginAttempted = true;
            var beginResponse = await SendWithRetry(host, begin, send, cancellationToken);
            RequireResponse(beginResponse, "$CAL,BEGIN,OK,");

            for (var i = 0; i < document.Points.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var point = document.Points[i];
                var command = $"$CAL,DATA,S={session},I={i},RF={point.RfMHz},IF={point.IfMHz}," +
                    $"IN={point.InputCdbm},V={point.VoutMv},OUT={point.PoutCdbm},ATT={point.Att2BaseQdb}";
                if (command.Length + 2 > 240)
                    throw new IOException("校准测量点命令超过 240 字节协议上限。");
                var response = await SendWithRetry(host, command, send, cancellationToken);
                RequireResponse(response, "$CAL,DATA,OK,");
                var fields = UdpCommandClient.ParseFields(response);
                if (!Matches(fields, "S", session) || !Matches(fields, "I", i))
                    throw new IOException($"校准点 {i + 1} 收到不匹配的序号确认：{response.Trim()}");
                progress?.Invoke($"已传输 {i + 1}/{document.Points.Count} 个测量点。");
            }

            var commit = $"$CAL,COMMIT,S={session},CRC={document.Crc32:X8}";
            var commitResponse = await SendWithRetry(host, commit, send, cancellationToken);
            RequireResponse(commitResponse, "$CAL,COMMIT,OK,");
            var commitFields = UdpCommandClient.ParseFields(commitResponse);
            if (!Matches(commitFields, "S", session) ||
                !string.Equals(commitFields.GetValueOrDefault("CRC"), $"{document.Crc32:X8}", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"显示板提交确认与当前校准表不一致：{commitResponse.Trim()}");
            progress?.Invoke($"校准表已写入，代次 {commitFields.GetValueOrDefault("GEN", "?")}，CRC {document.Crc32:X8}。");
        }
        catch
        {
            if (beginAttempted)
            {
                try
                {
                    _ = await SendWithRetry(host, $"$CAL,ABORT,S={session}", send,
                        CancellationToken.None);
                }
                catch
                {
                    // 原始导入错误更有助于定位失败点；设备写入在 COMMIT 前不改变当前槽。
                }
            }
            throw;
        }
    }

    private static async Task<string> SendWithRetry(
        string host,
        string command,
        Func<string, string, CancellationToken, Task<string>> send,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await send(host, command, cancellationToken);
                if (response.Contains("$>ERR,CAL_TIMEOUT", StringComparison.Ordinal))
                    throw new TimeoutException($"主控等待显示板响应超时：{command}");
                if (response.Contains("$CAL,ERR,", StringComparison.Ordinal) ||
                    UdpCommandClient.HasDeviceError(response))
                    throw new IOException($"指令 {command} 被设备拒绝：{response.Trim()}");
                return response;
            }
            catch (TimeoutException) when (attempt < MaximumRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static void RequireResponse(string response, string prefix)
    {
        if (!response.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException($"显示板返回格式错误：{response.Trim()}");
    }

    private static bool Matches(IReadOnlyDictionary<string, string> fields, string name, long expected) =>
        fields.TryGetValue(name, out var value) &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed == expected;
}
