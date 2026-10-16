using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeijingStationConverter;

public static class TftpUploader
{
    public const int ServerPort = 69;
    public const int MaxImageBytes = 128 * 1024;
    private const int BlockSize = 512;
    private const int RetryCount = 5;

    public static async Task UploadAsync(string host, string filePath, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var image = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (image.Length == 0 || image.Length > MaxImageBytes)
            throw new InvalidDataException($"固件文件大小须为 1 到 {MaxImageBytes / 1024} KB。当前：{image.Length / 1024.0:F1} KB。");
        if (!IPAddress.TryParse(host, out var address))
            throw new ArgumentException("设备 IP 地址无效。", nameof(host));

        using var client = new UdpClient(AddressFamily.InterNetwork);
        var server = new IPEndPoint(address, ServerPort);
        var fileName = Encoding.ASCII.GetBytes(Path.GetFileName(filePath));
        var mode = Encoding.ASCII.GetBytes("octet");
        var optionName = Encoding.ASCII.GetBytes("tsize");
        var optionValue = Encoding.ASCII.GetBytes(image.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var request = new byte[2 + fileName.Length + 1 + mode.Length + 1 + optionName.Length + 1 + optionValue.Length + 1];
        request[1] = 2;
        Buffer.BlockCopy(fileName, 0, request, 2, fileName.Length);
        Buffer.BlockCopy(mode, 0, request, 3 + fileName.Length, mode.Length);
        var optionOffset = 4 + fileName.Length + mode.Length;
        Buffer.BlockCopy(optionName, 0, request, optionOffset, optionName.Length);
        optionOffset += optionName.Length + 1;
        Buffer.BlockCopy(optionValue, 0, request, optionOffset, optionValue.Length);

        var transferEndpoint = await ExchangeAsync(client, server, request, 0, address, cancellationToken);
        var blockNumber = 1;
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataLength = Math.Min(BlockSize, image.Length - offset);
            var packet = new byte[4 + dataLength];
            packet[1] = 3;
            packet[2] = (byte)(blockNumber >> 8);
            packet[3] = (byte)blockNumber;
            if (dataLength > 0)
                Buffer.BlockCopy(image, offset, packet, 4, dataLength);

            await ExchangeAsync(client, transferEndpoint, packet, blockNumber, address, cancellationToken);
            offset += dataLength;
            progress?.Report((double)offset / image.Length);
            if (dataLength < BlockSize)
                break;

            blockNumber = (blockNumber + 1) & 0xffff;
            if (blockNumber == 0)
                throw new InvalidDataException("固件文件超过当前 TFTP 块号可处理范围。");
        }
    }

    private static async Task<IPEndPoint> ExchangeAsync(UdpClient client, IPEndPoint endpoint,
        byte[] packet, int expectedBlock, IPAddress expectedAddress, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            await client.SendAsync(packet, endpoint, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var result = await client.ReceiveAsync(timeout.Token);
                    if (!result.RemoteEndPoint.Address.Equals(expectedAddress))
                        continue;
                    if (result.Buffer.Length < 4)
                        continue;

                    var opcode = (result.Buffer[0] << 8) | result.Buffer[1];
                    if (opcode == 5)
                    {
                        var message = Encoding.ASCII.GetString(result.Buffer, 4, Math.Max(0, result.Buffer.Length - 4));
                        throw new IOException($"设备 TFTP 错误：{message.TrimEnd('\0')}");
                    }
                    if (opcode != 4)
                        continue;

                    var block = (result.Buffer[2] << 8) | result.Buffer[3];
                    if (block == expectedBlock)
                        return result.RemoteEndPoint;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        throw new TimeoutException($"TFTP 等待块 {expectedBlock} 确认超时。");
    }
}
