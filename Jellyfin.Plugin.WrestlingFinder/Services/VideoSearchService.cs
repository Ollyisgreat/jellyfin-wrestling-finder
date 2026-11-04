using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Searches YouTube, the Internet Archive and Dailymotion for an event, and rates each result.
/// </summary>
public class VideoSearchService
{
    private static readonly string[] BadWords =
    {
        "highlight", "highlights", "preview", "predictions", "prediction", "reaction", "review", "recap",
        "results", "kickoff", "pre-show", "preshow", "press conference", "post-show", "postshow", "breakdown",
        "trailer", "promo", "2k", "w2k", "simulation", "fantasy", "live stream reaction", "watch along", "watchalong"
    };

    private static readonly string[] GoodWords = { "full show", "full event", "full ppv", "full pay-per-view", "full ple", "complete", "entire show" };

    private readonly ILogger<VideoSearchService> _logger;
    private readonly YtDlpLocator _ytDlp;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoSearchService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="ytDlp">yt-dlp locator.</param>
    public VideoSearchService(ILogger<VideoSearchService> logger, YtDlpLocator ytDlp)
    {
        _logger = logger;
        _ytDlp = ytDlp;
    }

    /// <summary>
    /// Searches all enabled sources.
    /// </summary>
    /// <param name="eventName">The resolved event name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All results, viable full-length ones first, then by score.</returns>
    public async Task<List<VideoResult>> SearchAsync(string eventName, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var n = Math.Clamp(config.ResultsPerSource, 1, 50);
        var tasks = new List<Task<List<VideoResult>>>();

        if (config.SearchYouTube)
        {
            tasks.Add(Safe("YouTube", () => SearchYouTubeAsync(eventName, n, cancellationToken)));
        }

        if (config.SearchInternetArchive)
        {
            tasks.Add(Safe("Internet Archive", () => SearchArchiveAsync(eventName, n, cancellationToken)));
        }

        if (config.SearchDailymotion)
        {
            tasks.Add(Safe("Dailymotion", () => SearchDailymotionAsync(eventName, n, cancellationToken)));
        }

        var all = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(x => x)
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        foreach (var r in all)
        {
            Rate(r, eventName, config.MinDurationMinutes);
        }

        return all.OrderByDescending(r => r.Viable).ThenByDescending(r => r.Score).ThenByDescending(r => r.DurationSeconds).ToList();
    }

    /// <summary>
    /// Scores a result and decides whether it looks like the full event.
    /// </summary>
    /// <param name="r">Result.</param>
    /// <param name="eventName">Event name.</param>
    /// <param name="minMinutes">Minimum length in minutes.</param>
    internal static void Rate(VideoResult r, string eventName, int minMinutes)
    {
        var title = r.Title.ToLowerInvariant();
        var tokens = Regex.Matches(eventName.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).Where(t => t.Length > 1).Distinct().ToList();
        var year = Regex.Match(eventName, @"\b(19|20)\d{2}\b").Value;

        var matched = tokens.Count == 0 ? 0 : tokens.Count(t => title.Contains(t, StringComparison.Ordinal));
        var score = tokens.Count == 0 ? 0 : (int)(50.0 * matched / tokens.Count);

        if (!string.IsNullOrEmpty(year))
        {
            if (title.Contains(year, StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (Regex.IsMatch(title, @"\b(19|20)\d{2}\b"))
            {
                score -= 30; // a different year's event
            }
        }

        if (GoodWords.Any(w => title.Contains(w, StringComparison.Ordinal)))
        {
            score += 20;
        }

        var bad = BadWords.FirstOrDefault(w => Regex.IsMatch(title, @"\b" + Regex.Escape(w) + @"\b"));
        if (bad != null)
        {
            score -= 25;
        }

        var minutes = r.DurationSeconds / 60.0;
        if (minutes >= 120)
        {
            score += 20;
        }
        else if (minutes >= minMinutes)
        {
            score += 10;
        }

        r.Score = score;

        var notes = new List<string>();
        if (r.DurationSeconds <= 0)
        {
            notes.Add("length unknown");
        }
        else if (minutes < minMinutes)
        {
            notes.Add($"only {Math.Round(minutes)} min");
        }

        if (bad != null)
        {
            notes.Add($"title says \"{bad}\"");
        }

        if (tokens.Count > 0 && matched * 2 < tokens.Count)
        {
            notes.Add("title doesn't match the event well");
        }

        // Unknown length is allowed through (Archive items sometimes lack it); short or highlight videos are not.
        r.Viable = (r.DurationSeconds <= 0 || minutes >= minMinutes) && bad == null && (tokens.Count == 0 || matched * 2 >= tokens.Count);
        r.Note = string.Join(", ", notes);
    }

    private async Task<List<VideoResult>> Safe(string source, Func<Task<List<VideoResult>>> search)
    {
        try
        {
            return await search().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Source} search failed", source);
            return new List<VideoResult>();
        }
    }

    private async Task<List<VideoResult>> SearchYouTubeAsync(string eventName, int n, CancellationToken ct)
    {
        var exe = await _ytDlp.GetAsync(ct).ConfigureAwait(false);
        var results = new List<VideoResult>();

        // Two searches: one aimed at full shows, one plain, so we don't miss oddly-titled uploads.
        foreach (var q in new[] { eventName + " full show", eventName })
        {
            var (_, output) = await YtDlpLocator.RunAsync(
                exe,
                new[] { "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors", $"ytsearch{n}:{q}" },
                null,
                ct).ConfigureAwait(false);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    var id = Str(e, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    results.Add(new VideoResult
                    {
                        Source = "YouTube",
                        Title = Str(e, "title"),
                        Url = "https://www.youtube.com/watch?v=" + id,
                        Uploader = Str(e, "channel") is { Length: > 0 } c ? c : Str(e, "uploader"),
                        DurationSeconds = Num(e, "duration"),
                        Thumbnail = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg"
                    });
                }
                catch (JsonException)
                {
                }
            }
        }

        return results;
    }

