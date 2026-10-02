using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.World;

namespace BadDeduction.Characters;

public sealed class CastSpec
{
    /// <summary>Phase 10 picks the two hidden geniuses from this pool, so the slice's "15 NPCs + Malvr + Lumiel" is 17 civilians.</summary>
    public int Civilians { get; set; } = 17;
    public int Police { get; set; } = 5;
    public string HomeLocationId { get; set; } = "loc_residential";

    /// <summary>Chance that a person shares a household with one more person.</summary>
    public double HouseholdChance { get; set; } = 0.45;

    /// <summary>Locations outside the vertical slice: no jobs or leisure there (see audit C-6).</summary>
    public HashSet<string> ExcludedLocationIds { get; set; } = new() { "loc_harbor", "loc_underground" };
}

/// <summary>
/// Phase 2: builds the run's cast (people, households, personalities, secrets, relationships, goals,
/// schedules) from the RunSeed. Each stage uses its own named RNG stream, so changing one stage never
/// shifts another, and the simulation RNG is never consumed. Character ids are shuffled, so an id
/// says nothing about role or occupation. No hidden role is assigned here (Phase 10).
/// </summary>
public sealed class CastGenerator
{
    private enum Shape { Single, Spouses, Siblings, ParentChild }

    private sealed class Draft
    {
        public CharacterKind Kind;
        public int Age;
        public OccupationDefinition Occupation = null!;
        public int Household;
        public string Id = "";
        public string Name = "";
    }

    private readonly GameState _state;
    private readonly ContentDatabase _content;
    private readonly WorldService _world;
    private readonly RelationshipGraph _graph;

    public CastGenerator(GameState state, ContentDatabase content, WorldService world, RelationshipGraph graph)
    {
        _state = state;
        _content = content;
        _world = world;
        _graph = graph;
    }

    public IReadOnlyList<string> Generate(CastSpec spec)
    {
        if (_state.World.Characters.Count > 0) throw new InvalidOperationException("The cast can only be generated into an empty world.");
        if (!_content.HasLocation(spec.HomeLocationId)) throw new ArgumentException($"Unknown home location '{spec.HomeLocationId}'.", nameof(spec));
        var seed = _state.Meta.RunSeed;

        var drafts = DrawPeople(spec, DeterministicRandom.Derive(seed, "cast.people"));
        AssignIdsAndNames(drafts, DeterministicRandom.Derive(seed, "cast.names"));

        foreach (var d in drafts)
            _world.AddCharacter(new CharacterState
            {
                Id = d.Id, DisplayName = d.Name, Age = d.Age, OccupationId = d.Occupation.Id, Kind = d.Kind,
                HomeLocationId = spec.HomeLocationId, WorkLocationId = d.Occupation.WorkLocationId,
                CurrentLocationId = spec.HomeLocationId,
            });

        var rngPersonality = DeterministicRandom.Derive(seed, "cast.personality");
        var rngSecrets = DeterministicRandom.Derive(seed, "cast.secrets");
        foreach (var d in drafts)
        {
            var profile = new CharacterProfile { Personality = DrawPersonality(rngPersonality), HouseholdId = $"h_{d.Household + 1:D2}" };
            var secret = DrawSecret(rngSecrets, profile.Personality, d.Kind);
            if (secret is not null) profile.SecretIds.Add(secret.Id);
            _state.World.Profiles[d.Id] = profile;
        }

        BuildRelationships(drafts, DeterministicRandom.Derive(seed, "cast.social"));
        AssignGoals(drafts, DeterministicRandom.Derive(seed, "cast.goals"));

        var rngSchedule = DeterministicRandom.Derive(seed, "cast.schedules");
        foreach (var d in drafts)
            _state.World.Schedules[d.Id] = BuildSchedule(d, spec, rngSchedule);

        new ScheduleSystem(_state, _content).PlaceAtScheduledPositions();
        return drafts.Select(d => d.Id).ToList();
    }

    // ---------------------------------------------------------------- people

