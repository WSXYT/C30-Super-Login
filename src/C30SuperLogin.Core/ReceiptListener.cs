using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace C30SuperLogin.Core;

public sealed class ReceiptListener : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IReadOnlyList<SavedAccount>> accounts = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ActivationSnapshot> activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private readonly Task worker;
    public string Address { get; }
    public ReceiptListener()
    {
        listener.Start(8);
        Address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/receipt/{nonce}";
        worker = Listen();
    }
    private async Task Listen()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(1000);
                var stream = client.GetStream();
                var header = new List<byte>(); byte[] one = new byte[1];
                while (header.Count < 8192) {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) break;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                string text = Encoding.ASCII.GetString(header.ToArray());
                string firstLine = text.Split('\n')[0];
                string[] pieces = firstLine.Trim().Split(' ');
                // Drain a bounded body even for a bad route, so closing does not reset its 404 response.
                byte[] body = [];
                string? lengthLine = text.Split("\r\n").FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthLine is not null) {
                    if (!int.TryParse(lengthLine.Split(':')[1].Trim(), out int bodyLength) || bodyLength is < 0 or > 65536) throw new InvalidDataException();
                    body = new byte[bodyLength]; await stream.ReadExactlyAsync(body, timeout.Token);
                }
                string? result = null;
                IReadOnlyList<SavedAccount>? observed = null;
                if (pieces.Length == 3 && pieces[1].StartsWith($"/receipt/{nonce}?event=", StringComparison.Ordinal))
                {
                    var value = pieces[1][($"/receipt/{nonce}?event=").Length..];
                    if (pieces[0] == "GET" && new[] { "ready", "submitted", "not_ready", "page_mismatch", "busy", "duplicate", "bridge_error", "invalid_credentials", "invalid_operation", "saved_missing", "saved_ambiguous", "password_required", "activation_requested", "activation_not_ready", "activation_status" }.Contains(value)) result = value;
                    if (pieces[0] == "POST" && value == "accounts") {
                        if (body.Length < 2) throw new InvalidDataException();
                        var list = JsonSerializer.Deserialize<List<SavedAccount>>(body, Json.Options) ?? throw new InvalidDataException();
                        if (list.Count > 256 || list.Any(x => x is null || x.Username is null || x.Uid is null || x.DisplayName is null || x.Username.Length is 0 or > 256 || x.Uid.Length > 128 || x.DisplayName.Length > 128)) throw new InvalidDataException();
                        observed = list; result = "accounts";
                    }
                    if (pieces[0] == "POST" && value == "activation_status") {
                        if (body.Length < 2 || body.Length > 4096) throw new InvalidDataException();
                        var snapshot = JsonSerializer.Deserialize<ActivationSnapshot>(body, Json.Options) ?? throw new InvalidDataException();
                        if (snapshot.Time.Length > 128 || snapshot.Tips.Length > 512) throw new InvalidDataException();
                        activation.TrySetResult(snapshot); result = "activation_status";
                    }
                }
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (result is null ? "404 Not Found" : "204 No Content") + "\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
                // A mismatched frame must not beat the actual login frame to the useful receipt.
                if (result is not null && result != "page_mismatch") completion.TrySetResult(result);
                if (observed is not null) accounts.TrySetResult(observed);
                } catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException or SocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    public Task<string> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => completion.Task.WaitAsync(timeout, cancellationToken);
    public Task<IReadOnlyList<SavedAccount>> ReadAccountsAsync(TimeSpan timeout, CancellationToken token) => accounts.Task.WaitAsync(timeout, token);
    public Task<ActivationSnapshot> ReadActivationAsync(TimeSpan timeout, CancellationToken token) => activation.Task.WaitAsync(timeout, token);
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await worker; } catch (ObjectDisposedException) { }
        stop.Dispose();
    }
}
