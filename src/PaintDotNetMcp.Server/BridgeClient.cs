using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PaintDotNetMcp.Contracts;
using ModelContextProtocol;

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
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await DisposePipeAsync();
            // MCP intentionally hides ordinary exception messages. Explicit operational errors
            // must use McpException so the client can see the recovery instructions.
            throw new McpException(ex.Message, ex);
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

        var pipe = new NamedPipeClientStream(".", PipeNames.Current, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout: 3000, ct);
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            // Check the deployed plugin before sending any operation that could mutate the canvas.
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            int id = Interlocked.Increment(ref _nextId);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method = "ping" }).AsMemory(), handshake.Token);
            var line = await _reader.ReadLineAsync(handshake.Token) ?? throw new IOException("Bridge closed connection during version check. Restart Paint.NET and reconnect.");
            var response = JsonSerializer.Deserialize<RpcResponse>(line);
            var version = response?.Result?.Deserialize<PingResult>()?.Version;
            if (response?.Ok != true || version != PipeNames.BridgeVersion)
                throw new InvalidOperationException($"bridge_version_mismatch: Server expects {PipeNames.BridgeVersion}, Bridge reports {version ?? "unknown"}. Close Paint.NET, run install.ps1, reopen Paint.NET, and reconnect the MCP client.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new IOException("bridge_not_responding: Wait for Paint.NET startup or a modal dialog/effect to finish, then retry. If this persists, restart Paint.NET and reconnect the MCP client.", ex);
        }
        catch (TimeoutException ex)
        {
            pipe.Dispose();
            var processes = Process.GetProcessesByName("paintdotnet");
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            throw new IOException(running
                ? "bridge_unavailable: Paint.NET is running but the Bridge did not connect. Wait for startup and retry. If this persists, run Effects > Tools > MCP Bridge. If the menu is missing, close Paint.NET, run install.ps1, and check Paint.NET plugin errors."
                : "paintdotnet_not_running: Open Paint.NET and an image or canvas, then retry. No Tools menu invocation is normally required.", ex);
        }
        catch { pipe.Dispose(); throw; }
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
