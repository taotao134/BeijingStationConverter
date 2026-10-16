using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeijingStationConverter;

public sealed class UdpLogListener : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private UdpClient? _client;
    private Task? _receiveTask;

    public event Action<string, IPEndPoint>? PacketReceived;

    public async Task StartAsync()
    {
        _client = new UdpClient(AddressFamily.InterNetwork);
        _client.Client.ExclusiveAddressUse = true;
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, 10080));
        _receiveTask = ReceiveLoopAsync(_cancellation.Token);
        await Task.Yield();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _client!.ReceiveAsync(cancellationToken);
                PacketReceived?.Invoke(Encoding.UTF8.GetString(result.Buffer).TrimEnd('\0', '\r', '\n'), result.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _client?.Dispose();
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch (ObjectDisposedException) { }
        }
        _cancellation.Dispose();
    }
}
