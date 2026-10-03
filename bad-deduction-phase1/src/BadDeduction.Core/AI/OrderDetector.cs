namespace BadDeduction.AI;

/// <summary>
/// Kinds of orders the player can issue through free-text dialogue. A modest,
/// extensible taxonomy: movement and violence now, trade and theft hooks for later
/// phases (the shop/inventory economy is Phase 17; kill methods are Phase 15).
/// </summary>
public enum OrderKind
{
    None,
    GoTo,
    Buy,
    Steal,
    Lie,
    Attack,
    Follow,
    Wait,
}

/// <summary>
/// One detected order: its kind, the raw target text (item, person, topic), a
/// resolved location id for <see cref="OrderKind.GoTo"/> (null when unresolvable),
/// and an absolute game minute for time-qualified orders (null when none given).
/// </summary>
public sealed record DetectedOrder(
    OrderKind Kind,
    string? TargetText,
    string? LocationId,
    long? TimeMinute);

/// <summary>
/// Phase 14: deterministic order-intent detection over player utterances (English and
/// Indonesian), following the <see cref="ThreatDetector"/> pattern exactly: lowercase,
/// contraction expansion, non-letters to spaces, whitespace collapsed, padded with
/// spaces, phrase matching with word boundaries. A pure function — no state, no RNG,
/// no truth access — so the same utterance always yields the same order.
/// <para/>
/// Severity order (most severe wins): Attack → Steal → Lie → Buy → GoTo → Follow →
/// Wait. Second-person violence ("kill you", "kubunuh") is NOT an order — the threat
/// pipeline owns it; Attack here means violence against a third party ("kill him").
/// </summary>
public static class OrderDetector
{
    // Verbs are bare ("kill", "bunuh") so the target AFTER the verb is captured
    // ("kill him" -> target "him"). Second-person targets ("kill you", "kubunuh")
    // are filtered out below — the threat pipeline owns those.
    private static readonly string[] AttackPhrases =
    {
        // English
        "get rid of", "take out",
        "kill", "murder", "attack", "hurt",
        // Indonesian
        "singkirkan", "habisi", "bunuh", "serang", "hajar",
    };

    private static readonly string[] StealPhrases =
    {
        // English
        "steal", "rob", "pickpocket",
        // Indonesian
        "curi", "mencuri", "rampok",
    };

    private static readonly string[] LiePhrases =
    {
        // English
        "lie about", "lie to them", "lie for me", "tell them that", "say that",
        // Indonesian
        "bohong", "berbohong", "bilang bahwa", "katakan bahwa",
    };

    private static readonly string[] BuyPhrases =
    {
        // English
        "buy me", "buy a", "buy some", "purchase",
        // Indonesian
        "belikan", "beli", "belilah",
    };

    private static readonly string[] GoToPhrases =
    {
        // English
        "go to", "meet me at", "come to", "head to", "wait for me at",
        // Indonesian
        "pergi ke", "datang ke", "temui aku di", "ketemu di",
    };

    private static readonly string[] FollowPhrases =
    {
        // English
        "follow him", "follow her", "follow them", "watch him", "watch her", "keep an eye on",
        // Indonesian
        "ikuti dia", "awasi dia", "buntuti",
    };

    private static readonly string[] WaitPhrases =
    {
        // English
        "wait here", "stay here", "dont move", "do not move", "stay put",
        // Indonesian
        "tunggu di sini", "tunggu sini", "diam di sini", "jangan pergi", "tunggu",
    };

    private static readonly (string From, string To)[] ContractionExpansions =
    {
        ("i'll", "i ll"), ("you'll", "you ll"), ("you're", "you re"),
        ("i'm", "im"), ("don't", "dont"), ("won't", "wont"), ("can't", "cant"),
    };

    /// <summary>Second-person targets: violence here belongs to the threat pipeline, not orders.</summary>
    private static readonly string[] SecondPersonTargets =
        { "you", "kamu", "kau", "anda", "mu" };

    /// <summary>
    /// Classifies an utterance into an order. <paramref name="resolveLocation"/> maps a
    /// location name fragment to a location id (null when unknown);
    /// <paramref name="nowMinute"/> is the absolute game minute used to resolve time
    /// phrases ("midnight" → the next midnight).
    /// </summary>
    public static DetectedOrder Detect(
        string utterance,
        Func<string, string?> resolveLocation,
        long nowMinute)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return new DetectedOrder(OrderKind.None, null, null, null);
        var text = " " + Normalize(utterance) + " ";

