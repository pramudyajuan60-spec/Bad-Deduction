using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class ContentTests
{
    [Fact]
    public void Shipped_location_data_is_valid_connected_and_matches_the_map_design()
    {
        var db = TestSupport.LoadContent();
        Assert.Equal(0, db.Validate().Count);
        Assert.Equal(11, db.Locations.Count);

        foreach (var id in new[]
                 {
                     "loc_central_market", "loc_residential", "loc_city_hall", "loc_church", "loc_warehouse",
                     "loc_tavern", "loc_guard_station", "loc_forest", "loc_cemetery", "loc_underground", "loc_harbor",
                 })
            Assert.True(db.HasLocation(id), $"missing {id}");
    }

    [Fact]
    public void Connections_are_undirected_with_consistent_travel_times()
    {
        var db = TestSupport.LoadContent();
        foreach (var loc in db.Locations)
            foreach (var (to, minutes) in db.Neighbors(loc.Id))
                Assert.Equal(minutes, db.Neighbors(to)[loc.Id]);
        Assert.Equal(6, db.Neighbors("loc_central_market")["loc_city_hall"]);
        Assert.Equal(6, db.Neighbors("loc_city_hall")["loc_central_market"]);
    }

    [Fact]
    public void Validation_catches_bad_data()
    {
        LocationDefinition L(string id, params (string to, int min)[] links) => new()
        {
            Id = id, Name = id, Connections = links.Select(l => new LocationConnection { To = l.to, Minutes = l.min }).ToList(),
        };

        Assert.True(new ContentDatabase(new[] { L("a", ("ghost", 5)) }).Validate().Any(e => e.Contains("unknown location")));
        Assert.True(new ContentDatabase(new[] { L("a"), L("a") }).Validate().Any(e => e.Contains("Duplicate")));
        Assert.True(new ContentDatabase(new[] { L("a"), L("b") }).Validate().Any(e => e.Contains("unreachable")));
        Assert.True(new ContentDatabase(new[] { L("a", ("b", 5)), L("b", ("a", 9)) }).Validate().Any(e => e.Contains("Conflicting")));
        Assert.True(new ContentDatabase(new[] { L("a", ("b", 0)), L("b") }).Validate().Any(e => e.Contains("non-positive")));
    }

    [Fact]
    public void Unknown_fields_in_data_files_fail_loudly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "locations.json"),
                "{\"locations\":[{\"id\":\"a\",\"name\":\"A\",\"visibilty\":\"Public\"}]}"); // typo
            Assert.Throws<System.Text.Json.JsonException>(() => ContentDatabase.LoadFromDirectory(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Travel_minutes_are_shortest_paths_and_symmetric()
    {
        var db = TestSupport.LoadContent();
        Assert.Equal(0, db.TravelMinutes("loc_church", "loc_church"));
        Assert.Equal(6, db.TravelMinutes("loc_central_market", "loc_city_hall"));
        // residential -> guard station has no direct road: it goes via market (8+7) or city hall (9+5).
        Assert.Equal(14, db.TravelMinutes("loc_residential", "loc_guard_station"));
        foreach (var a in db.Locations)
            foreach (var b in db.Locations)
                Assert.Equal(db.TravelMinutes(a.Id, b.Id), db.TravelMinutes(b.Id, a.Id));
        Assert.Throws<KeyNotFoundException>(() => db.TravelMinutes("loc_church", "nowhere"));
    }

    [Fact]
    public void Shipped_cast_content_is_valid_and_complete()
    {
        var db = TestSupport.LoadContent();
        Assert.Equal(0, db.Validate().Count);
        Assert.True(db.Occupations.Count >= 10 && db.Goals.Count >= 8 && db.Secrets.Count >= 4);
        Assert.True(db.Names.Given.Count >= 30 && db.Names.Surnames.Count >= 25);
        Assert.True(db.Occupations.Any(o => o.Kind == CharacterKind.Police));
    }

    [Fact]
    public void Cast_content_validation_catches_bad_data()
    {
        var loc = new LocationDefinition { Id = "a", Name = "A" };
        OccupationDefinition Occ(Action<OccupationDefinition> tweak)
        {
            var o = new OccupationDefinition { Id = "o", Name = "O", WorkLocationId = "a", ShiftStartHour = 8, ShiftEndHour = 16, MinAge = 20, MaxAge = 60 };
            tweak(o);
            return o;
        }
        bool Bad(OccupationDefinition o, string expected) =>
            new ContentDatabase(new[] { loc }, new[] { o }).Validate().Any(e => e.Contains(expected));

        Assert.True(Bad(Occ(o => o.WorkLocationId = "ghost"), "unknown location"));
        Assert.True(Bad(Occ(o => o.ShiftEndHour = 24), "invalid shift"));
        Assert.True(Bad(Occ(o => o.MinAge = 10), "age range"));
        Assert.True(Bad(Occ(o => { o.MinCount = 3; o.MaxCount = 2; }), "minCount"));
        Assert.True(new ContentDatabase(new[] { loc }, goals: new[] { new GoalDefinition { Id = "g", Requires = { "nonsense" } } })
            .Validate().Any(e => e.Contains("unknown tag")));
        Assert.True(new ContentDatabase(new[] { loc }, secrets: new[] { new SecretDefinition { Id = "s", Severity = 9 } })
            .Validate().Any(e => e.Contains("severity")));
    }
}
