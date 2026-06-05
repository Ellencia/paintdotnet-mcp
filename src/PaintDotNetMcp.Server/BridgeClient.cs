using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Server;

// Thin Named Pipe client. One in-flight request at a time (serialized via SemaphoreSlim).
// Reconnects lazily on each call so the user can start/stop Paint.NET freely.
public sealed class BridgeClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public async Task<JsonElement?> CallAsync(string method, object? @params, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            int id = Interlocked.Increment(ref _nextId);
            var req = new RpcRequest { Id = id, Method = method };
            var payload = JsonSerializer.Serialize(new
            {
                id,
                method,
                @params = @params,
            });
            await _writer!.WriteLineAsync(payload.AsMemory(), ct);
            var line = await _reader!.ReadLineAsync(ct)
                ?? throw new IOException("Bridge closed connection");
            var resp = JsonSerializer.Deserialize<RpcResponse>(line)
                ?? throw new InvalidOperationException("malformed response");
            if (!resp.Ok)
                throw new InvalidOperationException("Bridge error: " + (resp.Error ?? "unknown"));
            return resp.Result;
        }
        catch
        {
            // Drop connection so the next call reconnects.
            await DisposePipeAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;
        await DisposePipeAsync();

        var pipe = new NamedPipeClientStream(".", PipeNames.Default, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout: 3000, ct);
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    private Task DisposePipeAsync()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null; _reader = null; _pipe = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposePipeAsync();
        _gate.Dispose();
    }
}
