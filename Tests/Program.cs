using System.Globalization;
using System.Text;
using BeijingStationConverter;

const string Uid = "00112233445566778899AABB";
var checks = new List<(string Name, Action Test)>
{
    ("standard CSV template has all RF/IF/input rows", TestTemplate),
    ("BOM, all groups, low-voltage point and CRC", TestValidCsv),
    ("incomplete row is skipped and reported", TestIncompleteRow),
    ("reject invalid version, UID, RF, IF, units and group size", TestCsvRejections),
    ("reject incomplete curves and inconsistent ATT2", TestCurveRejections),
    ("transfer retries the same point and checks acknowledgements", TestSuccessfulTransfer),
    ("reject wrong model, protocol version and UID before BEGIN", TestTargetMismatch),
    ("abort after transfer failure", TestAbortOnFailure)
};

foreach (var (name, test) in checks)
{
    test();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{checks.Count} calibration import checks passed.");

static void TestTemplate()
{
    var csv = CalibrationCsvTemplate.Build();
    var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 76, "header plus 75 template rows");
    Assert(lines[0] == "format_version,board_uid,rf_ghz,if_mhz,input_dbm,vout_mv,pout_dbm,att2_base_db",
        "template header matches v1");
    Assert(lines[1] == "1,,23.182,70,-67.00,,,6.00", "first template row is fillable");
    Assert(lines[^1] == "1,,26.400,1200,-36.00,,,0.00", "last template row is fillable");
    Assert(lines.All(line => line.Split(',').Length == 8), "every template row has eight columns");

    var completedLines = new List<string> { lines[0] };
    foreach (var line in lines.Skip(1))
    {
        var fields = line.Split(',');
        fields[1] = Uid;
        fields[5] = (80 + completedLines.Count % 5 * 20).ToString(CultureInfo.InvariantCulture);
        fields[6] = "-14.00";
        completedLines.Add(string.Join(',', fields));
    }
    var document = CalibrationCsvParser.Parse(string.Join("\r\n", completedLines));
    Assert(document.Points.Count == 75 && document.Groups.Count == 15,
        "filled template parses as a complete calibration dataset");
}

static void TestValidCsv()
{
    var csv = "\uFEFF" + MakeCsv(includeLowPoint: true);
    var document = CalibrationCsvParser.Parse(csv);
    Assert(document.BoardUid == Uid, "UID normalization");
    Assert(document.Groups.Count == 15, "15 RF/IF groups");
    Assert(document.Points.Count == 31, "all complete points retained");
    var lowGroup = document.Groups.Single(group => group.RfMHz == 23182 && group.IfMHz == 70);
    Assert(lowGroup.PointCount == 3 && lowGroup.FitPointCount == 2, "low voltage excluded from fit only");
    Assert(document.Groups.Single(group => group.RfMHz == 23182 && group.IfMHz == 720).Att2BaseQdb == 24,
        "ATT2 quarter-dB conversion");
    Assert(document.Crc32 == CalibrationCsvParser.CalculateCrc32(document.Points), "stable normalized CRC");
}

static void TestIncompleteRow()
{
    var source = MakeCsv();
    var newline = source.IndexOf('\n');
    var csv = source[..(newline + 1)] + "1," + Uid + ",23.182,70,,125,-14.50,6\n" +
        source[(newline + 1)..];
    var document = CalibrationCsvParser.Parse(csv);
    Assert(document.Points.Count == 30, "incomplete row omitted");
    Assert(document.SkippedRows.Count == 1 && document.SkippedRows[0].Contains("第 2 行", StringComparison.Ordinal),
        "skipped source line reported");
}

static void TestCsvRejections()
{
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace("\n1,", "\n2,", StringComparison.Ordinal)), "version");
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace(Uid, "0011", StringComparison.Ordinal)), "UID");
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace("23.182", "23.183", StringComparison.Ordinal)), "RF");
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace(",70,", ",700,", StringComparison.Ordinal)), "IF");
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace("-67.00", "-67.001", StringComparison.Ordinal)), "unit precision");
    ExpectFormat(() => CalibrationCsvParser.Parse(MakeCsv().Replace("-67.00", "-101.00", StringComparison.Ordinal)), "firmware dBm range");

    var ninthPoint = "1," + Uid + ",23.182,70,-60.00,110,-14.00,6";
    var overfull = MakeCsv().Replace("\n", "\n" + ninthPoint + "\n", StringComparison.Ordinal);
    ExpectFormat(() => CalibrationCsvParser.Parse(overfull), "more than eight points");
}

