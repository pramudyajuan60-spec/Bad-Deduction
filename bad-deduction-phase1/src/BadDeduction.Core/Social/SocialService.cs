using System.Globalization;
using BadDeduction.Characters;
using BadDeduction.Core;

namespace BadDeduction.Social;

/// <summary>Per-axis change applied to one directed edge. Positive or negative; the result is clamped to 0-100.</summary>
public readonly record struct SocialDelta(
    int Trust = 0, int Fear = 0, int Respect = 0, int Loyalty = 0,
    int Suspicion = 0, int Influence = 0, int Affection = 0, int Resentment = 0)
{
    public int Get(RelationshipAxis axis) => axis switch
    {
        RelationshipAxis.Trust => Trust,
        RelationshipAxis.Fear => Fear,
        RelationshipAxis.Respect => Respect,
        RelationshipAxis.Loyalty => Loyalty,
        RelationshipAxis.Suspicion => Suspicion,
        RelationshipAxis.Influence => Influence,
        RelationshipAxis.Affection => Affection,
        RelationshipAxis.Resentment => Resentment,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public bool IsZero
    {
        get
        {
            var self = this; // lambdas inside a struct cannot capture 'this'
            return SocialRules.AllAxes.All(a => self.Get(a) == 0);
        }
    }
}

/// <summary>
/// Read-only snapshot of how <see cref="From"/> sees <see cref="To"/>. For pairs with no edge yet it is the
/// "stranger" view (identical to a fresh acquaintance), and <see cref="Exists"/> is false.
/// </summary>
public sealed record RelationshipView(
    string From, string To, bool Exists, RelationshipKind? Kind,
    int Trust, int Fear, int Respect, int Loyalty, int Suspicion, int Influence, int Affection, int Resentment)
{
    public int Get(RelationshipAxis axis) => axis switch
    {
        RelationshipAxis.Trust => Trust,
        RelationshipAxis.Fear => Fear,
        RelationshipAxis.Respect => Respect,
        RelationshipAxis.Loyalty => Loyalty,
        RelationshipAxis.Suspicion => Suspicion,
        RelationshipAxis.Influence => Influence,
        RelationshipAxis.Affection => Affection,
        RelationshipAxis.Resentment => Resentment,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public TrustBand Band => SocialRules.BandOf(Trust);
}

/// <summary>
/// Phase 3: the only sanctioned way to change how people feel about each other. Every change is validated,
/// clamped, and recorded as a causally-linked WorldEvent, so "why does she distrust him?" is answerable from
/// the log. Volatile axes (fear, suspicion, resentment) relax toward their resting value once per day.
/// Nothing here reads hidden roles: the social layer works on what people feel, never on who is Malvr or Lumiel.
/// </summary>
public sealed class SocialService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly RelationshipGraph _graph;

    public SocialService(GameState state, EventSystem events, RelationshipGraph graph)
    {
        _state = state;
        _events = events;
        _graph = graph;
        _events.Subscribe<DayChanged>(_ => ApplyDailyDrift());
    }

    public RelationshipView View(string from, string to)
    {
        Require(from);
        Require(to);
        if (from == to) throw new ArgumentException("A character has no relationship with itself.");
        // Phase 14: the player's trust meter starts at exactly 20 for every NPC
        // (SocialRules.PlayerStartingTrust) — personality nudges don't apply to
        // player pairs. NPC↔NPC pairs keep their kind-based baselines.
        var playerId = _state.Player.CharacterId;
        var playerPair = !string.IsNullOrEmpty(playerId) && (from == playerId || to == playerId);
        var e = _graph.Get(from, to);
        if (e is not null)
            return new RelationshipView(from, to, true, e.Kind, e.Trust, e.Fear, e.Respect, e.Loyalty, e.Suspicion, e.Influence, e.Affection, e.Resentment);
        var v = SocialBaselines.Resting(null, PersonalityOf(from), PersonalityOf(to));
        return new RelationshipView(from, to, false, null,
            playerPair ? SocialRules.PlayerStartingTrust : v[(int)RelationshipAxis.Trust],
            v[(int)RelationshipAxis.Fear], v[(int)RelationshipAxis.Respect], v[(int)RelationshipAxis.Loyalty],
            v[(int)RelationshipAxis.Suspicion], v[(int)RelationshipAxis.Influence], v[(int)RelationshipAxis.Affection],
            v[(int)RelationshipAxis.Resentment]);
    }

    /// <summary>
    /// Changes how <paramref name="from"/> sees <paramref name="to"/> (one direction only). First contact between two
    /// people who have no edge makes them acquaintances in both directions. Returns the logged event, or null when
    /// nothing changed (zero delta, or everything already at its limit).
    /// </summary>
    public WorldEvent? Adjust(string from, string to, SocialDelta delta, string reason, long? causedBy = null)
    {
        Require(from);
        Require(to);
        if (from == to) throw new ArgumentException("A character cannot change its relationship with itself.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required so the change can be explained later.", nameof(reason));

        var edge = _graph.Get(from, to);
        var created = false;
        if (edge is null)
        {
            if (delta.IsZero) return null;
            _graph.Connect(from, to, RelationshipKind.Acquaintance);
            edge = _graph.Get(from, to)!;
            created = true;
            // Phase 14: player pairs start at exactly Trust 20 (SocialRules.PlayerStartingTrust).
            var playerId = _state.Player.CharacterId;
            if (!string.IsNullOrEmpty(playerId) && (from == playerId || to == playerId))
                edge.SetAxis(RelationshipAxis.Trust, SocialRules.PlayerStartingTrust);
        }

        var data = new Dictionary<string, string> { ["reason"] = reason };
        var changed = created;
        foreach (var axis in SocialRules.AllAxes)
        {
            var requested = delta.Get(axis);
            if (requested == 0) continue;
            var before = edge.GetAxis(axis);
            edge.SetAxis(axis, (long)before + requested);
            var applied = edge.GetAxis(axis) - before;
            if (applied == 0) continue;
            data[axis.ToString().ToLowerInvariant()] = applied.ToString("+0;-0", CultureInfo.InvariantCulture);
            changed = true;
        }
        if (!changed) return null;
        if (created) data["created"] = "acquaintance";
        return _events.Record(WorldEventTypes.RelationshipChanged, participants: new[] { from, to }, data: data, causedBy: causedBy);
    }

    /// <summary>Moves every volatile axis one day's step toward its resting value, never overshooting.</summary>
    public void ApplyDailyDrift()
    {
        foreach (var e in _state.World.Relationships)
        {
            var resting = SocialBaselines.Resting(e.Kind, PersonalityOf(e.From), PersonalityOf(e.To));
            foreach (var axis in SocialRules.AllAxes)
            {
                var step = SocialRules.DailyStep(axis);
                if (step == 0) continue;
                var cur = e.GetAxis(axis);
                var target = resting[(int)axis];
                if (cur > target) e.SetAxis(axis, Math.Max(target, cur - step));
                else if (cur < target) e.SetAxis(axis, Math.Min(target, cur + step));
            }
        }
    }

    // ------------------------------------------------------------------ police (design §10)

    /// <summary>
    /// Sets every police officer's trust toward <paramref name="subjectId"/> (design: 90 toward Lumiel at the start).
    /// The caller decides who the subject is; this class never looks at hidden roles. Returns the officer count.
    /// </summary>
    public int SeedPoliceTrust(string subjectId, int trust = SocialRules.PoliceStartingTrust)
    {
        Require(subjectId);
        var count = 0;
        foreach (var officer in _state.World.Characters.Values
                     .Where(c => c.Kind == CharacterKind.Police && c.Id != subjectId)
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var current = View(officer.Id, subjectId).Trust;
            Adjust(officer.Id, subjectId, new SocialDelta(Trust: SocialRules.Clamp(trust) - current), "initial police trust");
            count++;
        }
        return count;
    }

    /// <summary>Where this officer sits on the trust ladder toward the subject: cooperate, question, verify, or suspect.</summary>
    public PoliceStance StanceToward(string officerId, string subjectId)
    {
        Require(officerId);
        if (_state.World.Characters[officerId].Kind != CharacterKind.Police)
            throw new InvalidOperationException($"'{officerId}' is not a police officer.");
        return SocialRules.StanceOf(View(officerId, subjectId).Trust);
    }

    // ----------------------------------------------------------------------------- helpers

    internal Personality? PersonalityOf(string id) => _state.World.Profiles.GetValueOrDefault(id)?.Personality;

    private void Require(string id)
    {
        if (!_state.World.Characters.ContainsKey(id)) throw new ArgumentException($"Unknown character '{id}'.");
    }
}
