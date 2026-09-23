using System.Diagnostics;
using System.Text;

namespace PhotoTag.Core;

public sealed class ExifToolException(string message) : Exception(message);

/// <summary>
/// A long-running ExifTool process driven through its <c>-stay_open</c> protocol, so each
/// command costs milliseconds rather than a fresh process start (which is slow on Windows).
/// Commands are serialized; the process is started on first use and restarted if it dies.
/// </summary>
public sealed class ExifTool : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _commandId;
    private bool _disposed;

    public ExifTool(string executablePath) => ExecutablePath = executablePath;

    public string ExecutablePath { get; }

    /// <summary>
    /// Finds ExifTool: the PHOTOTAG_EXIFTOOL environment variable, then a copy bundled with the
    /// app (in <paramref name="appDirectory"/> or its <c>exiftool</c> subfolder), then the PATH.
    /// </summary>
    public static string? Locate(string? appDirectory = null)
    {
        if (Environment.GetEnvironmentVariable("PHOTOTAG_EXIFTOOL") is { Length: > 0 } configured && File.Exists(configured))
            return configured;

        string[] names = OperatingSystem.IsWindows() ? ["exiftool.exe"] : ["exiftool"];

        var bundledDirs = appDirectory is null ? [] : new[] { Path.Combine(appDirectory, "exiftool"), appDirectory };
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return bundledDirs.Concat(pathDirs)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Runs one command. Returns ExifTool's stdout; throws <see cref="ExifToolException"/> on errors.</summary>
    public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        foreach (var argument in arguments)
        {
            if (argument.Contains('\n') || argument.Contains('\r'))
                throw new ArgumentException("ExifTool arguments can't contain line breaks; escape them with -E.", nameof(arguments));
        }

        // Cancellation is only honoured while waiting our turn: once a command is sent,
        // it must be read to completion or the protocol falls out of step.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var process = EnsureStarted();
            var id = ++_commandId;
            var marker = $"{{ready{id}}}";

            var command = new StringBuilder();
            foreach (var argument in arguments) command.Append(argument).Append('\n');
            command.Append("-echo4\n").Append(marker).Append('\n');
            command.Append("-execute").Append(id).Append('\n');

            await process.StandardInput.WriteAsync(command.ToString()).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            var output = ReadUntilAsync(process.StandardOutput, marker);
            var errors = ReadUntilAsync(process.StandardError, marker);
            await Task.WhenAll(output, errors).ConfigureAwait(false);

            var errorLines = errors.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (errorLines.Count > 0) throw new ExifToolException(string.Join(Environment.NewLine, errorLines));

            return output.Result;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            KillProcess();
            throw new ExifToolException($"ExifTool stopped unexpectedly: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false }) return _process;
        KillProcess();

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-stay_open", "True", "-@", "-" }) startInfo.ArgumentList.Add(argument);

        _process = Process.Start(startInfo) ?? throw new ExifToolException($"Couldn't start {ExecutablePath}");
        return _process;
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, string marker)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false)
                       ?? throw new IOException("ExifTool closed its output.");
            if (line.Trim() == marker) return text.ToString();
            text.AppendLine(line);
        }
    }

    private void KillProcess()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;

            if (_process is { HasExited: false } process)
            {
                try
                {
                    await process.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                }
            }
            KillProcess();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
