using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>One side of a fixture in the sports artwork catalogue.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Aliases">Normalized names the guide may use.</param>
public sealed record CrestTeam(string Name, IReadOnlyList<string> Aliases);

/// <summary>A fixture with both team crests, from the companion's sports artwork catalogue (events.json, version 2).</summary>
/// <param name="Start">Kickoff.</param>
/// <param name="Away">Away side.</param>
/// <param name="Home">Home side.</param>
/// <param name="Logos">Crest file names, away then home.</param>
public sealed record CrestFixture(DateTime Start, CrestTeam Away, CrestTeam Home, IReadOnlyList<string> Logos);

/// <summary>The two teams of a Home sports card, in the guide's order, with their crests.</summary>
/// <param name="Teams">Team display names.</param>
/// <param name="Crests">Server-relative crest image paths, same order.</param>
public sealed record CardCrests(IReadOnlyList<string> Teams, IReadOnlyList<string> Crests);

/// <summary>
/// Matches a guide programme to exactly one catalogue fixture, the same way the web's Live sports row does
/// (branding/zorofin-live-events.js artworkDetailsFor): both team names from the title must be aliases of the
/// fixture's two sides, and kickoff must be within three hours. A generic or ambiguous title gets nothing.
/// </summary>
public static class SportsCrests
{
    /// <summary>Where the plugin serves crest images (anonymous, immutable).</summary>
    public const string ArtRoute = "Zorofin/Web/ui/live-tv-logos/art/";

    private static readonly Regex FileName = new(@"^sports-[a-f0-9]{24}-[a-f0-9]{8}\.(?:png|jpg)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Versus = new(@"\s+(?:vs\.?|v\.?|versus|at)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LivePrefix = new(@"^live\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NotTeam = new(@"\b(?:[0-9]{1,2}(?::[0-9]{2})?\s*(?:am|pm)|afc (?:east|west|north|south)|nfc (?:east|west|north|south)|\d*news)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NonAlphanumeric = new("[^a-z0-9]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly TimeSpan KickoffSlack = TimeSpan.FromHours(3);

    /// <summary>Reads the catalogue. Anything malformed is skipped.</summary>
    /// <param name="json">events.json bytes.</param>
    /// <returns>Fixtures with two crests.</returns>
    public static IReadOnlyList<CrestFixture> Parse(byte[]? json)
    {
        if (json is null || json.Length == 0 || json.Length > 3_000_000)
        {
            return Array.Empty<CrestFixture>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 2 || !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CrestFixture>();
            }

            var list = new List<CrestFixture>();
            foreach (var e in events.EnumerateArray().Take(2000))
            {
                if (e.ValueKind != JsonValueKind.Object || Team(e, "away") is not { } away || Team(e, "home") is not { } home
                    || !e.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.String
                    || !DateTime.TryParse(start.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var kickoff)
                    || !e.TryGetProperty("logos", out var logos) || logos.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var files = logos.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? FileOf(l.GetString()) : null).ToList();
                if (files.Count == 2 && files.All(f => f is not null))
                {
                    list.Add(new CrestFixture(kickoff, away, home, files.Select(f => f!).ToArray()));
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Array.Empty<CrestFixture>();
        }
    }

    /// <summary>The two teams and crests for a guide programme, or null when no single fixture matches.</summary>
    /// <param name="programName">Guide title.</param>
    /// <param name="start">Programme start.</param>
    /// <param name="fixtures">Catalogue.</param>
    /// <returns>Teams and crests, or null.</returns>
    public static CardCrests? Match(string programName, DateTime start, IReadOnlyList<CrestFixture> fixtures)
    {
        if (fixtures.Count == 0 || Matchup(programName) is not { } pair)
        {
            return null;
        }

        static bool Is(string value, CrestTeam team)
        {
            var name = Normalize(value);
            name = name.StartsWith("the ", StringComparison.Ordinal) ? name[4..] : name;
            return team.Aliases.Contains(name);
        }

        var candidates = fixtures.Where(f => (f.Start - start).Duration() <= KickoffSlack
            && ((Is(pair[0], f.Away) && Is(pair[1], f.Home)) || (Is(pair[1], f.Away) && Is(pair[0], f.Home)))).Take(2).ToList();
        if (candidates.Count != 1)
        {
            return null;
        }

        var f = candidates[0];
        var reversed = Is(pair[0], f.Home) && Is(pair[1], f.Away);
        var teams = reversed ? new[] { f.Home.Name, f.Away.Name } : new[] { f.Away.Name, f.Home.Name };
        var logos = reversed ? new[] { f.Logos[1], f.Logos[0] } : new[] { f.Logos[0], f.Logos[1] };
        return new CardCrests(teams, logos.Select(l => ArtRoute + l).ToArray());
    }

    /// <summary>The two team names in a guide title ("Live: NBA — Wizards at Celtics"), or null.</summary>
    /// <param name="title">Guide title.</param>
    /// <returns>Two names or null.</returns>
    public static IReadOnlyList<string>? Matchup(string? title)
    {
        var parts = LivePrefix.Replace((title ?? string.Empty).Trim(), string.Empty).Split(" — ");
        return Pair(parts[^1]) ?? (parts.Length > 1 ? Pair(parts[0]) : null);
    }

    /// <summary>Folds a name the way the web does: accents off, lower case, letters and digits only.</summary>
    /// <param name="value">Text.</param>
    /// <returns>Normalized text.</returns>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Replace("ø", "o", StringComparison.Ordinal).Replace("Ø", "o", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal).Replace("Æ", "ae", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("ł", "l", StringComparison.Ordinal).Replace("Ł", "l", StringComparison.Ordinal).Normalize(NormalizationForm.FormKD);
        var plain = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(c);
            }
        }

        return NonAlphanumeric.Replace(plain.ToString().ToLowerInvariant(), " ").Trim();
    }

    private static IReadOnlyList<string>? Pair(string text)
    {
        var parts = Versus.Split(text);
        return parts.Length == 2 && parts.All(p => Normalize(p).Length > 2 && !NotTeam.IsMatch(p)) ? parts.Select(p => p.Trim()).ToArray() : null;
    }

    private static CrestTeam? Team(JsonElement fixture, string side)
    {
        if (!fixture.TryGetProperty(side, out var team) || team.ValueKind != JsonValueKind.Object
            || !team.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var aliases = team.TryGetProperty("aliases", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => Normalize(a.GetString())).Where(a => a.Length >= 3).Take(20).ToArray()
            : Array.Empty<string>();
        var display = (name.GetString() ?? string.Empty).Trim();
        return display.Length is > 0 and <= 80 && aliases.Length > 0 ? new CrestTeam(display, aliases) : null;
    }

    private static string? FileOf(string? path)
    {
        const string Prefix = "/live-tv-logos/art/";
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var file = path[Prefix.Length..];
        return FileName.IsMatch(file) ? file : null;
    }
}
