using System.Text.Json;

namespace C30SuperLogin.Core;

public sealed class DiagnosticQueue : IAsyncDisposable
{
    private readonly string directory;
    private readonly Telemetry telemetry;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public event Action<IReadOnlyList<Delivery>>? Delivered;
    public DiagnosticQueue(Store store, Telemetry telemetry)
    {
        this.telemetry = telemetry;
        directory = Path.Combine(store.Root, "pending-diagnostics"); Directory.CreateDirectory(directory);
        worker = Worker(); signal.Release();
    }
    public void Add(Diagnostic item)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*.json").OrderBy(File.GetCreationTimeUtc).ToArray();
            foreach (var file in files.Take(Math.Max(0, files.Length - 63))) File.Delete(file);
            // Diagnostic has only enum stages, a validated version in outbound payloads and numeric fields.
            File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), Json.Serialize(item));
            if (signal.CurrentCount == 0) signal.Release();
        }
        catch (IOException) { }
    }
    private async Task Worker()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await signal.WaitAsync(TimeSpan.FromMinutes(1), stop.Token);
                foreach (var file in Directory.GetFiles(directory, "*.json"))
                {
                    if (stop.IsCancellationRequested) break;
                    Diagnostic? item;
                    try { item = JsonSerializer.Deserialize<Diagnostic>(File.ReadAllText(file), Json.Options); }
                    catch (Exception e) when (e is IOException or JsonException) { continue; }
                    if (item is null) continue;
                    var delivery = await telemetry.PublishAsync(item, stop.Token);
                    Delivered?.Invoke(delivery);
                    // Retain unsent events when credentials arrive later; partial delivery is explicit.
                    if (delivery.Any(x => x.State == "sent")) File.Delete(file);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; } catch (OperationCanceledException) { }
        signal.Dispose(); stop.Dispose();
    }
}
