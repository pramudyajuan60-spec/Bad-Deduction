using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Autoload "Game": owns the single <see cref="GameSession"/> for the run and the shared
/// <see cref="ContentDatabase"/>. All panels read the simulation ONLY through the
/// sanctioned services (View, Cognition, Social, Investigate, Crime, Dialogue, Simulate,
/// Police) — never <c>State.Truth</c> or <c>Debug</c> (see docs/UI_RULES.md).
/// </summary>
public partial class GameController : Node
{
    public static GameController Instance { get; private set; } = null!;

    public GameSession? Session { get; private set; }
    public ContentDatabase? Content { get; private set; }
    public bool HasRun => Session is not null;

    private string _selectedNpcId = "";

    /// <summary>Dialogue backend for runs. Mock (default) is deterministic; Ollama is opt-in via the New Run panel.</summary>
    public DialogueProviderKind ProviderKind { get; set; } = DialogueProviderKind.Mock;
    /// <summary>Ollama model tag, used when <see cref="ProviderKind"/> is Ollama.</summary>
    public string OllamaModel { get; set; } = "llama3.1:8b";
    /// <summary>Ollama server base URL, used when <see cref="ProviderKind"/> is Ollama.</summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>2D explorer time scale: game minutes advanced per real second while walking.</summary>
    public int MinutesPerSecond { get; set; } = 1;

    /// <summary>
    /// NPC-initiated conversation opener, set by <c>Main.OpenDialogueWith</c> and
    /// consumed once by DialoguePanel.Refresh. Null when no opener is pending.
    /// </summary>
    public sealed class PendingOpener
    {
        public string NpcId { get; set; } = "";
        public string OpeningLine { get; set; } = "";
    }

    public PendingOpener? PendingNpcOpener { get; set; }

    /// <summary>Currently inspected NPC (shared across panels).</summary>
    public string SelectedNpcId
    {
        get => _selectedNpcId;
        set
        {
            if (_selectedNpcId != value)
            {
                _selectedNpcId = value;
                EmitSignal(SignalName.SelectionChanged);
            }
        }
    }

    [Signal] public delegate void RunStartedEventHandler();
    [Signal] public delegate void TimeAdvancedEventHandler();
    [Signal] public delegate void SelectionChangedEventHandler();

    public override void _Ready() => Instance = this;

    public string DataDir() => ProjectSettings.GlobalizePath("res://data");

    /// <summary>
    /// Starts a fresh vertical-slice run: generated cast (17 civilians + 5 police), the
    /// player as "You" (holding the campaign's genius role, known to the player), the
    /// opposing genius seeded among NPCs, and a Day-1 murder to investigate.
    /// </summary>
    public void NewRun(ulong seed, Campaign campaign, Difficulty difficulty)
    {
        Content = ContentDatabase.LoadFromDirectory(DataDir());
        var provider = DialogueProviderFactory.Create(ProviderKind, seed, OllamaModel, OllamaEndpoint);
        var session = GameSession.NewRun(seed, campaign, difficulty, Content, dialogueProvider: provider);
        // NOTE: Cast.Generate requires an EMPTY world — run it before adding the player.
        session.Cast.Generate(new CastSpec());
        session.World.AddCharacter(new CharacterState
        {
            Id = "c_player",
            DisplayName = "You",
            Age = 34,
            OccupationId = "consultant",
            Kind = CharacterKind.Civilian,
            HomeLocationId = "loc_residential",
            CurrentLocationId = "loc_residential",
        });
        session.World.SetPlayerCharacter("c_player");
        session.Identity.AssignHiddenRoles();
        session.Crime.GenerateIncident("murder");
        Session = session;
        SelectedNpcId = "";
        EmitSignal(SignalName.RunStarted);
    }

    public int CurrentDay => Session is null ? 1 : (int)(Session.State.TotalMinutes / GameTime.MinutesPerDay) + 1;

    /// <summary>The slice runs Day 1-7; past that the Resolution screen takes over.</summary>
    public bool IsRunOver => CurrentDay > 7;

    public void AdvanceMinutes(int minutes)
    {
        if (Session is null) return;
        Session.Simulate.Advance(minutes);
        EmitSignal(SignalName.TimeAdvanced);
    }

    public string SaveDir() => ProjectSettings.GlobalizePath("user://saves");

    public void SaveRun(string name)
    {
        if (Session is null) return;
        DirAccess.MakeDirRecursiveAbsolute(SaveDir());
        Session.Save(System.IO.Path.Combine(SaveDir(), name + ".json"));
    }

    public List<string> ListSaves()
    {
        var names = new List<string>();
        var dir = SaveDir();
        if (!DirAccess.DirExistsAbsolute(dir)) return names;
        foreach (var f in DirAccess.GetFilesAt(dir))
            if (f.EndsWith(".json", StringComparison.Ordinal))
                names.Add(f.Substring(0, f.Length - 5));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public void LoadRun(string name)
    {
        Content ??= ContentDatabase.LoadFromDirectory(DataDir());
        var path = System.IO.Path.Combine(SaveDir(), name + ".json");
        // The provider choice is UI-session state (not saved): re-read the seed so the
        // mock fallback inside an Ollama provider matches the run, like a fresh NewRun.
        var seed = SaveSystem.ReadFile(path).Meta.RunSeed;
        Session = GameSession.Load(path, Content,
            DialogueProviderFactory.Create(ProviderKind, seed, OllamaModel, OllamaEndpoint));
        SelectedNpcId = "";
        EmitSignal(SignalName.RunStarted);
    }
}
