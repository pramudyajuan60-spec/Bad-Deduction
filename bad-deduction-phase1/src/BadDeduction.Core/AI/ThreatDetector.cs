namespace BadDeduction.AI;

/// <summary>How threatening an utterance is, per deterministic keyword heuristics.</summary>
public enum ThreatLevel
{
    None,
    Menacing,
    ExplicitThreat,
    DeathThreat,
}

/// <summary>
/// Phase 13: deterministic threat detection over player utterances (English and Indonesian).
/// A pure function — no state, no RNG, no truth access — so the same utterance always yields
/// the same level on every platform. Normalization: lowercase, common contractions expanded,
/// non-letters replaced by spaces, whitespace collapsed, padded with spaces; phrase matching
/// then uses word boundaries, so "skill issue" never trips on "kill" and passive "dibunuh"
/// never trips on "kubunuh".
/// </summary>
public static class ThreatDetector
{
    private static readonly string[] DeathThreatPhrases =
    {
        // English
        "kill you", "i ll kill you", "i will kill you", "want to kill you",
        "gonna kill you", "going to kill you", "murder you",
        "you are dead", "you re dead", "youre dead",
        // Indonesian
        "kubunuh", "akan kubunuh", "ku bunuh", "membunuhmu",
        "mati kau", "kau mati", "kamu mati",
        "kuhabisi", "akan kuhabisi", "kuhabisi kau",
    };

    private static readonly string[] ExplicitThreatPhrases =
    {
        // English
        "hurt you", "i ll hurt you", "ill hurt you",
        "break your", "watch your back",
        "you ll regret", "youll regret", "you will regret",
        "im warning you", "i m warning you", "last warning",
        // Indonesian
        "kuhajar", "akan kuhajar", "akan menyesal",
        "peringatan terakhir", "awas kau",
    };

    private static readonly string[] MenacingPhrases =
    {
        // English
        "dont test me", "do not test me", "dont push me", "do not push me",
        // Indonesian
        "jangan coba coba", "jangan main main", "hati hati kau",
    };

    private static readonly (string From, string To)[] ContractionExpansions =
    {
        ("i'll", "i ll"), ("you'll", "you ll"), ("you're", "you re"),
        ("i'm", "im"), ("don't", "dont"), ("won't", "wont"), ("can't", "cant"),
    };

    /// <summary>
    /// Classifies an utterance. DeathThreat is checked first, then ExplicitThreat, then
    /// Menacing — the most severe applicable level wins.
    /// </summary>
    public static ThreatLevel Detect(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return ThreatLevel.None;
        var text = " " + Normalize(utterance) + " ";
        if (ContainsAny(text, DeathThreatPhrases)) return ThreatLevel.DeathThreat;
        if (ContainsAny(text, ExplicitThreatPhrases)) return ThreatLevel.ExplicitThreat;
        if (ContainsAny(text, MenacingPhrases)) return ThreatLevel.Menacing;
        return ThreatLevel.None;
    }

    private static bool ContainsAny(string paddedText, string[] phrases)
    {
        foreach (var phrase in phrases)
            if (paddedText.Contains(" " + phrase + " ", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Normalize(string utterance)
    {
        var lowered = utterance.ToLowerInvariant();
        foreach (var (from, to) in ContractionExpansions)
            lowered = lowered.Replace(from, to, StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder(lowered.Length);
        var lastWasSpace = true; // trims leading whitespace for free
        foreach (var ch in lowered)
        {
            if (char.IsLetter(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        var result = sb.ToString();
        return result.EndsWith(' ') ? result[..^1] : result;
    }
}
