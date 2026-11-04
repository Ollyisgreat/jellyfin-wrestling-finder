using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Finds (or installs) yt-dlp and runs it.
/// </summary>
public class YtDlpLocator
{
    private readonly ILogger<YtDlpLocator> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private string? _resolved;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpLocator"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder (used to find ffmpeg).</param>
    public YtDlpLocator(ILogger<YtDlpLocator> logger, IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets the path to Jellyfin's ffmpeg, if known.
    /// </summary>
    public string? FfmpegPath => string.IsNullOrEmpty(_mediaEncoder.EncoderPath) ? null : _mediaEncoder.EncoderPath;

    /// <summary>
    /// Returns a working yt-dlp path, installing it into the plugin folder if needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to yt-dlp.</returns>
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!string.IsNullOrWhiteSpace(config.YtDlpPath))
        {
            return config.YtDlpPath;
        }

        if (_resolved != null)
        {
            return _resolved;
        }

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolved != null)
            {
                return _resolved;
            }

            if (await WorksAsync("yt-dlp", cancellationToken).ConfigureAwait(false))
            {
                return _resolved = "yt-dlp";
            }

            var local = Path.Combine(Plugin.Instance.DataFolderPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp");
            if (!File.Exists(local))
            {
                if (!config.AutoInstallYtDlp)
                {
                    throw new InvalidOperationException("yt-dlp was not found. Install it on the server or set its path in the plugin settings.");
                }

                await DownloadAsync(local, cancellationToken).ConfigureAwait(false);
            }

            return _resolved = local;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Updates the auto-installed yt-dlp (sites change often, so old versions break).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>yt-dlp output.</returns>
    public async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        var path = await GetAsync(cancellationToken).ConfigureAwait(false);
        var (code, output) = await RunAsync(path, new[] { "-U" }, null, cancellationToken).ConfigureAwait(false);
        return $"exit {code}: {output}";
    }

    /// <summary>
    /// Runs yt-dlp (or another program) and captures output.
    /// </summary>
    /// <param name="exe">Executable.</param>
    /// <param name="args">Arguments.</param>
    /// <param name="onLine">Optional callback for each output line.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code and full stdout.</returns>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var output = stdout.ToString();
        if (process.ExitCode != 0)
        {
            output += stderr.ToString();
        }

        return (process.ExitCode, output);
    }

    private static async Task<bool> WorksAsync(string exe, CancellationToken cancellationToken)
    {
        try
        {
            var (code, _) = await RunAsync(exe, new[] { "--version" }, null, cancellationToken).ConfigureAwait(false);
            return code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task DownloadAsync(string target, CancellationToken cancellationToken)
    {
        string asset;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            asset = "yt-dlp.exe";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            asset = "yt-dlp_macos";
        }
        else
        {
            asset = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux"
            };
        }

        var url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + asset;
        _logger.LogInformation("Downloading yt-dlp from {Url} to {Target}", url, target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var tmp = target + ".part";
        using (var response = await Http.Client.GetAsync(url, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tmp, target, true);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                         | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                         | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
