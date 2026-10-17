using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Sprinkler.Core;

namespace Sprinkler;

/// <summary>
/// How a second copy of the app talks to the first: a local pipe named after the data folder, open to the current user
/// only. It carries two messages, "activate" (bring the window forward) and "nxm &lt;link&gt;" (a Nexus download link).
/// </summary>
internal sealed class InstanceChannel : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public event Action<string>? MessageReceived;

    private static string PipeName
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppEnvironment.DataDir));
            return "Sprinkler-" + Convert.ToHexString(hash)[..16];
        }
    }

    public void Start() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (!string.IsNullOrWhiteSpace(line))
                    MessageReceived?.Invoke(line.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Instance channel: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Sends a message to the running copy. False when nobody answers.</summary>
    public static async Task<bool> SendAsync(string message, int timeoutMs = 1500)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeoutMs);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(message);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => _stop.Cancel();
}
