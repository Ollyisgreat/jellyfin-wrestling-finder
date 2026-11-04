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
    private readonly FfmpegLocator _ffmpeg;
    private readonly ILibraryManager _libraryManager;
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="ytDlp">yt-dlp locator.</param>
    /// <param name="ffmpeg">ffmpeg locator.</param>
    /// <param name="libraryManager">Library manager.</param>
    public DownloadManager(ILogger<DownloadManager> logger, YtDlpLocator ytDlp, FfmpegLocator ffmpeg, ILibraryManager libraryManager)
    {
        _logger = logger;
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;
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
    /// Gets every folder of every Jellyfin library.
    /// </summary>
    /// <returns>Library folders.</returns>
    public List<LibraryFolder> GetLibraryFolders() =>
        _libraryManager.GetVirtualFolders()
            .SelectMany(v => (v.Locations ?? Array.Empty<string>()).Select(l => new LibraryFolder { Library = v.Name, Path = l }))
            .OrderBy(f => f.Library, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Queues a download.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <returns>The new job.</returns>
    public DownloadJob Start(DownloadRequest request)
    {
        var config = Plugin.Instance!.Configuration;
        var folder = string.IsNullOrWhiteSpace(request.Folder) ? config.DownloadPath : request.Folder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("Choose a library folder to save into.");
        }

        if (!string.Equals(folder, config.DownloadPath, StringComparison.Ordinal)
            && !GetLibraryFolders().Any(f => string.Equals(f.Path, folder, StringComparison.Ordinal)))
        {
            throw new ArgumentException("That folder isn't one of your Jellyfin library folders.");
        }

        // Remember the choice so it's pre-selected next time.
        if (!string.Equals(folder, config.DownloadPath, StringComparison.Ordinal))
        {
            config.DownloadPath = folder;
            Plugin.Instance.SaveConfiguration();
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
            OutputFolder = Path.Combine(folder, name),
            Folder = folder
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
            job.Message = "Finding ffmpeg (first run may download it)…";
            var ffmpeg = await _ffmpeg.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var h = config.MaxHeight > 0 ? config.MaxHeight : 1080;
            var args = new List<string>
            {
                "--newline",
                "--no-part",
                "-o", Path.Combine(job.OutputFolder, job.EventName + ".%(ext)s"),
            };

            if (ffmpeg != null)
            {
                // A bare "ffmpeg" means it's on PATH, which yt-dlp finds by itself.
                if (ffmpeg.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || ffmpeg.Contains('/', StringComparison.Ordinal))
                {
                    args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
                }

                // Best quality: separate video + audio streams joined by ffmpeg.
                args.AddRange(new[]
                {
                    "-f", $"bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b",
                    "--merge-output-format", "mkv",
                    "--write-thumbnail", "--convert-thumbnails", "jpg",
                    "-o", "thumbnail:" + Path.Combine(job.OutputFolder, "poster.%(ext)s"),
                });
            }
            else
            {
                // No ffmpeg: only formats that are already a single file (may be lower quality).
                args.AddRange(new[]
                {
                    "-f", $"b[height<={h}][acodec!=none][vcodec!=none]/b[acodec!=none][vcodec!=none]/b",
                    "--write-thumbnail",
                    "-o", "thumbnail:" + Path.Combine(job.OutputFolder, "poster.%(ext)s"),
                });
            }

            // Internet Archive items can hold several files; take the first video only.
            if (job.Url.Contains("archive.org", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "--playlist-items", "1" });
            }
            else
            {
                args.Add("--no-playlist");
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
