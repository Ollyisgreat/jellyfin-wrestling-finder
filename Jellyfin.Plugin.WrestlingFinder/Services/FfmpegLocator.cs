using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Finds an ffmpeg that yt-dlp can use (needed to join video + audio), downloading one if necessary.
/// </summary>
public class FfmpegLocator
{
    private readonly ILogger<FfmpegLocator> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _resolved;
    private bool _downloadFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegLocator"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder.</param>
    public FfmpegLocator(ILogger<FfmpegLocator> logger, IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string ExeName => IsWindows ? "ffmpeg.exe" : "ffmpeg";

    private static string LocalDir => Path.Combine(Plugin.Instance!.DataFolderPath, "ffmpeg");

    /// <summary>
    /// Returns a usable ffmpeg path, or null if none could be found or installed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to ffmpeg, or null.</returns>
    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (_resolved != null && File.Exists(_resolved))
        {
            return _resolved;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var candidate in Candidates())
            {
                if (await WorksAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Using ffmpeg at {Path}", candidate);
                    return _resolved = candidate;
                }
            }

            // ffmpeg on PATH?
            if (await WorksAsync(ExeName, cancellationToken).ConfigureAwait(false))
            {
                return _resolved = ExeName;
            }

            if (!_downloadFailed && Plugin.Instance!.Configuration.AutoInstallYtDlp)
            {
                try
                {
                    var path = await DownloadAsync(cancellationToken).ConfigureAwait(false);
                    if (path != null && await WorksAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        return _resolved = path;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not download ffmpeg");
                }

                _downloadFailed = true;
            }

            _logger.LogWarning("No ffmpeg found; downloads will use single-file formats only");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private IEnumerable<string> Candidates()
    {
        var configured = Plugin.Instance!.Configuration.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim();
        }

        string? encoder = null;
        try
        {
            encoder = _mediaEncoder.EncoderPath;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
        }

        if (!string.IsNullOrWhiteSpace(encoder))
        {
            yield return encoder;
        }

        // Where Jellyfin's bundled ffmpeg lives on the common installs.
        yield return "/usr/lib/jellyfin-ffmpeg/ffmpeg";             // Debian/Ubuntu, official Docker image
        yield return "/usr/share/jellyfin-ffmpeg/ffmpeg";
        yield return "/usr/lib/jellyfin-ffmpeg/bin/ffmpeg";
        yield return "/opt/jellyfin-ffmpeg/ffmpeg";
        yield return "/Applications/Jellyfin.app/Contents/MacOS/ffmpeg"; // macOS app
        yield return "/opt/homebrew/bin/ffmpeg";
        yield return "/usr/local/bin/ffmpeg";
        yield return "/usr/bin/ffmpeg";
        yield return "/var/packages/jellyfin/target/ffmpeg/bin/ffmpeg"; // Synology
        yield return "/var/packages/ffmpeg7/target/bin/ffmpeg";
        yield return Path.Combine(AppContext.BaseDirectory, ExeName);  // Windows installer puts it next to jellyfin.exe
        yield return Path.Combine(LocalDir, ExeName);                   // our own download
        yield return Path.Combine(LocalDir, "bin", ExeName);
    }

    private static async Task<bool> WorksAsync(string path, CancellationToken cancellationToken)
    {
        if (path.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) && !File.Exists(path))
        {
            return false;
        }

        try
        {
            var (code, _) = await YtDlpLocator.RunAsync(path, new[] { "-version" }, null, cancellationToken).ConfigureAwait(false);
            return code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> DownloadAsync(CancellationToken cancellationToken)
    {
        string? asset = null;
        if (IsWindows)
        {
            asset = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            asset = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ffmpeg-master-latest-linuxarm64-gpl.tar.xz" : "ffmpeg-master-latest-linux64-gpl.tar.xz";
        }

        if (asset == null)
        {
            return null; // macOS etc: no prebuilt download
        }

        var url = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/" + asset;
        _logger.LogInformation("Downloading ffmpeg from {Url}", url);

        var dir = LocalDir;
        var tmpDir = dir + ".tmp";
        if (Directory.Exists(tmpDir))
        {
            Directory.Delete(tmpDir, true);
        }

        Directory.CreateDirectory(tmpDir);
        var archive = Path.Combine(tmpDir, asset);

        using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyfinWrestlingFinder/1.0");
            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(archive);
            await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        var extractDir = Path.Combine(tmpDir, "x");
        Directory.CreateDirectory(extractDir);
        if (asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            ZipFile.ExtractToDirectory(archive, extractDir);
        }
        else
        {
            var (code, output) = await YtDlpLocator.RunAsync("tar", new[] { "-xJf", archive, "-C", extractDir }, null, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                throw new IOException("Extracting ffmpeg failed: " + output);
            }
        }

        // The archive contains <name>/bin/ffmpeg and ffprobe; copy the bin folder into place.
        var ffmpeg = Directory.EnumerateFiles(extractDir, ExeName, SearchOption.AllDirectories).FirstOrDefault();
        if (ffmpeg == null)
        {
            throw new FileNotFoundException("ffmpeg not found in downloaded archive");
        }

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        var binDir = Path.GetDirectoryName(ffmpeg)!;
        foreach (var f in Directory.EnumerateFiles(binDir))
        {
            var target = Path.Combine(dir, Path.GetFileName(f));
            File.Copy(f, target, true);
            if (!IsWindows)
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        Directory.Delete(tmpDir, true);
        return Path.Combine(dir, ExeName);
    }
}
