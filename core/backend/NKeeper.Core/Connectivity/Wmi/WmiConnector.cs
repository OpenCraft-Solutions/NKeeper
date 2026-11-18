using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Connectivity.Rpc;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Connectivity.Wmi;

/// <summary>
/// WMI connections for plugins (docs/architecture.md §7.28, SDK 1.5): one Impacket helper process per connection,
/// <c>wmi_helper.py</c>, signed in once over its standard input, then answering one query per line.
/// </summary>
public sealed class WmiConnector(CredentialBroker broker, CoreDbContext db, IHttpContextAccessor http, IAuditLog audit, IConfiguration configuration)
{
    public const string DefaultPython = "/usr/bin/python3";
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The most a query's answer may be, as a line of JSON.</summary>
    public const int MaxAnswerChars = 16 * 1024 * 1024;

    public async Task<IWmiConnection> OpenAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken)
    {
        var target = await WindowsTarget.ResolveAsync(host, serviceId, handle, "WMI", broker, db, http, cancellationToken);
        var start = new ProcessStartInfo(configuration["Connectivity:Wmi:Python"] is { Length: > 0 } python ? python : DefaultPython)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(configuration["Connectivity:Wmi:Helper"] is { Length: > 0 } helper ? helper : Path.Combine(AppContext.BaseDirectory, "Connectivity", "wmi_helper.py"));

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            process.Dispose();
            throw new ConnectException(ConnectFailure.Unreachable, "WMI needs Python with Impacket on the NKeeper server (the python3-impacket package).");
        }

        var connection = new WmiConnection(process);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SignInTimeout);
            JsonElement answer;
            try
            {
                answer = await connection.ExchangeAsync(new { host = target.Address, username = target.Username, password = target.Password, domain = target.Domain }, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.TimedOut, $"{target.Address} didn't answer over RPC in time.");
            }

            if (Error(answer) is var (kind, message))
            {
                throw new ConnectException(kind == "auth" ? ConnectFailure.AuthenticationFailed : ConnectFailure.Unreachable, message);
            }

            await audit.RecordAsync(new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"wmi to service {serviceId}, plugin {target.PluginId}, {target.CredentialDescription}"), cancellationToken);
            connection.StartIdleTimer();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static (string Kind, string Message)? Error(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("error", out var error)
            ? (answer.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "error" : "error", error.GetString() ?? "WMI failed.")
            : null;

    private sealed class WmiConnection : IWmiConnection
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Task _drainErrors;
        private readonly char[] _buffer = new char[64 * 1024];
        private int _start;
        private int _end;
        private Timer? _idle;
        private bool _dead;

        public WmiConnection(Process process)
        {
            _process = process;
            // The helper's own errors aren't shown (they could repeat a query); reading them keeps it from blocking.
            _drainErrors = process.StandardError.ReadToEndAsync();
        }

        public void StartIdleTimer() => _idle = new Timer(_ => Kill(), null, IdleTimeout, Timeout.InfiniteTimeSpan);

        public async Task<IReadOnlyList<JsonElement>> QueryAsync(WmiQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (string.IsNullOrWhiteSpace(query.Wql) || query.Limit is < 1)
            {
                throw new ArgumentException("A query needs its WQL, and a limit of at least 1.", nameof(query));
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_dead)
                {
                    throw new ConnectException(ConnectFailure.Unreachable, "The WMI connection was closed. Open it again.");
                }

                _idle?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(QueryTimeout);
                JsonElement answer;
                try
                {
                    answer = await ExchangeAsync(
                        new
                        {
                            @namespace = query.Namespace,
                            wql = query.Wql,
                            limit = Math.Min(query.Limit ?? WmiQuery.MaxRows, WmiQuery.MaxRows),
                            timeProperty = query.TimeProperty,
                            since = query.Since?.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                        },
                        timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Kill();
                    throw new ConnectException(ConnectFailure.TimedOut, "The host didn't answer the WMI query in time.");
                }

                if (Error(answer) is var (kind, message))
                {
                    if (kind == "query")
                    {
                        throw new WmiException(message);
                    }

                    Kill();
                    throw new ConnectException(ConnectFailure.Unreachable, message);
                }

                return answer.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array ? [.. rows.EnumerateArray()] : [];
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Writes one line of JSON and reads the answer's, at most <see cref="MaxAnswerChars"/> long.</summary>
        public async Task<JsonElement> ExchangeAsync(object request, CancellationToken cancellationToken)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
                var line = await ReadLineAsync(_process.StandardOutput, cancellationToken);
                return JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException or EndOfStreamException or InvalidOperationException)
            {
                Kill();
                throw new ConnectException(ConnectFailure.Unreachable, "The WMI helper stopped. Check that Impacket for Python is installed on the NKeeper server.");
            }
        }

        private async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                if (newline >= 0)
                {
                    line.Append(_buffer, _start, newline - _start);
                    _start = newline + 1;
                    return line.ToString();
                }

                line.Append(_buffer, _start, _end - _start);
                if (line.Length > MaxAnswerChars)
                {
                    throw new IOException("The WMI answer is too large.");
                }

                _start = 0;
                _end = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                if (_end == 0)
                {
                    throw new EndOfStreamException();
                }
            }
        }

        private void Kill()
        {
            _dead = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // It had already exited.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_idle is not null)
            {
                await _idle.DisposeAsync();
            }

            try
            {
                // Closing its input signs the helper out; it's killed if it doesn't go.
                _process.StandardInput.Close();
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(wait.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                Kill();
            }

            await _drainErrors.ContinueWith(_ => { }, TaskScheduler.Default);
            _process.Dispose();
            _lock.Dispose();
        }
    }
}