static void TestCurveRejections()
{
    var missingIf = MakeCsv().Replace("1," + Uid + ",23.182,70,-67.00,79,-15.00,6.00\r\n", string.Empty,
        StringComparison.Ordinal);
    ExpectFormat(() => CalibrationCsvParser.Parse(missingIf), "missing group");

    var onlyLowVoltage = MakeCsv().Replace("23.182,70,-67.00,79,-15.00,6.00", "23.182,70,-67.00,78,-15.00,6.00",
        StringComparison.Ordinal).Replace("23.182,70,-60.00,100,-14.00,6.00", "23.182,70,-60.00,78,-14.00,6.00",
        StringComparison.Ordinal);
    ExpectFormat(() => CalibrationCsvParser.Parse(onlyLowVoltage), "fewer than two fit points");

    var mismatch = MakeCsv().Replace("23.182,720,-67.00,79,-15.00,6.00", "23.182,720,-67.00,79,-15.00,6.25",
        StringComparison.Ordinal);
    ExpectFormat(() => CalibrationCsvParser.Parse(mismatch), "ATT2 mismatch");
}

static void TestSuccessfulTransfer()
{
    var document = CalibrationCsvParser.Parse(MakeCsv());
    var sent = new List<string>();
    var dataAttempts = 0;
    var firstData = string.Empty;

    Task<string> Send(string host, string command, CancellationToken token)
    {
        Assert(host == "192.168.1.18", "current KaDown endpoint used");
        Assert(command.Length + 2 <= 240, "UART line fits send buffer");
        sent.Add(command);
        if (command == "$GETVERSION")
            return Task.FromResult("$VERSION,MODEL=KA_DOWN,DEVICE_ID=4\r\n$>OK\r\n");
        if (command == "$CAL,INFO")
            return Task.FromResult($"$CAL,INFO,VERSION=1,UID={Uid},GEN=0,COUNT=0,CRC=00000000,VALID=0\r\n");
        if (command.StartsWith("$CAL,BEGIN,", StringComparison.Ordinal))
        {
            var fields = UdpCommandClient.ParseFields(command);
            Assert(fields["N"] == document.Points.Count.ToString(CultureInfo.InvariantCulture), "BEGIN point count");
            Assert(fields["CRC"] == $"{document.Crc32:X8}", "BEGIN CRC");
            return Task.FromResult($"$CAL,BEGIN,OK,S={fields["S"]},N={fields["N"]}\r\n");
        }
        if (command.StartsWith("$CAL,DATA,", StringComparison.Ordinal))
        {
            if (dataAttempts++ == 0)
            {
                firstData = command;
                throw new TimeoutException("simulated lost ACK");
            }
            if (command == firstData)
                Assert(true, "same DATA retried");
            var fields = UdpCommandClient.ParseFields(command);
            return Task.FromResult($"$CAL,DATA,OK,S={fields["S"]},I={fields["I"]}\r\n");
        }
        if (command.StartsWith("$CAL,COMMIT,", StringComparison.Ordinal))
            return Task.FromResult($"$CAL,COMMIT,OK,S={UdpCommandClient.ParseFields(command)["S"]},GEN=2,CRC={document.Crc32:X8}\r\n");
        throw new InvalidOperationException($"Unexpected command {command}");
    }

    CalibrationTransferClient.ImportAsync("192.168.1.18", document, Send).GetAwaiter().GetResult();
    Assert(sent.Count(command => command.StartsWith("$CAL,DATA,", StringComparison.Ordinal)) == 31,
        "one timed-out DATA was retried once");
    Assert(sent[3] == sent[4], "retransmission is byte-identical");
    Assert(sent[^1].StartsWith("$CAL,COMMIT,", StringComparison.Ordinal), "COMMIT sent last");
}

