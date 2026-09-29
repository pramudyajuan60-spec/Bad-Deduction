using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class SaveTests
{
    [Fact]
    public void Round_trip_preserves_the_exact_state()
    {
        var s = TestSupport.NewPopulatedSession(seed: 2024);
        TestSupport.Simulate(s, steps: 40);

        var json = SaveSystem.Serialize(s.State);
        var loaded = SaveSystem.Deserialize(json);

        Assert.Equal(SaveSystem.ComputeStateHash(s.State), SaveSystem.ComputeStateHash(loaded));
        Assert.Equal(json, SaveSystem.Serialize(loaded), "re-serializing a loaded save must be byte-identical");
        Assert.Equal(s.State.World.Characters.Count, loaded.World.Characters.Count);
        Assert.Equal(s.State.Truth.CharacterWithRole(HiddenRole.Malvr), loaded.Truth.CharacterWithRole(HiddenRole.Malvr));
    }

    [Fact]
    public void Loading_and_continuing_equals_never_having_saved()
    {
        var content = TestSupport.LoadContent();

        var straight = TestSupport.NewPopulatedSession(seed: 77, content: content);
        TestSupport.Simulate(straight, 30);
        TestSupport.Simulate(straight, 30);

        var first = TestSupport.NewPopulatedSession(seed: 77, content: content);
        TestSupport.Simulate(first, 30);
        var resumed = GameSession.FromState(SaveSystem.Deserialize(SaveSystem.Serialize(first.State)), content);
        TestSupport.Simulate(resumed, 30);

        Assert.Equal(straight.StateHash(), resumed.StateHash(), "RNG state must survive save/load");
    }

    [Fact]
    public void Same_seed_same_actions_give_identical_worlds_and_different_seeds_diverge()
    {
        var content = TestSupport.LoadContent();
        string Run(ulong seed)
        {
            var s = TestSupport.NewPopulatedSession(seed, content: content);
            TestSupport.Simulate(s, 50);
            return s.StateHash();
        }
        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public void File_round_trip_works_and_leaves_no_temp_file()
    {
        var content = TestSupport.LoadContent();
        var dir = Path.Combine(Path.GetTempPath(), "bd-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = TestSupport.NewPopulatedSession(content: content);
            TestSupport.Simulate(s, 10);
            var path = Path.Combine(dir, "slot1.json");

            s.Save(path);
            s.Save(path); // overwrite must also work
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = GameSession.Load(path, content);
            Assert.Equal(s.StateHash(), loaded.StateHash());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rejects_garbage_foreign_and_newer_saves()
    {
        Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize("not json"));
        Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize("[]"));
        Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize("{\"Game\":\"Other\",\"FormatVersion\":1}"));

        var json = SaveSystem.Serialize(TestSupport.NewPopulatedSession().State);
        var newer = json.Replace($"\"FormatVersion\":{SaveSystem.CurrentFormatVersion}", "\"FormatVersion\":999");
        var ex = Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize(newer));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void Rejects_saves_with_unknown_fields_or_broken_references()
    {
        var s = TestSupport.NewPopulatedSession();
        var json = SaveSystem.Serialize(s.State);

        Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize(json.Replace("\"Meta\":", "\"Surprise\":1,\"Meta\":")));

        s.State.Truth.HiddenRoles["c_nobody"] = HiddenRole.Malvr;
        var ex = Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize(SaveSystem.Serialize(s.State)));
        Assert.Contains("c_nobody", ex.Message);
    }

    [Fact]
    public void Loading_against_mismatched_content_is_rejected()
    {
        var s = TestSupport.NewPopulatedSession();
        var smaller = new BadDeduction.Content.ContentDatabase(s.Content.Locations.Take(3));
        var state = SaveSystem.Deserialize(SaveSystem.Serialize(s.State));
        Assert.Throws<SaveFormatException>(() => GameSession.FromState(state, smaller));
    }

    [Fact]
    public void Save_contains_no_wall_clock_data_so_identical_states_are_identical_bytes()
    {
        var a = TestSupport.NewPopulatedSession(seed: 9);
        var b = TestSupport.NewPopulatedSession(seed: 9);
        Assert.Equal(SaveSystem.Serialize(a.State), SaveSystem.Serialize(b.State));
    }
}
