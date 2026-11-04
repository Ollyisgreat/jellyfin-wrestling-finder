using System;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// A candidate event name found for the user's query.
/// </summary>
public class EventCandidate
{
    /// <summary>Gets or sets the clean event name, e.g. "Elimination Chamber 2025".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikipedia page title.</summary>
    public string WikiTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets a short description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikipedia URL.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// A video found on one of the sources.
/// </summary>
public class VideoResult
{
    /// <summary>Gets or sets the source name (YouTube, Internet Archive, Dailymotion).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the direct link to the video page.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the uploader / channel.</summary>
    public string Uploader { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string Thumbnail { get; set; } = string.Empty;

    /// <summary>Gets or sets the duration in seconds (0 if unknown).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the match score; higher means a more likely full event.</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets a value indicating whether this looks like the full event (long enough, not highlights).</summary>
    public bool Viable { get; set; }

    /// <summary>Gets or sets the reason the result was judged not viable.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Request body for starting a download.
/// </summary>
public class DownloadRequest
{
    /// <summary>Gets or sets the video URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the event name used for the file and folder.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>Gets or sets the library folder to save into (empty = the saved default).</summary>
    public string Folder { get; set; } = string.Empty;
}

/// <summary>
/// A folder belonging to a Jellyfin library.
/// </summary>
public class LibraryFolder
{
    /// <summary>Gets or sets the library name.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder path on the server.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// A download job.
/// </summary>
public class DownloadJob
{
    /// <summary>Gets or sets the job id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the event name.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>Gets or sets the source URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the status: Queued, Downloading, Completed, Failed.</summary>
    public string Status { get; set; } = "Queued";

    /// <summary>Gets or sets progress (0-100).</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the last log line from yt-dlp.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the output folder.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Gets or sets when the job started.</summary>
    public DateTime Started { get; set; } = DateTime.UtcNow;
}
