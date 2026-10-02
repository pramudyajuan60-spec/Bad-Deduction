using BadDeduction.AI;
using BadDeduction.Core;

namespace BadDeduction.Investigation;

/// <summary>A parsed whereabouts claim. <see cref="LocationId"/> is null while traveling.</summary>
public sealed record WhereaboutsClaim(
    string? LocationId,
    string? TravelFrom,
    string? TravelTo,
    long Minute,
    string Detail);

/// <summary>
/// Phase 8 tuning constants and pure helpers. All contradiction detection is deterministic
/// integer/string comparison — no RNG anywhere in the investigation subsystem.
/// </summary>
public static class InvestigationRules
{
    // ------------------------------------------------------------------ topic grammar

    public const string EventTopicPrefix = "event:";
    public const string PersonTopicPrefix = "person:";
    public const string WhereaboutsTopicPrefix = "whereabouts:";

    /// <summary>The canonical "I know nothing" answer. Compared by exact equality.</summary>
    public const string DontKnowClaim = "I don't know anything about that.";

    /// <summary>Structured claims are short; free-text answers are capped like dialogue replies.</summary>
    public const int MaxClaimLength = 500;

    public static string EventTopic(long eventId) => $"{EventTopicPrefix}{eventId}";
    public static string PersonTopic(string characterId) => $"{PersonTopicPrefix}{characterId}";
    public static string WhereaboutsTopic(long minute) => $"{WhereaboutsTopicPrefix}{minute}";

    public static bool IsValidTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        (topic.StartsWith(EventTopicPrefix, StringComparison.Ordinal) ||
         topic.StartsWith(PersonTopicPrefix, StringComparison.Ordinal) ||
         topic.StartsWith(WhereaboutsTopicPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Two topics can contradict each other when they are identical, or when both are
    /// whereabouts questions: "where were you at 600" and "where were you at 630" are
    /// about the same alibi even though the minutes differ.
    /// </summary>
    public static bool TopicsCompatible(string a, string b) =>
        a == b ||
        (a.StartsWith(WhereaboutsTopicPrefix, StringComparison.Ordinal) &&
         b.StartsWith(WhereaboutsTopicPrefix, StringComparison.Ordinal));

    // ------------------------------------------------------------------ whereabouts claim format

    /// <summary>Formats a whereabouts claim: "at:{location}@{minute}" with optional "#{detail}".</summary>
    public static string FormatWhereabouts(string locationId, long minute, string? detail = null) =>
        string.IsNullOrWhiteSpace(detail)
            ? $"at:{locationId}@{minute}"
            : $"at:{locationId}@{minute}#{detail}";

    /// <summary>Formats a traveling claim: "traveling:{from}&gt;{to}@{minute}".</summary>
    public static string FormatTraveling(string fromId, string toId, long minute) =>
        $"traveling:{fromId}>{toId}@{minute}";

    /// <summary>
    /// Parses a whereabouts claim. Returns false for free-text claims (other topics), which
    /// simply do not participate in the whereabouts contradiction rules.
    /// </summary>
    public static bool TryParseWhereabouts(string claim, out WhereaboutsClaim? parsed)
    {
        parsed = null;
        if (claim.StartsWith("at:", StringComparison.Ordinal))
        {
            var rest = claim.Substring(3);
            var atSplit = rest.IndexOf('@');
            if (atSplit < 0) return false;
            var locationId = rest.Substring(0, atSplit);
            var minuteAndDetail = rest.Substring(atSplit + 1);
            var hashSplit = minuteAndDetail.IndexOf('#');
            var minuteText = hashSplit < 0 ? minuteAndDetail : minuteAndDetail.Substring(0, hashSplit);
            var detail = hashSplit < 0 ? "" : minuteAndDetail.Substring(hashSplit + 1);
            if (string.IsNullOrWhiteSpace(locationId) || !long.TryParse(minuteText, out var minute) || minute < 0)
                return false;
            parsed = new WhereaboutsClaim(locationId, null, null, minute, detail);
            return true;
        }
        if (claim.StartsWith("traveling:", StringComparison.Ordinal))
        {
            var rest = claim.Substring(10);
            var atSplit = rest.IndexOf('@');
            if (atSplit < 0) return false;
            var leg = rest.Substring(0, atSplit);
            var arrowSplit = leg.IndexOf('>');
            if (arrowSplit < 0) return false;
            var from = leg.Substring(0, arrowSplit);
            var to = leg.Substring(arrowSplit + 1);
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
            if (!long.TryParse(rest.Substring(atSplit + 1), out var minute) || minute < 0) return false;
            parsed = new WhereaboutsClaim(null, from, to, minute, "");
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ contradiction model

    /// <summary>Two places at once: the strongest, always flagged.</summary>
    public const int BlatantScore = 100;

    /// <summary>Denied knowledge, then claimed details (or the reverse): an obvious tell.</summary>
    public const int DenialThenClaimedScore = 60;

    /// <summary>Same story, different details: only a sharp investigator notices.</summary>
    public const int SubtleScore = 50;

    /// <summary>
    /// The alibi window: two whereabouts claims closer than this at different places are
    /// blatant. Genius investigators hold alibis to a tighter standard (wider window).
    /// </summary>
    public static int BlatantWindowMinutes(Difficulty difficulty) =>
        difficulty == Difficulty.Genius ? 120 : 60;

    /// <summary>
    /// A clash is flagged when its score reaches the bar. The bar drops as
    /// <see cref="DifficultySystem.ContradictionSensitivity"/> rises:
    /// Easy(25)→76: blatant only · Medium(50)→51: +denial tells · Hard(75)→26: +all subtle ·
    /// Genius(100)→1: everything, with a wider alibi window.
    /// </summary>
    public static int FlagThreshold(Difficulty difficulty) =>
        101 - DifficultySystem.ContradictionSensitivity(difficulty);
}
