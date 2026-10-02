using System.Text;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Social;

namespace BadDeduction.AI;

/// <summary>
/// Phase 6: builds the prompt for ONE NPC from ONLY that NPC's knowledge.
/// <para/>
/// Context schema (sections, in order):
/// <list type="number">
/// <item>IDENTITY — speaker's public profile (name, age, occupation, kind).</item>
/// <item>PERSONALITY — six integer traits, when a profile exists (hand-built worlds have none).</item>
/// <item>GOALS — the speaker's goal labels with slots, when a profile exists.</item>
/// <item>FEELINGS — how the speaker sees the listener: all eight social axes with values and the trust band.</item>
/// <item>MEMORIES — the speaker's most recent memories, bounded by <see cref="DifficultySystem.ContextMemoryCap"/>
///   (source, confidence, day, summary). Only the speaker's own memories ever appear.</item>
/// <item>BELIEFS — the speaker's beliefs with band, confidence and evidence counts, bounded by
///   <see cref="DifficultySystem.ContextBeliefCap"/>.</item>
/// <item>SITUATION — day, time of day, clock time, and where the speaker is.</item>
/// <item>UTTERANCE — what the listener just said, verbatim (truncated).</item>
/// <item>INSTRUCTIONS — reply in character within the length cap, plus the structured fields.</item>
/// </list>
/// <para/>
/// What NEVER appears: hidden roles (the words "Malvr"/"Lumiel" are never written here),
/// anyone else's memories or beliefs, <c>WorldTruth</c>, or debug access. The engine holds no
/// reference to <c>GameState</c>/<c>WorldTruth</c>/<c>DebugAccess</c> at all, so truth cannot
/// leak into a prompt by construction (risk 2); a reflection test pins this, and a dedicated
/// leak test asserts no prompt contains role words or role-holder linkages.
/// <para/>
/// The speaker's own secrets are deliberately NOT included yet: guarding them is Phase 10
/// (hidden objectives) work, and no validator rule could catch a slip today.
/// </summary>
public sealed class ContextEngine
{
    private readonly PlayerView _view;
    private readonly CognitionService _cognition;
    private readonly SocialService _social;
    private readonly RelationshipGraph _graph;
    private readonly ContentDatabase _content;
    private readonly IReadOnlyDictionary<string, CharacterProfile> _profiles;
    private readonly Func<string, string> _currentLocationOf;

    /// <param name="currentLocationOf">
    /// Maps character id → current location id. A plain delegate (not GameState) so this class
    /// stays free of truth types; a character's own whereabouts are their own knowledge.
    /// </param>
    public ContextEngine(
        PlayerView view,
        CognitionService cognition,
        SocialService social,
        RelationshipGraph graph,
        ContentDatabase content,
        IReadOnlyDictionary<string, CharacterProfile> profiles,
        Func<string, string> currentLocationOf)
    {
        _view = view;
        _cognition = cognition;
        _social = social;
        _graph = graph;
        _content = content;
        _profiles = profiles;
        _currentLocationOf = currentLocationOf;
    }

