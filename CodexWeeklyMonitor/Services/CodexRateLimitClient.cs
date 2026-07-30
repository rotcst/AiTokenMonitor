using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

public sealed class CodexRateLimitClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();

    private Process? _process;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private bool _disposed;

    public event EventHandler? RefreshSuggested;

    public string? ExecutablePath { get; private set; }

    public async Task<CodexUsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var rateLimitResult = await SendRequestAsync(
                    method: "account/rateLimits/read",
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var fetchedAt = DateTimeOffset.Now;
            var rateLimits = QuotaParser.ParseAccountRateLimits(rateLimitResult, fetchedAt);

            AccountTokenUsage? tokenUsage = null;
            string? tokenUsageError = null;
            try
            {
                var tokenUsageResult = await SendRequestAsync(
                        method: "account/usage/read",
                        parameters: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                tokenUsage = TokenUsageParser.Parse(tokenUsageResult, fetchedAt);
            }
            catch (Exception exception) when (
                exception is CodexProtocolException or InvalidDataException or TimeoutException)
            {
                // Account token history is optional on older servers and some plans.
                tokenUsageError = exception.Message;
            }

            return new CodexUsageSnapshot(
                RateLimits: rateLimits,
                TokenUsage: tokenUsage,
                TokenUsageError: tokenUsageError,
                FetchedAt: fetchedAt);
        }
        catch
        {
            await ResetConnectionAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _writer is not null)
        {
            return;
        }

        await ResetConnectionAsync().ConfigureAwait(false);

        ExecutablePath = CodexExecutableResolver.Resolve();
        DiagnosticsLog.Write("CodexRateLimitClient", $"starting app-server: {ExecutablePath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.Exited += Process_Exited;
        if (!process.Start())
        {
            process.Exited -= Process_Exited;
            process.Dispose();
            DiagnosticsLog.Write("CodexRateLimitClient", "failed to start app-server process");
            throw new InvalidOperationException(Loc.T("err.codex.startFailed"));
        }

        _process = process;
        _connectionCancellation = new CancellationTokenSource();
        _writer = process.StandardInput;
        _writer.AutoFlush = true;
        _writer.NewLine = "\n";

        _stdoutTask = ReadStdoutAsync(process.StandardOutput, _connectionCancellation.Token);
        _stderrTask = DrainStderrAsync(process.StandardError, _connectionCancellation.Token);

        var initializeParams = new
        {
            clientInfo = new
            {
                name = "ai_token_monitor",
                title = "AI TOKEN Usage Monitor",
                version = "1.9.8",
            },
            capabilities = new
            {
                optOutNotificationMethods = Array.Empty<string>(),
            },
        };

        await SendRequestAsync("initialize", initializeParams, cancellationToken).ConfigureAwait(false);
        await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        DiagnosticsLog.Write("CodexRateLimitClient", "app-server initialized");
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new IOException("Codex app-server 尚未连接。");
        var connectionToken = _connectionCancellation?.Token ?? CancellationToken.None;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connectionToken);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法登记 Codex 请求。");
        }

        var message = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["id"] = requestId,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await WriteMessageAsync(writer, message, linkedCancellation.Token).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(RequestTimeout, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new IOException("Codex app-server 尚未连接。");
        var message = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["params"] = parameters,
        };

        await WriteMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteMessageAsync(
        StreamWriter writer,
        object message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ProcessMessage(line);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                DiagnosticsLog.Write("CodexRateLimitClient", "app-server stdout ended");
                FailPending(new IOException("Codex app-server 已结束。"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Write(
                "CodexRateLimitClient",
                $"stdout read failed: {exception.GetType().Name}: {exception.Message}");
            FailPending(new IOException("读取 Codex app-server 响应失败。", exception));
        }
    }

    private void ProcessMessage(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (TryGetRequestId(root, out var requestId) &&
                _pending.TryRemove(requestId, out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;
                    DiagnosticsLog.Write(
                        "CodexRateLimitClient",
                        $"protocol error for request {requestId}: {message ?? "<no message>"}");
                    completion.TrySetException(new CodexProtocolException(
                        string.IsNullOrWhiteSpace(message) ? "Codex 请求失败。" : message));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new CodexProtocolException("Codex 响应缺少 result 字段。"));
                }

                return;
            }

            if (!root.TryGetProperty("method", out var methodElement))
            {
                return;
            }

            var method = methodElement.GetString();
            if (method is "account/rateLimits/updated" or "thread/tokenUsage/updated")
            {
                // Notifications can be sparse. Ask the UI to perform one complete read so
                // balances, both windows, and token history remain internally consistent.
                RefreshSuggested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static bool TryGetRequestId(JsonElement root, out long requestId)
    {
        requestId = 0;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("id", out var idElement) &&
               idElement.ValueKind == JsonValueKind.Number &&
               idElement.TryGetInt64(out requestId);
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        DiagnosticsLog.Write(
            "CodexRateLimitClient",
            $"app-server exited: pid={process.Id}, exitCode={(TryGetExitCode(process, out var exitCode) ? exitCode.ToString() : "<unknown>")}");
        FailPending(new IOException("Codex app-server 已退出。"));
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            exitCode = 0;
            return false;
        }
    }

    private static async Task DrainStderrAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DiagnosticsLog.Write("CodexRateLimitClient.stderr", line);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task ResetConnectionAsync()
    {
        var cancellation = _connectionCancellation;
        var writer = _writer;
        var process = _process;

        _connectionCancellation = null;
        _writer = null;
        _process = null;
        _stdoutTask = null;
        _stderrTask = null;

        cancellation?.Cancel();
        FailPending(new IOException("Codex app-server 连接已关闭。"));

        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // Process shutdown can close stdin first.
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // Best-effort cleanup of the child process created by this instance.
            }
            finally
            {
                process.Exited -= Process_Exited;
                process.Dispose();
            }
        }

        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private sealed class CodexProtocolException(string message) : Exception(message);
}
