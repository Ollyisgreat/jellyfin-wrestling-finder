using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Runs yt-dlp downloads in the background and tracks their progress.
/// </summary>
public class DownloadManager
{
    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ILogger<DownloadManager> _logger;
    private readonly YtDlpLocator _ytDlp;
    private readonly ILibraryManager _libraryManager;
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="ytDlp">yt-dlp locator.</param>
    /// <param name="libraryManager">Library manager.</param>
    public DownloadManager(ILogger<DownloadManager> logger, YtDlpLocator ytDlp, ILibraryManager libraryManager)
    {
        _logger = logger;
        _ytDlp = ytDlp;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets all jobs, newest first.
    /// </summary>
    /// <returns>Jobs.</returns>
    public IEnumerable<DownloadJob> GetJobs() => _jobs.Values.OrderByDescending(j => j.Started);

    /// <summary>
    /// Removes finished jobs from the list.
    /// </summary>
    public void ClearFinished()
    {
        foreach (var j in _jobs.Values.Where(j => j.Status is "Completed" or "Failed"))
        {
            _jobs.TryRemove(j.Id, out _);
        }
    }

    /// <summary>
    /// Queues a download.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <returns>The new job.</returns>
    public DownloadJob Start(DownloadRequest request)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.DownloadPath))
        {
            throw new InvalidOperationException("Set a download folder in the Wrestling Finder settings first.");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new ArgumentException("Invalid video URL.");
        }

        var name = SafeName(string.IsNullOrWhiteSpace(request.EventName) ? "Wrestling Event" : request.EventName);
        var job = new DownloadJob
        {
            EventName = name,
            Url = request.Url,
            OutputFolder = Path.Combine(config.DownloadPath, name)
        };
        _jobs[job.Id] = job;

        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    private async Task RunAsync(DownloadJob job)
    {
        await _concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance!.Configuration;
            job.Status = "Downloading";
            job.Message = "Starting…";
            Directory.CreateDirectory(job.OutputFolder);

            var exe = await _ytDlp.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var h = config.MaxHeight > 0 ? config.MaxHeight : 1080;
            var args = new List<string>
            {
                "--newline",
                "--no-playlist",
                "--no-part",
                "-f", $"bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b",
                "--merge-output-format", "mkv",
                "--write-thumbnail", "--convert-thumbnails", "jpg",
                "-o", Path.Combine(job.OutputFolder, job.EventName + ".%(ext)s"),
                "-o", "thumbnail:" + Path.Combine(job.OutputFolder, "poster.%(ext)s"),
            };

            // Internet Archive items can hold several files; take the first video only.
            if (job.Url.Contains("archive.org", StringComparison.OrdinalIgnoreCase))
            {
                args.Remove("--no-playlist");
                args.AddRange(new[] { "--playlist-items", "1" });
            }

            if (_ytDlp.FfmpegPath is { } ffmpeg)
            {
                args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
            }

            args.Add(job.Url);

            var (code, output) = await YtDlpLocator.RunAsync(exe, args, line =>
            {
                var m = ProgressRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    job.Progress = p;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    job.Message = line.Length > 300 ? line[..300] : line;
                }
            }, CancellationToken.None).ConfigureAwait(false);

            if (code != 0)
            {
                job.Status = "Failed";
                var err = output.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
                job.Message = err ?? $"yt-dlp exited with code {code}";
                _logger.LogError("Download failed for {Url}: {Output}", job.Url, output);
                return;
            }

            job.Progress = 100;
            job.Status = "Completed";
            job.Message = "Saved to " + job.OutputFolder;

            if (config.ScanLibraryAfterDownload)
            {
                _libraryManager.QueueLibraryScan();
            }
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.Message = ex.Message;
            _logger.LogError(ex, "Download failed for {Url}", job.Url);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' }).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.');
        return clean.Length == 0 ? "Wrestling Event" : clean;
    }
}