    public string BuildPrompt(
        string speakerId, string listenerId, string utterance, Difficulty difficulty, GameTime now)
    {
        var speaker = RequireProfile(speakerId);
        var listener = RequireProfile(listenerId);
        if (speakerId == listenerId)
            throw new ArgumentException("A character cannot hold a dialogue with itself.");
        if (string.IsNullOrWhiteSpace(utterance))
            throw new ArgumentException("An utterance is required.", nameof(utterance));

        var sb = new StringBuilder();
        sb.AppendLine($"You are roleplaying {speaker.DisplayName}, a {speaker.Age}-year-old {OccupationLabel(speaker.OccupationId)}.");
        sb.AppendLine($"You are speaking face to face with {listener.DisplayName} ({OccupationLabel(listener.OccupationId)}).");
        sb.AppendLine();

        if (_profiles.TryGetValue(speakerId, out var profile))
        {
            var p = profile.Personality;
            sb.AppendLine("Your personality (0-100): " +
                $"extraversion {p.Extraversion}, agreeableness {p.Agreeableness}, " +
                $"conscientiousness {p.Conscientiousness}, neuroticism {p.Neuroticism}, " +
                $"honesty {p.Honesty}, courage {p.Courage}.");
            if (profile.Goals.Count > 0)
            {
                sb.AppendLine("Your goals:");
                foreach (var goal in profile.Goals)
                    sb.AppendLine($"- {GoalLabel(goal.DefinitionId)} ({goal.Slot})");
            }
            sb.AppendLine();
        }

        var rel = _social.View(speakerId, listenerId);
        sb.AppendLine($"How you feel about {listener.DisplayName}: " +
            $"trust {rel.Trust}/100 ({rel.Band}), suspicion {rel.Suspicion}/100, " +
            $"fear {rel.Fear}/100, respect {rel.Respect}/100, loyalty {rel.Loyalty}/100, " +
            $"influence {rel.Influence}/100, affection {rel.Affection}/100, resentment {rel.Resentment}/100.");
        sb.AppendLine();

        var memories = _cognition.GetMemories(speakerId);
        var memoryCap = DifficultySystem.ContextMemoryCap(difficulty);
        sb.AppendLine($"What you remember (latest {Math.Min(memoryCap, memories.Count)}):");
        foreach (var memory in memories.TakeLast(memoryCap).Reverse())
        {
            var day = new GameTime(memory.RecordedAt).Day;
            sb.AppendLine($"- [{memory.Source}, confidence {memory.Confidence}, day {day}] {memory.Summary}");
        }
        if (memories.Count == 0) sb.AppendLine("- (nothing yet)");
        sb.AppendLine();

        var beliefs = _cognition.GetBeliefs(speakerId);
        var beliefCap = DifficultySystem.ContextBeliefCap(difficulty);
        sb.AppendLine($"What you believe (up to {Math.Min(beliefCap, beliefs.Count)}):");
        foreach (var belief in beliefs.Take(beliefCap))
            sb.AppendLine($"- \"{belief.PropositionId}\" — {belief.Band} " +
                $"(confidence {belief.Confidence}, {belief.EvidenceFor} supporting / {belief.EvidenceAgainst} refuting)");
        if (beliefs.Count == 0) sb.AppendLine("- (nothing yet)");
        sb.AppendLine();

        var locationName = _content.HasLocation(_currentLocationOf(speakerId))
            ? _content.GetLocation(_currentLocationOf(speakerId)).Name
            : "somewhere";
        sb.AppendLine($"It is day {now.Day}, {now.Phase.ToString().ToLowerInvariant()} ({now.Hour:00}:{now.Minute:00}). You are at {locationName}.");
        sb.AppendLine();
        sb.AppendLine($"{listener.DisplayName} says to you: \"{Truncate(utterance, AIRules.MaxUtteranceLength)}\"");
        sb.AppendLine();
        sb.AppendLine(
            $"Reply in character in at most {AIRules.MaxReplyLength} characters. " +
            "Then decide, as structured fields: trust_delta (-20..20, how this changes your trust in them), " +
            "suspicion_delta (-20..20), new_facts (things you learned from them, as short phrases, max 5), " +
            "memory_summary (one line for your memory of this exchange), " +
            "relationship_note (optional, one line). " +
            "Never reveal anything you do not actually know; never speak of hidden masters or secret roles.");

        return sb.ToString();
    }

    /// <summary>
    /// Every text the speaker legitimately knows: their own and the listener's names, names of
    /// characters they have relationship edges with, public location names, their own memory
    /// summaries and belief propositions, and the current utterance. The validator's entity
    /// check tests provider output against this set.
    /// </summary>
    public IReadOnlyList<string> KnownTexts(string speakerId, string listenerId, string utterance)
    {
        var speaker = RequireProfile(speakerId);
        var listener = RequireProfile(listenerId);

        var texts = new List<string> { speaker.DisplayName, listener.DisplayName, utterance };
        foreach (var edge in _graph.From(speakerId))
        {
            var other = _view.PublicProfile(edge.To);
            if (other is not null) texts.Add(other.DisplayName);
        }
        foreach (var loc in _content.Locations)
            texts.Add(loc.Name);
        foreach (var memory in _cognition.GetMemories(speakerId))
            texts.Add(memory.Summary);
        foreach (var belief in _cognition.GetBeliefs(speakerId))
            texts.Add(belief.PropositionId);
        return texts;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The speaker's public display name (truth-free).</summary>
    public string DisplayNameOf(string characterId) => RequireProfile(characterId).DisplayName;

    private PublicCharacterInfo RequireProfile(string id) =>
        _view.PublicProfile(id) ?? throw new ArgumentException($"Unknown character '{id}'.");

    private string OccupationLabel(string occupationId)
    {
        foreach (var def in _content.Occupations)
            if (def.Id == occupationId) return def.Name;
        return occupationId;
    }

    private string GoalLabel(string definitionId)
    {
        foreach (var def in _content.Goals)
            if (def.Id == definitionId) return def.Label;
        return definitionId;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max);
}
