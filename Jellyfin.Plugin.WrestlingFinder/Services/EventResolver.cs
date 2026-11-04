using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WrestlingFinder.Services;

/// <summary>
/// Turns whatever the user typed ("elim chamber 25", "wm 41") into the proper event name using Wikipedia.
/// </summary>
public class EventResolver
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wm"] = "WrestleMania",
        ["mania"] = "WrestleMania",
        ["rr"] = "Royal Rumble",
        ["rumble"] = "Royal Rumble",
        ["ss"] = "SummerSlam",
        ["sss"] = "Survivor Series",
        ["mitb"] = "Money in the Bank",
        ["ec"] = "Elimination Chamber",
        ["elim"] = "Elimination Chamber",
        ["hiac"] = "Hell in a Cell",
        ["tlc"] = "TLC: Tables, Ladders & Chairs",
        ["takeover"] = "NXT TakeOver",
        ["aew"] = "AEW",
        ["don"] = "Double or Nothing",
        ["fyter fest"] = "Fyter Fest",
        ["wk"] = "Wrestle Kingdom",
        ["bfg"] = "Bound for Glory",
        ["roh"] = "Ring of Honor",
        ["ppv"] = string.Empty
    };

    private readonly ILogger<EventResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public EventResolver(ILogger<EventResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds likely event names for a query.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate events, best first. Always includes the user's own text as a fallback.</returns>
    public async Task<List<EventCandidate>> ResolveAsync(string query, CancellationToken cancellationToken)
    {
        var expanded = ExpandQuery(query);
        var results = new List<EventCandidate>();

        try
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=10&srprop=snippet&srsearch="
                      + Uri.EscapeDataString(expanded + " professional wrestling event");
            using var stream = await Http.Client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var year = Regex.Match(expanded, @"\b(19|20)\d{2}\b").Value;

            foreach (var item in doc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? string.Empty;
                var snippet = StripHtml(item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty);

                // Skip list/overview pages and anything that clearly isn't an event.
                if (title.StartsWith("List of", StringComparison.OrdinalIgnoreCase)
                    || !snippet.Contains("wrestling", StringComparison.OrdinalIgnoreCase)
                    && !title.Contains('(') && !Regex.IsMatch(title, @"\d"))
                {
                    continue;
                }

                var candidate = new EventCandidate
                {
                    WikiTitle = title,
                    Name = CleanTitle(title),
                    Description = snippet,
                    Url = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'))
                };

                // If the user gave a year, put matching-year events first.
                if (!string.IsNullOrEmpty(year) && candidate.Name.Contains(year, StringComparison.Ordinal))
                {
                    results.Insert(results.Count(r => r.Name.Contains(year, StringComparison.Ordinal)), candidate);
                }
                else
                {
                    results.Add(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Wikipedia lookup failed for {Query}", query);
        }

        results = results.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(8).ToList();

        // Always let the user use exactly what they typed.
        var own = ToTitleCase(expanded);
        if (!results.Any(r => r.Name.Equals(own, StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(new EventCandidate { Name = own, Description = "Use exactly what you typed" });
        }

        return results;
    }

    /// <summary>
    /// Converts "Elimination Chamber (2025)" into "Elimination Chamber 2025".
    /// </summary>
    /// <param name="title">Wikipedia title.</param>
    /// <returns>Clean name.</returns>
    internal static string CleanTitle(string title)
    {
        var t = Regex.Replace(title, @"\s*\((\d{4})\)\s*$", " $1");
        t = Regex.Replace(t, @"\s*\((?:professional wrestling|wrestling|event|WWE event|AEW event)\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static string ExpandQuery(string query)
    {
        var q = query.Trim();

        // Two-digit years: "chamber 25" -> "chamber 2025" (but leave "WrestleMania 41" alone).
        var words = Regex.Split(q, @"\s+").ToList();
        for (var i = 0; i < words.Count; i++)
        {
            if (Aliases.TryGetValue(words[i], out var full))
            {
                words[i] = full;
            }
        }

        q = string.Join(' ', words.Where(w => w.Length > 0));
        if (!q.Contains("WrestleMania", StringComparison.OrdinalIgnoreCase))
        {
            q = Regex.Replace(q, @"(?<=\s)'?(\d{2})$", m => "20" + m.Groups[1].Value);
        }

        return q;
    }

    private static string StripHtml(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", string.Empty));

    private static string ToTitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Any(char.IsUpper) ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
