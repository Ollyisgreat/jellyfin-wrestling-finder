using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WrestlingFinder.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the folder downloads are saved into. Add this folder as a Jellyfin library.
    /// </summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to yt-dlp. Leave empty to use yt-dlp from PATH, or auto-download it.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to ffmpeg. Leave empty to find Jellyfin's own ffmpeg automatically.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether yt-dlp (and ffmpeg) may be downloaded automatically if it is missing.
    /// </summary>
    public bool AutoInstallYtDlp { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum video height to download (e.g. 1080).
    /// </summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the minimum length (minutes) for a result to count as a full event. Shorter videos (highlights, previews) are hidden by default.
    /// </summary>
    public int MinDurationMinutes { get; set; } = 50;

    /// <summary>
    /// Gets or sets the number of results to fetch per source.
    /// </summary>
    public int ResultsPerSource { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether YouTube is searched.
    /// </summary>
    public bool SearchYouTube { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Internet Archive is searched.
    /// </summary>
    public bool SearchInternetArchive { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Dailymotion is searched.
    /// </summary>
    public bool SearchDailymotion { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a library scan runs after each download.
    /// </summary>
    public bool ScanLibraryAfterDownload { get; set; } = true;
}
