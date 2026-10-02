using BadDeduction.Characters;

namespace BadDeduction.Content;

/// <summary>Data-driven job: where, when and who can hold it (data/occupations.json).</summary>
public sealed class OccupationDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CharacterKind Kind { get; set; }
    public string WorkLocationId { get; set; } = "";
    public int ShiftStartHour { get; set; }
    public int ShiftEndHour { get; set; }
    public int MinAge { get; set; }
    public int MaxAge { get; set; }
    public int Weight { get; set; } = 1;
    /// <summary>Every run gets at least this many holders (e.g. exactly one guard captain).</summary>
    public int MinCount { get; set; }
    public int MaxCount { get; set; } = int.MaxValue;
    public bool HasDayOff { get; set; } = true;
}

public enum GoalSlot { Primary, Secondary }

/// <summary>Which relationship a goal is aimed at, when it has a target.</summary>
public enum GoalTargetSource { Family, Friend }

public sealed class GoalDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public GoalSlot Slot { get; set; }
    public int Weight { get; set; } = 1;
    public List<string> Requires { get; set; } = new();
    public GoalTargetSource? TargetFrom { get; set; }
}

public sealed class SecretDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Severity { get; set; }
    public int Weight { get; set; } = 1;
}

public sealed class NameTable
{
    public List<string> Given { get; set; } = new();
    public List<string> Surnames { get; set; } = new();
}

public sealed class OccupationsFile { public List<OccupationDefinition> Occupations { get; set; } = new(); }
public sealed class GoalsFile { public List<GoalDefinition> Goals { get; set; } = new(); }
public sealed class SecretsFile { public List<SecretDefinition> Secrets { get; set; } = new(); }
