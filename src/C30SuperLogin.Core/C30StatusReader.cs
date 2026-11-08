using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace C30SuperLogin.Core;

internal static class C30StatusReader
{
    private const string Endpoint = "http://127.0.0.1:9099/json/list";
    private const string Expression =
        "(() => { try { " +
        "if (typeof wincore === 'undefined' || !wincore.System || typeof wincore.System.get_stupc_register !== 'function') " +
        "return { ready: false, error: 'interface_missing' }; " +
        "const value = wincore.System.get_stupc_register(); " +
        "return { ready: true, result: value && value.result, time: value && value.time, tips: value && value.tips }; " +
        "} catch (error) { return { ready: false, error: String(error) }; } })()";

    public static async Task<ActivationSnapshot?> TryReadAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var document = JsonDocument.Parse(await http.GetStringAsync(Endpoint, token));
            string? socketUrl = null;
            foreach (var target in document.RootElement.EnumerateArray())
            {
                string url = target.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string type = target.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "page" && url.Contains("/mainProject/", StringComparison.OrdinalIgnoreCase) &&
                    target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                {
                    socketUrl = ws.GetString();
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(socketUrl)) return null;

            using var socket = new ClientWebSocket();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new Uri(socketUrl), connectTimeout.Token);
            string request = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = Expression, returnByValue = true, awaitPromise = true }
            });
            byte[] requestBytes = Encoding.UTF8.GetBytes(request);
            await socket.SendAsync(requestBytes, WebSocketMessageType.Text, true, connectTimeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            while (buffer.Length < 65536)
            {
                var result = await socket.ReceiveAsync(chunk, connectTimeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                buffer.Write(chunk, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            using var reply = JsonDocument.Parse(buffer.ToArray());
            if (!reply.RootElement.TryGetProperty("result", out var response) ||
                !response.TryGetProperty("result", out var remote) ||
                !remote.TryGetProperty("value", out var value) ||
                !value.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
                return null;
            bool authorized = value.TryGetProperty("result", out var auth) &&
                (auth.ValueKind == JsonValueKind.True ||
                 auth.ValueKind == JsonValueKind.String && auth.GetString() == "true" ||
                 auth.ValueKind == JsonValueKind.Number && auth.TryGetInt32(out int number) && number == 1);
            string time = value.TryGetProperty("time", out var timeValue) && timeValue.ValueKind == JsonValueKind.String
                ? timeValue.GetString() ?? "" : "";
            string tips = value.TryGetProperty("tips", out var tipsValue) && tipsValue.ValueKind == JsonValueKind.String
                ? tipsValue.GetString() ?? "" : "";
            return new ActivationSnapshot(authorized, time, tips);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WebSocketException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
