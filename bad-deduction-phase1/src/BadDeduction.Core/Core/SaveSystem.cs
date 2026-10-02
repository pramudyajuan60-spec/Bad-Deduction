using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BadDeduction.Core;

public sealed class SaveFormatException : Exception
{
    public SaveFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class SaveEnvelope
{
    public string Game { get; set; } = SaveSystem.GameId;
    public int FormatVersion { get; set; } = SaveSystem.CurrentFormatVersion;
    public GameState State { get; set; } = new();
}

/// <summary>
/// Persists the full simulation state. There is intentionally no wall-clock timestamp inside the
/// save so identical states serialize to identical bytes (used for determinism checks).
/// Every schema change must bump <see cref="CurrentFormatVersion"/> and register a migration.
/// </summary>
public static class SaveSystem
{
    public const string GameId = "BadDeduction";
    public const int CurrentFormatVersion = 4;

    /// <summary>Migrations keyed by the version they upgrade FROM (operate on the raw JSON tree).</summary>
    private static readonly Dictionary<int, Action<JsonNode>> Migrations = new()
    {
        // v1 -> v2 (Phase 2): worlds gain character profiles, schedules and relationships.
        [1] = root =>
        {
            if (root["State"]?["World"] is not JsonObject world) return;
            world["Profiles"] ??= new JsonObject();
            world["Schedules"] ??= new JsonObject();
            world["Relationships"] ??= new JsonArray();
        },
        // v2 -> v3 (Phase 3): relationship edges gain the eight numeric axes. Old edges only knew their kind,
        // so they start at that kind's resting values (neutral personalities: v2 saves carry no social history).
        [2] = root =>
        {
            if (root["State"]?["World"]?["Relationships"] is not JsonArray edges) return;
            foreach (var node in edges)
            {
                if (node is not JsonObject edge) continue;
                var kind = Enum.TryParse<Social.RelationshipKind>(edge["Kind"]?.GetValue<string>(), out var k) ? k : Social.RelationshipKind.Acquaintance;
                var resting = Social.SocialBaselines.Resting(kind, null, null);
                foreach (var axis in Social.SocialRules.AllAxes)
                    edge[axis.ToString()] = resting[(int)axis];
            }
        },
        // v3 -> v4 (Phase 4): runs gain the cognition state (memories, beliefs, knowledge, journal).
        // Old saves get empty containers plus a valid cognition RNG stream derived from their run seed,
        // so post-migration rumor/distortion draws are deterministic and never touch the sim stream.
        [3] = root =>
        {
            if (root["State"] is not JsonObject state) return;
            if (state["Cognition"] is not null) return;
            var seed = state["Meta"]?["RunSeed"]?.GetValue<ulong>() ?? 0;
            var cognition = new Cognition.CognitionState
            {
                CognitionRng = DeterministicRandom.Derive(seed, "cognition.memory").Snapshot(),
            };
            state["Cognition"] = JsonSerializer.SerializeToNode(cognition, Compact);
        },
    };

    private static readonly JsonSerializerOptions Compact = CreateOptions(indented: false);
    private static readonly JsonSerializerOptions Indented = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        WriteIndented = indented,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(GameState state, bool indented = false) =>
        JsonSerializer.Serialize(new SaveEnvelope { State = state }, indented ? Indented : Compact);

    public static GameState Deserialize(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new SaveFormatException("Save file is not valid JSON.", ex); }

        if (root is not JsonObject obj) throw new SaveFormatException("Save file has no root object.");
        if (obj["Game"]?.GetValue<string>() != GameId) throw new SaveFormatException("Not a Bad Deduction save file.");

        var version = obj["FormatVersion"]?.GetValue<int>() ?? throw new SaveFormatException("Save file has no FormatVersion.");
        if (version > CurrentFormatVersion)
            throw new SaveFormatException($"Save format v{version} is newer than this build supports (v{CurrentFormatVersion}).");
        if (version < 1) throw new SaveFormatException($"Invalid save format version {version}.");

        for (; version < CurrentFormatVersion; version++)
        {
            if (!Migrations.TryGetValue(version, out var migrate))
                throw new SaveFormatException($"No migration from save format v{version}.");
            migrate(obj);
            obj["FormatVersion"] = version + 1;
        }

        SaveEnvelope? envelope;
        try { envelope = obj.Deserialize<SaveEnvelope>(Compact); }
        catch (JsonException ex) { throw new SaveFormatException("Save file does not match the expected schema: " + ex.Message, ex); }
        if (envelope is null) throw new SaveFormatException("Save file is empty.");

        var errors = GameStateValidator.Validate(envelope.State);
        if (errors.Count > 0)
            throw new SaveFormatException("Save file failed validation:\n - " + string.Join("\n - ", errors));
        return envelope.State;
    }

    /// <summary>Atomic write: a crash mid-save never corrupts the previous save.</summary>
    public static void WriteFile(string path, GameState state)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(state, indented: true), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static GameState ReadFile(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>SHA-256 over the canonical compact serialization; equal hashes mean equal simulation state.</summary>
    public static string ComputeStateHash(GameState state)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Compact));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