    private List<Draft> DrawPeople(CastSpec spec, DeterministicRandom rng)
    {
        var occs = _content.Occupations.Where(o => !spec.ExcludedLocationIds.Contains(o.WorkLocationId)).ToList();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        int civLeft = spec.Civilians, polLeft = spec.Police;
        var drafts = new List<Draft>();
        var household = 0;

        CharacterKind TakeKind()
        {
            var kind = rng.NextInt(0, civLeft + polLeft) < polLeft ? CharacterKind.Police : CharacterKind.Civilian;
            if (kind == CharacterKind.Police) polLeft--; else civLeft--;
            return kind;
        }

        void Return(CharacterKind kind) { if (kind == CharacterKind.Police) polLeft++; else civLeft++; }

        void Add(CharacterKind anchorKind, OccupationDefinition? forced)
        {
            CharacterKind? companion = null;
            if (civLeft + polLeft > 0 && rng.Chance(spec.HouseholdChance)) companion = TakeKind();

            var built = TryHousehold(rng, occs, used, anchorKind, forced, companion)
                        ?? (companion is null ? null : TryHousehold(rng, occs, used, anchorKind, forced, null));
            if (built is null)
                throw new InvalidOperationException("Could not fill the cast from the available occupations; check occupation ages/counts.");
            if (companion is not null && built.Count == 1) Return(companion.Value);

            foreach (var d in built) { d.Household = household; drafts.Add(d); used[d.Occupation.Id] = used.GetValueOrDefault(d.Occupation.Id) + 1; }
            household++;
        }

        foreach (var occ in occs.Where(o => o.MinCount > 0))
            for (var i = 0; i < occ.MinCount; i++)
            {
                if (occ.Kind == CharacterKind.Police ? polLeft <= 0 : civLeft <= 0)
                    throw new InvalidOperationException($"Cast size is too small for the required occupation '{occ.Id}'.");
                if (occ.Kind == CharacterKind.Police) polLeft--; else civLeft--;
                Add(occ.Kind, occ);
            }

        while (civLeft + polLeft > 0) Add(TakeKind(), null);
        return drafts;
    }

    private static List<Draft>? TryHousehold(
        DeterministicRandom rng, List<OccupationDefinition> occs, Dictionary<string, int> used,
        CharacterKind anchorKind, OccupationDefinition? forced, CharacterKind? companionKind)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var shape = companionKind is null ? Shape.Single : (Shape)rng.NextInt(1, 4);
            var anchorAge = forced is not null ? rng.NextInt(forced.MinAge, forced.MaxAge + 1) : rng.NextInt(24, 63);
            var companionAge = shape switch
            {
                Shape.Spouses => anchorAge + rng.NextInt(-6, 7),
                Shape.Siblings => anchorAge + rng.NextInt(-8, 9),
                Shape.ParentChild => rng.Chance(0.5) ? anchorAge - rng.NextInt(20, 36) : anchorAge + rng.NextInt(20, 36),
                _ => 0,
            };
            if (shape != Shape.Single && companionAge is < 18 or > 75) continue;

            var pending = new Dictionary<string, int>(used, StringComparer.Ordinal);
            var anchorOcc = forced ?? PickOccupation(rng, occs, pending, anchorKind, anchorAge);
            if (anchorOcc is null) continue;
            pending[anchorOcc.Id] = pending.GetValueOrDefault(anchorOcc.Id) + 1;

