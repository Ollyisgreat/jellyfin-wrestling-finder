using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.WrestlingFinder.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WrestlingFinder.Api;

/// <summary>
/// API used by the Wrestling Finder page. Admins only.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("WrestlingFinder")]
[Produces("application/json")]
public class WrestlingFinderController : ControllerBase
{
    private readonly EventResolver _resolver;
    private readonly VideoSearchService _search;
    private readonly DownloadManager _downloads;
    private readonly YtDlpLocator _ytDlp;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrestlingFinderController"/> class.
    /// </summary>
    /// <param name="resolver">Event resolver.</param>
    /// <param name="search">Video search.</param>
    /// <param name="downloads">Download manager.</param>
    /// <param name="ytDlp">yt-dlp locator.</param>
    public WrestlingFinderController(EventResolver resolver, VideoSearchService search, DownloadManager downloads, YtDlpLocator ytDlp)
    {
        _resolver = resolver;
        _search = search;
        _downloads = downloads;
        _ytDlp = ytDlp;
    }

    /// <summary>
    /// Works out the proper event name from what was typed.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate event names.</returns>
    [HttpGet("Resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<EventCandidate>>> Resolve([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("query is required");
        }

        return await _resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches the video sites for an event.
    /// </summary>
    /// <param name="eventName">Resolved event name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VideoResult>>> Search([FromQuery] string eventName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return BadRequest("eventName is required");
        }

        return await _search.SearchAsync(eventName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts downloading the chosen video.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <returns>The job.</returns>
    [HttpPost("Download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DownloadJob> Download([FromBody] DownloadRequest request)
    {
        try
        {
            return _downloads.Start(request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Lists the folders of all Jellyfin libraries, plus the last-used folder.
    /// </summary>
    /// <returns>Folders and the default.</returns>
    [HttpGet("Folders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Folders() =>
        Ok(new { Folders = _downloads.GetLibraryFolders(), Default = Plugin.Instance!.Configuration.DownloadPath });

    /// <summary>
    /// Lists download jobs.
    /// </summary>
    /// <returns>Jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<DownloadJob>> Jobs() => Ok(_downloads.GetJobs());

    /// <summary>
    /// Clears finished jobs.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearJobs()
    {
        _downloads.ClearFinished();
        return NoContent();
    }

    /// <summary>
    /// Updates yt-dlp to the latest version.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>yt-dlp output.</returns>
    [HttpPost("UpdateYtDlp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> UpdateYtDlp(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _ytDlp.UpdateAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(ex.Message);
        }
    }
}
