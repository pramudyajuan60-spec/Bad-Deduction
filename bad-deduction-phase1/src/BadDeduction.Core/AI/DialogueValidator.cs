namespace BadDeduction.AI;

/// <summary>The validator's verdict. The LLM proposes; the validator disposes.</summary>
public sealed class ValidationResult
{
    public bool Accepted { get; }
    public DialogueOutput? Output { get; }
    public bool DeltasClamped { get; }
    public string? RejectionReason { get; }

    private ValidationResult(bool accepted, DialogueOutput? output, bool deltasClamped, string? reason)
    {
        Accepted = accepted;
        Output = output;
        DeltasClamped = deltasClamped;
        RejectionReason = reason;
    }

    public static ValidationResult Accept(DialogueOutput output, bool deltasClamped) =>
        new(true, output, deltasClamped, null);

    public static ValidationResult Reject(string reason) =>
        new(false, null, false, reason);
}

/// <summary>
/// Phase 6: the gate between a provider's proposal and the simulation. Rejects output that is
/// (a) malformed — empty or over-long reply, too many facts, over-long fields;
/// (b) role-revealing — the words "malvr"/"lumiel" as whole words, or any forbidden phrase
///     (e.g. "{name} is Malvr"); the forbidden set is supplied by the caller, which derives it
///     outside this namespace so no truth type ever crosses the boundary;
/// (c) entity-leaking — a capitalized name in NewFacts/NewMemorySummary that appears nowhere in
///     the speaker's known texts (their names, places, memories, beliefs, the utterance).
/// Out-of-range trust/suspicion deltas are CLAMPED to ±<see cref="AIRules.MaxDeltaMagnitude"/>,
/// never rejected: a loud number is a calibration issue, not a lie.
/// </summary>
public sealed class DialogueValidator
{
    /// <summary>Capitalized words that are never treated as entity references.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "The", "A", "An", "I", "You", "He", "She", "We", "They", "It",
        "This", "That", "These", "Those", "Yesterday", "Today", "Tomorrow",
        "Here", "There", "What", "When", "Where", "Why", "How", "Who",
        "My", "Your", "His", "Her", "Our", "Their", "And", "But", "For", "With", "From",
    };

    public ValidationResult Validate(
        DialogueOutput? output,
        IReadOnlyList<string> knownTexts,
        IReadOnlyList<string> forbiddenPhrases)
    {
        if (output is null)
            return ValidationResult.Reject("no output produced");

        if (string.IsNullOrWhiteSpace(output.ReplyText))
            return ValidationResult.Reject("empty reply text");
        if (output.ReplyText.Length > AIRules.MaxReplyLength)
            return ValidationResult.Reject($"reply text exceeds {AIRules.MaxReplyLength} characters");

        var facts = output.NewFacts ?? new List<string>();
        if (facts.Count > AIRules.MaxNewFacts)
            return ValidationResult.Reject($"more than {AIRules.MaxNewFacts} new facts");
        for (var i = 0; i < facts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(facts[i]))
                return ValidationResult.Reject($"new fact #{i + 1} is empty");
            if (facts[i].Length > AIRules.MaxFactLength)
                return ValidationResult.Reject($"new fact #{i + 1} exceeds {AIRules.MaxFactLength} characters");
        }

        if (output.NewMemorySummary is { } summary)
        {
            if (string.IsNullOrWhiteSpace(summary))
                return ValidationResult.Reject("empty memory summary");
            if (summary.Length > AIRules.MaxFactLength)
                return ValidationResult.Reject($"memory summary exceeds {AIRules.MaxFactLength} characters");
        }

        if (output.RelationshipNote is { } note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return ValidationResult.Reject("empty relationship note");
            if (note.Length > AIRules.MaxRelationshipNoteLength)
                return ValidationResult.Reject($"relationship note exceeds {AIRules.MaxRelationshipNoteLength} characters");
        }
        var memorySummary = output.NewMemorySummary;

        // Role-revealing content: checked in every free-text field.
        foreach (var (field, text) in FreeTextFields(output))
        {
            if (ContainsRoleWord(text))
                return ValidationResult.Reject($"{field} mentions a hidden role");
            foreach (var phrase in forbiddenPhrases)
            {
                if (!string.IsNullOrWhiteSpace(phrase) &&
                    text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return ValidationResult.Reject($"{field} reveals a hidden role");
            }
        }

        // Unknown entities: capitalized tokens in facts/memories must be known to the speaker.
        foreach (var fact in facts)
        {
            var unknown = FirstUnknownEntity(fact, knownTexts);
            if (unknown is not null)
                return ValidationResult.Reject($"new fact references unknown entity '{unknown}'");
        }
        if (memorySummary is not null)
        {
            var unknown = FirstUnknownEntity(memorySummary, knownTexts);
            if (unknown is not null)
                return ValidationResult.Reject($"memory summary references unknown entity '{unknown}'");
        }

        // Clamp deltas (never reject for overflow).
        var trust = ClampDelta(output.TrustDelta);
        var suspicion = ClampDelta(output.SuspicionDelta);
        var clamped = trust != output.TrustDelta || suspicion != output.SuspicionDelta;

        return ValidationResult.Accept(new DialogueOutput
        {
            ReplyText = output.ReplyText,
            TrustDelta = trust,
            SuspicionDelta = suspicion,
            NewFacts = facts,
            NewMemorySummary = output.NewMemorySummary,
            RelationshipNote = output.RelationshipNote,
        }, clamped);
    }

    // ------------------------------------------------------------------ helpers

    private static int ClampDelta(int value) =>
        Math.Clamp(value, -AIRules.MaxDeltaMagnitude, AIRules.MaxDeltaMagnitude);

    private static IEnumerable<(string Field, string Text)> FreeTextFields(DialogueOutput output)
    {
        yield return ("reply text", output.ReplyText);
        var facts = output.NewFacts ?? new List<string>();
        for (var i = 0; i < facts.Count; i++)
            yield return ($"new fact #{i + 1}", facts[i]);
        if (output.NewMemorySummary is { } summary)
            yield return ("memory summary", summary);
        if (output.RelationshipNote is { } note)
            yield return ("relationship note", note);
    }

    /// <summary>The role words may never appear as whole words in NPC-authored text at this phase:
    /// no mechanic has introduced them into any character's knowledge.</summary>
    private static bool ContainsRoleWord(string text)
    {
        foreach (var word in SplitWords(text))
        {
            if (word.Equals("malvr", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("lumiel", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? FirstUnknownEntity(string text, IReadOnlyList<string> knownTexts)
    {
        foreach (var word in SplitWords(text))
        {
            if (word.Length < 3 || !char.IsLetter(word[0]) || !char.IsUpper(word[0])) continue;
            if (Stopwords.Contains(word)) continue;
            var known = false;
            foreach (var knownText in knownTexts)
            {
                if (knownText is not null &&
                    knownText.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    known = true;
                    break;
                }
            }
            if (!known) return word;
        }
        return null;
    }

    private static IEnumerable<string> SplitWords(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'')
                sb.Append(ch);
            else if (sb.Length > 0)
            {
                yield return sb.ToString().Trim('\'');
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString().Trim('\'');
    }
}