    private static async Task<List<VideoResult>> SearchArchiveAsync(string eventName, int n, CancellationToken ct)
    {
        var q = $"({eventName}) AND mediatype:(movies)";
        var url = "https://archive.org/advancedsearch.php?q=" + Uri.EscapeDataString(q)
                  + "&fl[]=identifier&fl[]=title&fl[]=creator&rows=" + n.ToString(CultureInfo.InvariantCulture) + "&output=json";

        using var stream = await Http.Client.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<VideoResult>();
        foreach (var d in doc.RootElement.GetProperty("response").GetProperty("docs").EnumerateArray())
        {
            var id = Str(d, "identifier");
            results.Add(new VideoResult
            {
                Source = "Internet Archive",
                Title = Str(d, "title"),
                Url = "https://archive.org/details/" + id,
                Uploader = Str(d, "creator"),
                Thumbnail = "https://archive.org/services/img/" + id
            });
        }

        // The search API doesn't give lengths, so look each item up (in parallel).
        await Task.WhenAll(results.Select(r => FillArchiveDurationAsync(r, ct))).ConfigureAwait(false);
        return results;
    }

    private static async Task FillArchiveDurationAsync(VideoResult r, CancellationToken ct)
    {
        try
        {
            var id = r.Url[(r.Url.LastIndexOf('/') + 1)..];
            using var stream = await Http.Client.GetStreamAsync("https://archive.org/metadata/" + id + "/files", ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            double longest = 0;
            foreach (var f in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                if (f.TryGetProperty("length", out var len))
                {
                    longest = Math.Max(longest, ParseLength(len.ValueKind == JsonValueKind.String ? len.GetString() : len.GetRawText()));
                }
            }

            r.DurationSeconds = longest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // leave duration unknown
        }
    }

    private static async Task<List<VideoResult>> SearchDailymotionAsync(string eventName, int n, CancellationToken ct)
    {
        var url = "https://api.dailymotion.com/videos?fields=id,title,duration,url,owner.screenname,thumbnail_240_url&limit="
                  + n.ToString(CultureInfo.InvariantCulture) + "&search=" + Uri.EscapeDataString(eventName);
        using var stream = await Http.Client.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<VideoResult>();
        foreach (var v in doc.RootElement.GetProperty("list").EnumerateArray())
        {
            results.Add(new VideoResult
            {
                Source = "Dailymotion",
                Title = Str(v, "title"),
                Url = Str(v, "url"),
                Uploader = Str(v, "owner.screenname"),
                DurationSeconds = Num(v, "duration"),
                Thumbnail = Str(v, "thumbnail_240_url")
            });
        }

        return results;
    }

    private static double ParseLength(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        s = s.Trim('"');
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            return secs;
        }

        // "01:23:45" or "23:45"
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return 0;
            }

            total = (total * 60) + v;
        }

        return total;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ToString())),
                JsonValueKind.Null => string.Empty,
                _ => v.ToString()
            }
            : string.Empty;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