            var result = new List<Draft> { new() { Kind = anchorKind, Age = anchorAge, Occupation = anchorOcc } };
            if (companionKind is { } ck)
            {
                var occ = PickOccupation(rng, occs, pending, ck, companionAge);
                if (occ is null) continue;
                result.Add(new Draft { Kind = ck, Age = companionAge, Occupation = occ });
            }
            return result;
        }
        return null;
    }

    private static OccupationDefinition? PickOccupation(
        DeterministicRandom rng, List<OccupationDefinition> occs, Dictionary<string, int> used, CharacterKind kind, int age)
    {
        var options = occs.Where(o => o.Kind == kind && age >= o.MinAge && age <= o.MaxAge && used.GetValueOrDefault(o.Id) < o.MaxCount).ToList();
        return options.Count == 0 ? null : WeightedPick(rng, options, o => o.Weight);
    }

    private void AssignIdsAndNames(List<Draft> drafts, DeterministicRandom rng)
    {
        // Shuffle so ids carry no information about generation order (captain, households, ...).
        rng.Shuffle(drafts);
        for (var i = 0; i < drafts.Count; i++) drafts[i].Id = $"c_{i + 1:D2}";

        var given = _content.Names.Given.ToList();
        var surnames = _content.Names.Surnames.ToList();
        rng.Shuffle(given);
        rng.Shuffle(surnames);

        var householdCount = drafts.Max(d => d.Household) + 1;
        if (given.Count < drafts.Count || surnames.Count < householdCount)
            throw new InvalidOperationException("The name tables are too small for this cast size.");

        var surnameOf = new Dictionary<int, string>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            if (!surnameOf.TryGetValue(d.Household, out var surname)) surnameOf[d.Household] = surname = surnames[surnameOf.Count];
            d.Name = $"{given[i]} {surname}";
        }
        drafts.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    // ------------------------------------------------- personality and secrets

    private static Personality DrawPersonality(DeterministicRandom rng)
    {
        // Mean of two uniform draws: traits cluster near the middle, extremes are rarer.
        int Trait() => (rng.NextInt(0, 101) + rng.NextInt(0, 101)) / 2;
        return new Personality
        {
            Extraversion = Trait(), Agreeableness = Trait(), Conscientiousness = Trait(),
            Neuroticism = Trait(), Honesty = Trait(), Courage = Trait(),
        };
    }

    private SecretDefinition? DrawSecret(DeterministicRandom rng, Personality p, CharacterKind kind)
    {
        if (_content.Secrets.Count == 0) return null;
        var baseChance = kind == CharacterKind.Police ? 0.12 : 0.30;
        var chance = Math.Clamp(baseChance * (1.0 + (50 - p.Honesty) / 100.0), 0.0, 0.9);
        return rng.Chance(chance) ? WeightedPick(rng, _content.Secrets.ToList(), s => s.Weight) : null;
    }

    // ---------------------------------------------------------- relationships

    private void BuildRelationships(List<Draft> drafts, DeterministicRandom rng)
    {
        // Family: everyone in a household.
        foreach (var group in drafts.GroupBy(d => d.Household))
        {
            var members = group.ToList();
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                    _graph.Connect(members[i].Id, members[j].Id, RelationshipKind.Family);
        }

        // Colleagues: most pairs at the same workplace know each other.
        foreach (var group in drafts.GroupBy(d => d.Occupation.WorkLocationId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                    if (rng.Chance(0.85)) _graph.Connect(members[i].Id, members[j].Id, RelationshipKind.Colleague);
        }

        // Friends: outgoing people make more friends; similar ages are likelier.
        foreach (var d in drafts)
        {
            var p = _state.World.Profiles[d.Id].Personality;
            var wanted = rng.NextInt(0, 1 + p.Extraversion / 34);
            for (var k = 0; k < wanted && FriendCount(d.Id) < 4; k++)
            {
                var options = drafts.Where(o => o.Id != d.Id && _graph.Get(d.Id, o.Id) is null && FriendCount(o.Id) < 4).ToList();
                if (options.Count == 0) break;
                var friend = WeightedPick(rng, options, o => Math.Abs(o.Age - d.Age) <= 15 ? 3 : 1);
                _graph.Connect(d.Id, friend.Id, RelationshipKind.Friend);
            }
        }

        // Rivals: the least agreeable people pick a fight with someone they are not close to.
        var rivalCount = Math.Max(1, drafts.Count / 8);
        var prickly = drafts.OrderBy(d => _state.World.Profiles[d.Id].Personality.Agreeableness).ThenBy(d => d.Id, StringComparer.Ordinal).Take(rivalCount);
        foreach (var d in prickly)
        {
            var options = drafts.Where(o => o.Id != d.Id && _graph.Get(d.Id, o.Id) is null).ToList();
            if (options.Count > 0) _graph.Connect(d.Id, rng.Pick(options).Id, RelationshipKind.Rival);
        }

        // Guarantee one connected social network (information/rumors can reach everyone eventually).
        while (true)
        {
            var comps = _graph.Components(drafts.Select(d => d.Id));
            if (comps.Count <= 1) break;
            var largest = comps.OrderByDescending(c => c.Count).ThenBy(c => c[0], StringComparer.Ordinal).First();
            var other = comps.Where(c => c != largest).OrderBy(c => c[0], StringComparer.Ordinal).First();
            _graph.Connect(rng.Pick(other), rng.Pick(largest), RelationshipKind.Acquaintance);
        }
    }

    private int FriendCount(string id) => _graph.Of(id, RelationshipKind.Friend).Count();

    // ------------------------------------------------------------------ goals

    private void AssignGoals(List<Draft> drafts, DeterministicRandom rng)
    {
        foreach (var d in drafts)
        {
            var profile = _state.World.Profiles[d.Id];
            var family = _graph.Of(d.Id, RelationshipKind.Family).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var friends = _graph.Of(d.Id, RelationshipKind.Friend).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var tags = new HashSet<string> { d.Kind == CharacterKind.Police ? "police" : "civilian" };
            if (family.Count > 0) tags.Add("family");
            if (friends.Count > 0) tags.Add("friend");
            if (profile.SecretIds.Count > 0) tags.Add("secret");

            bool Eligible(GoalDefinition g) => g.Requires.All(tags.Contains);

            var primary = WeightedPick(rng, _content.Goals.Where(g => g.Slot == GoalSlot.Primary && Eligible(g)).ToList(), g => g.Weight);
            profile.Goals.Add(MakeGoal(primary, rng, family, friends, profile, rng.NextInt(60, 91)));

            var secondaryPool = _content.Goals.Where(g => g.Slot == GoalSlot.Secondary && Eligible(g)).ToList();
            var secondaryCount = Math.Min(secondaryPool.Count, rng.NextInt(1, 3));
            for (var i = 0; i < secondaryCount; i++)
            {
                var g = WeightedPick(rng, secondaryPool, x => x.Weight);
                secondaryPool.Remove(g);
                profile.Goals.Add(MakeGoal(g, rng, family, friends, profile, rng.NextInt(20, 56)));
            }
        }
    }

    private Goal MakeGoal(GoalDefinition def, DeterministicRandom rng, List<string> family, List<string> friends, CharacterProfile profile, int priority)
    {
        string? target = def.TargetFrom switch
        {
            GoalTargetSource.Family => rng.Pick(family),
            GoalTargetSource.Friend => rng.Pick(friends),
            _ => null,
        };
        if (def.Id == "hide_secret")
        {
            var severity = _content.Secrets.First(s => s.Id == profile.SecretIds[0]).Severity;
            priority = Math.Min(100, priority + 5 * severity);
        }
        return new Goal { DefinitionId = def.Id, Slot = def.Slot, TargetId = target, Priority = priority };
    }

    // -------------------------------------------------------------- schedules

    private const int LatestHomeArrival = 22 * 60 + 30;

    private Schedule BuildSchedule(Draft d, CastSpec spec, DeterministicRandom rng)
    {
        var personality = _state.World.Profiles[d.Id].Personality;
        var schedule = new Schedule
        {
            Workday = BuildWorkday(d.Occupation, spec, personality, rng),
            DayOffIndex = d.Occupation.HasDayOff ? rng.NextInt(0, 7) : -1,
        };
        if (schedule.DayOffIndex >= 0) schedule.DayOff = BuildDayOff(spec, personality, rng);
        return schedule;
    }

    private List<ScheduleBlock> BuildWorkday(OccupationDefinition occ, CastSpec spec, Personality p, DeterministicRandom rng)
    {
        var home = spec.HomeLocationId;
        var work = occ.WorkLocationId;
        int start = occ.ShiftStartHour * 60, end = occ.ShiftEndHour * 60;
        var toWork = _content.TravelMinutes(home, work);
        var wake = Math.Min(330 + (100 - p.Conscientiousness) * 60 / 100 + rng.NextInt(0, 30), start - toWork - 40);

        var blocks = new List<ScheduleBlock>();
        void Add(int from, int to, string loc, Activity a) => blocks.Add(new ScheduleBlock { StartMinute = from, EndMinute = to, LocationId = loc, Activity = a });

        Add(0, wake, home, Activity.Sleeping);
        Add(wake, start, home, Activity.Idle);

        var arriveHome = 0;
        var leisure = rng.Chance(0.35 + p.Extraversion / 200.0) ? PickLeisure(spec, p, rng, exclude: work) : null;
        if (leisure is not null)
        {
            var arriveL = end + _content.TravelMinutes(work, leisure);
            var stay = rng.NextInt(90, 181);
            arriveHome = arriveL + stay + _content.TravelMinutes(leisure, home);
            if (arriveHome > LatestHomeArrival) leisure = null;
            else
            {
                Add(start, arriveL, work, Activity.Working);
                Add(arriveL, arriveHome, leisure, Activity.Socializing);
            }
        }
        if (leisure is null)
        {
            arriveHome = end + _content.TravelMinutes(work, home);
            Add(start, arriveHome, work, Activity.Working);
        }

        var bed = Math.Max(arriveHome + 20, 21 * 60 + 30 + rng.NextInt(0, 60));
        Add(arriveHome, bed, home, Activity.Idle);
        Add(bed, GameTime.MinutesPerDay, home, Activity.Sleeping);
        return blocks;
    }

    private List<ScheduleBlock> BuildDayOff(CastSpec spec, Personality p, DeterministicRandom rng)
    {
        var home = spec.HomeLocationId;
        var blocks = new List<ScheduleBlock>();
        void Add(int from, int to, string loc, Activity a) => blocks.Add(new ScheduleBlock { StartMinute = from, EndMinute = to, LocationId = loc, Activity = a });

        var wake = 7 * 60 + rng.NextInt(0, 120);
        Add(0, wake, home, Activity.Sleeping);

        var cursor = wake;             // start of the current block at home
        string? firstOuting = null;
        for (var outing = 0; outing < 2; outing++)
        {
            var chance = outing == 0 ? 0.85 : 0.30 + p.Extraversion / 250.0;
            if (!rng.Chance(chance)) continue;
            var place = PickLeisure(spec, p, rng, exclude: firstOuting);
            if (place is null) continue;

            var leave = outing == 0 ? 10 * 60 + rng.NextInt(0, 120) : Math.Max(cursor + 60, 18 * 60 + rng.NextInt(0, 90));
            var arrive = leave + _content.TravelMinutes(home, place);
            var back = arrive + (outing == 0 ? rng.NextInt(90, 241) : rng.NextInt(90, 151)) + _content.TravelMinutes(place, home);
            if (back > LatestHomeArrival || arrive <= cursor) continue;

            Add(cursor, arrive, home, Activity.Idle);
            Add(arrive, back, place, Activity.Socializing);
            cursor = back;
            firstOuting ??= place;
        }

        var bed = Math.Max(cursor + 20, 21 * 60 + 30 + rng.NextInt(0, 60));
        Add(cursor, bed, home, Activity.Idle);
        Add(bed, GameTime.MinutesPerDay, home, Activity.Sleeping);
        return blocks;
    }

    /// <summary>Public, non-official places. Outgoing people favour social hubs; steady, kind people favour sanctuaries.</summary>
    private string? PickLeisure(CastSpec spec, Personality p, DeterministicRandom rng, string? exclude)
    {
        var options = _content.Locations
            .Where(l => l.Visibility == LocationVisibility.Public
                        && !l.Tags.Contains("investigation")
                        && !spec.ExcludedLocationIds.Contains(l.Id)
                        && l.Id != spec.HomeLocationId
                        && l.Id != exclude)
            .ToList();
        if (options.Count == 0) return null;
        return WeightedPick(rng, options, l =>
            1 + (l.Tags.Contains("social_hub") ? p.Extraversion / 20 : 0)
              + (l.Tags.Contains("sanctuary") ? p.Conscientiousness / 25 + p.Agreeableness / 50 : 0)).Id;
    }

    // ---------------------------------------------------------------- helpers

    private static T WeightedPick<T>(DeterministicRandom rng, IReadOnlyList<T> items, Func<T, int> weight)
    {
        if (items.Count == 0) throw new InvalidOperationException("Nothing to pick from.");
        var total = 0;
        foreach (var i in items) total += weight(i);
        var roll = rng.NextInt(0, total);
        foreach (var i in items)
        {
            roll -= weight(i);
            if (roll < 0) return i;
        }
        return items[^1];
    }
}
