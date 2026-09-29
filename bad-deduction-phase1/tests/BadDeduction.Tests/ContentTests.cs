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
}