        foreach (var kind in new[] { OrderKind.Attack, OrderKind.Steal, OrderKind.Lie,
                     OrderKind.Buy, OrderKind.GoTo, OrderKind.Follow, OrderKind.Wait })
        {
            var phrases = PhrasesFor(kind);
            foreach (var phrase in phrases)
            {
                var hit = " " + phrase + " ";
                var idx = text.IndexOf(hit, StringComparison.Ordinal);
                if (idx < 0) continue;
                var after = text.Substring(idx + hit.Length).Trim();
                if (kind == OrderKind.Attack && IsSecondPerson(after))
                    continue; // "kill you" is a threat, not an order — skip this phrase
                return BuildOrder(kind, after, resolveLocation, nowMinute);
            }
        }
        return new DetectedOrder(OrderKind.None, null, null, null);
    }

    private static string[] PhrasesFor(OrderKind kind) => kind switch
    {
        OrderKind.Attack => AttackPhrases,
        OrderKind.Steal => StealPhrases,
        OrderKind.Lie => LiePhrases,
        OrderKind.Buy => BuyPhrases,
        OrderKind.GoTo => GoToPhrases,
        OrderKind.Follow => FollowPhrases,
        OrderKind.Wait => WaitPhrases,
        _ => Array.Empty<string>(),
    };

    private static bool IsSecondPerson(string after)
    {
        var first = after.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return SecondPersonTargets.Contains(first, StringComparer.Ordinal);
    }

    private static DetectedOrder BuildOrder(
        OrderKind kind, string after, Func<string, string?> resolveLocation, long nowMinute)
    {
        string? target = string.IsNullOrWhiteSpace(after) ? null
            : after.Length > 60 ? after.Substring(0, 60).Trim() : after;
        string? locationId = null;
        long? timeMinute = null;

        if (kind == OrderKind.GoTo && target is not null)
        {
            var (placeText, time) = SplitTime(target, nowMinute);
            timeMinute = time;
            locationId = resolveLocation(placeText);
            target = placeText;
        }
        else if (kind is OrderKind.Wait or OrderKind.Follow)
        {
            // No target/time extraction: the order is the act itself.
            target = null;
        }

        return new DetectedOrder(kind, target, locationId, timeMinute);
    }

    /// <summary>
    /// Splits trailing time phrases off a location fragment. Recognized: midnight /
    /// tengah malam → next 00:00; tonight / malam ini / nanti malam → next 21:00;
    /// "jam N" / "at N" → next N:00. Returns (placeText, absoluteMinute or null).
    /// </summary>
    private static (string Place, long? Time) SplitTime(string text, long nowMinute)
    {
        var day = nowMinute / 1440;
        var minuteOfDay = (int)(nowMinute % 1440);

        long? AtNext(int mod)
        {
            var m = day * 1440 + mod;
            return m <= nowMinute ? m + 1440 : m;
        }

        foreach (var phrase in new[] { "tengah malam", "midnight" })
        {
            var idx = text.LastIndexOf(" " + phrase, StringComparison.Ordinal);
            if (idx >= 0) return (text.Substring(0, idx).Trim(), AtNext(0));
        }
        foreach (var phrase in new[] { "nanti malam", "malam ini", "tonight" })
        {
            var idx = text.LastIndexOf(" " + phrase, StringComparison.Ordinal);
            if (idx >= 0) return (text.Substring(0, idx).Trim(), AtNext(21 * 60));
        }
        // "jam 10" / "at 10": hour N today-or-tomorrow.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if ((tokens[i] == "jam" || tokens[i] == "at")
                && int.TryParse(tokens[i + 1], out var hour) && hour >= 0 && hour < 24)
            {
                var place = string.Join(" ", tokens.Take(i)).Trim();
                return (place, AtNext(hour * 60));
            }
        }
        return (text, null);
    }

    private static string Normalize(string utterance)
    {
        var lowered = utterance.ToLowerInvariant();
        foreach (var (from, to) in ContractionExpansions)
            lowered = lowered.Replace(from, to, StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder(lowered.Length);
        var lastWasSpace = true;
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
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
