using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace deepseek_copilot;

public sealed class ExternalInvokeService : IDisposable
{
    public const string PipeName = "DeepSeekCopilot-Ask";

    private readonly WindowManager _windowManager;
    private CancellationTokenSource? _cts;

    public ExternalInvokeService(WindowManager windowManager)
    {
        _windowManager = windowManager;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ListenAsync(token), token);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);

                var message = await ReadMessageAsync(server, token);
                if (string.IsNullOrEmpty(message))
                {
                    await WriteMessageAsync(server, "EMPTY");
                    continue;
                }

                var question = ParseMessage(message);
                if (string.IsNullOrEmpty(question))
                {
                    await WriteMessageAsync(server, "EMPTY");
                    continue;
                }

                var dispatched = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => _windowManager.AskAsync(question));
                var succeeded = await dispatched;

                await WriteMessageAsync(server, succeeded ? "OK" : "FAILED");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // transient failures are retried by looping
            }
        }
    }

    private static async Task<string?> ReadMessageAsync(PipeStream stream, CancellationToken token)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1024];
        while (!token.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer, token);
            if (n <= 0) return sb.Length > 0 ? sb.ToString() : null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            var idx = sb.ToString().IndexOf('\n');
            if (idx >= 0) return sb.ToString(0, idx);
        }
        return null;
    }

    private static async Task WriteMessageAsync(PipeStream stream, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        await stream.WriteAsync(bytes, CancellationToken.None);
    }

    public static async Task<bool> SendQuestionAsync(string question)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            using var cts = new CancellationTokenSource(2500);
            await client.ConnectAsync(cts.Token);

            var bytes = Encoding.UTF8.GetBytes(question + "\n");
            await client.WriteAsync(bytes, CancellationToken.None);
            await client.FlushAsync();

            var sb = new StringBuilder();
            var buffer = new byte[256];
            using var readCts = new CancellationTokenSource(10000);
            while (true)
            {
                var n = await client.ReadAsync(buffer, readCts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            return sb.ToString().Trim() == "OK";
        }
        catch
        {
            return false;
        }
    }

    private static string? ParseMessage(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var key in new[] { "question", "prompt", "text", "message" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch
            {
                // fall through to raw text
            }
        }
        return trimmed;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