static void TestTargetMismatch()
{
    var document = CalibrationCsvParser.Parse(MakeCsv());
    var sent = new List<string>();
    Task<string> WrongModel(string host, string command, CancellationToken token)
    {
        sent.Add(command);
        return Task.FromResult("$VERSION,MODEL=KA_UP\r\n");
    }
    ExpectIOException(() => CalibrationTransferClient.ImportAsync("host", document, WrongModel)
        .GetAwaiter().GetResult(), "wrong model");
    Assert(sent.Count == 1, "wrong model rejected before calibration info");

    sent.Clear();
    Task<string> WrongUid(string host, string command, CancellationToken token)
    {
        sent.Add(command);
        return Task.FromResult(command == "$GETVERSION"
            ? "$VERSION,MODEL=KA_DOWN\r\n"
            : "$CAL,INFO,VERSION=1,UID=FFEEDDCCBBAA998877665544,GEN=0,COUNT=0,CRC=00000000,VALID=0\r\n");
    }
    ExpectIOException(() => CalibrationTransferClient.ImportAsync("host", document, WrongUid)
        .GetAwaiter().GetResult(), "wrong board UID");
    Assert(!sent.Any(command => command.StartsWith("$CAL,BEGIN,", StringComparison.Ordinal)),
        "UID mismatch rejected before BEGIN");

    sent.Clear();
    Task<string> WrongProtocol(string host, string command, CancellationToken token)
    {
        sent.Add(command);
        return Task.FromResult(command == "$GETVERSION"
            ? "$VERSION,MODEL=KA_DOWN\r\n"
            : $"$CAL,INFO,VERSION=2,UID={Uid},GEN=0,COUNT=0,CRC=00000000,VALID=0\r\n");
    }
    ExpectIOException(() => CalibrationTransferClient.ImportAsync("host", document, WrongProtocol)
        .GetAwaiter().GetResult(), "unsupported calibration protocol version");
    Assert(!sent.Any(command => command.StartsWith("$CAL,BEGIN,", StringComparison.Ordinal)),
        "unsupported protocol rejected before BEGIN");
}

static void TestAbortOnFailure()
{
    var document = CalibrationCsvParser.Parse(MakeCsv());
    var sent = new List<string>();
    Task<string> Send(string host, string command, CancellationToken token)
    {
        sent.Add(command);
        if (command == "$GETVERSION") return Task.FromResult("$VERSION,MODEL=KA_DOWN\r\n");
        if (command == "$CAL,INFO") return Task.FromResult($"$CAL,INFO,VERSION=1,UID={Uid},GEN=0,COUNT=0,CRC=00000000,VALID=0\r\n");
        if (command.StartsWith("$CAL,BEGIN,", StringComparison.Ordinal))
            return Task.FromResult($"$CAL,BEGIN,OK,S={UdpCommandClient.ParseFields(command)["S"]},N={document.Points.Count}\r\n");
        if (command.StartsWith("$CAL,DATA,", StringComparison.Ordinal))
            return Task.FromResult("$CAL,ERR,CODE=DATA\r\n");
        if (command.StartsWith("$CAL,ABORT,", StringComparison.Ordinal))
            return Task.FromResult($"$CAL,ABORT,OK,S={UdpCommandClient.ParseFields(command)["S"]}\r\n");
        throw new InvalidOperationException($"Unexpected command {command}");
    }
    ExpectIOException(() => CalibrationTransferClient.ImportAsync("host", document, Send)
        .GetAwaiter().GetResult(), "DATA failure");
    Assert(sent.Any(command => command.StartsWith("$CAL,ABORT,", StringComparison.Ordinal)),
        "failed active session is aborted");
}

static string MakeCsv(bool includeLowPoint = false)
{
    var builder = new StringBuilder();
    builder.AppendLine("format_version,board_uid,rf_ghz,if_mhz,input_dbm,vout_mv,pout_dbm,att2_base_db");
    foreach (var rf in CalibrationCsvParser.RfPointsMHz)
    {
        var att2 = rf switch { 23182 or 23200 => 6m, 25520 => 1m, _ => 0m };
        foreach (var ifMHz in CalibrationCsvParser.IfPointsMHz)
        {
            var points = includeLowPoint && rf == 23182 && ifMHz == 70
                ? new[] { (78, -1550), (79, -1500), (100, -1400) }
                : new[] { (79, -1500), (100, -1400) };
            foreach (var (vout, poutCdbm) in points)
            {
                builder.Append("1,").Append(Uid).Append(',')
                    .Append((rf / 1000m).ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                    .Append(ifMHz.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append("-67.00,").Append(vout.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append((poutCdbm / 100m).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                    .AppendLine(att2.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }
    }
    return builder.ToString();
}

static void ExpectFormat(Action action, string name)
{
    try { action(); }
    catch (FormatException) { return; }
    throw new InvalidOperationException($"Expected CSV rejection: {name}");
}

static void ExpectIOException(Action action, string name)
{
    try { action(); }
    catch (IOException) { return; }
    throw new InvalidOperationException($"Expected transfer rejection: {name}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
