using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Initiative;
using Godot;

namespace BadDeduction.Godot;

/// <summary>
/// Phase 13: 2D open-world presentation (top-down explorer). PRESENTATION ONLY.
/// This view NEVER writes sim state on its own: every mutation goes through the
/// sanctioned services — <c>World.MoveCharacter</c> (travel), <c>Simulate.Advance</c>
/// (time), <c>Dialogue.Exchange</c> / <c>Dialogue.OpeningLine</c> (talk), and
/// <c>Initiative.Evaluate</c> / <c>TryAccept</c> (NPC-initiated chats).
/// NPC placement and wander are pure functions of (run seed, location, game time);
/// nothing visual is ever written back into the session.
/// </summary>
public partial class World2DView : PanelContainer, IPanel
{
    private const float WorldW = 1600f;
    private const float WorldH = 1000f;
    private const float PlayerSpeed = 260f;
    private const float ProximityRadius = 96f;
    private const float ExitRadius = 48f;

    // Programmer-art palette note: civilians are UiTheme.Blue; police get a steel
    // blue-grey (no theme color fits a uniform, so this one is documented here).
    private static readonly Color PoliceColor = new("#7c8da6");
    private static readonly Color PlayerBody = new("#3a4358");

    private Main? _main;
    private Camera2D _camera = null!;
    private Node2D _worldRoot = null!;

    private Label _hudLocation = null!;
    private Label _hudClock = null!;
    private Label _prompt = null!;
    private Label _notice = null!;
    private ColorRect _flash = null!;

    private ActorNode _player = null!;
    private readonly Dictionary<string, ActorNode> _npcNodes = new(StringComparer.Ordinal);
    private readonly List<(string destId, Vector2 pos, int minutes)> _exits = new();
    private HashSet<string> _pendingIds = new(StringComparer.Ordinal);

    private string _locationId = "";
    private ulong _runSeed;
    private Vector2 _playerPos = new(WorldW / 2f, WorldH / 2f);
    private double _secondAccum;
    private bool _eLatch;
    private string? _nearestNpcId;
    private ulong _noticeUntilMsec;
    private double _trustAccum;

    /// <summary>Test hook: how many NPC actor nodes are currently placed.</summary>
    public int NpcNodeCount => _npcNodes.Count;

    public override void _Ready()
    {
        _main = GetNodeOrNull<Main>("/root/Main");

        var viewportHost = new SubViewportContainer
        {
            Stretch = true,
        };
        viewportHost.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(viewportHost);

        var viewport = new SubViewport
        {
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        viewportHost.AddChild(viewport);

        _camera = new Camera2D
        {
            Enabled = true,
            LimitLeft = 0,
            LimitTop = 0,
            LimitRight = (int)WorldW,
            LimitBottom = (int)WorldH,
            PositionSmoothingEnabled = true,
            PositionSmoothingSpeed = 8f,
        };
        viewport.AddChild(_camera);

        _worldRoot = new Node2D();
        viewport.AddChild(_worldRoot);

        BuildHud();
    }

    private void BuildHud()
    {
        var hud = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hud.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(hud);

        var topLeft = new VBoxContainer();
        topLeft.SetAnchorsPreset(LayoutPreset.TopLeft);
        topLeft.Position = new Vector2(12, 8);
        topLeft.AddThemeConstantOverride("separation", 0);
        topLeft.MouseFilter = Control.MouseFilterEnum.Ignore;
        hud.AddChild(topLeft);
        _hudLocation = UiTheme.HeaderLabel("", 18);
        _hudLocation.MouseFilter = Control.MouseFilterEnum.Ignore;
        topLeft.AddChild(_hudLocation);
        _hudClock = UiTheme.DimLabel("", 14);
        _hudClock.MouseFilter = Control.MouseFilterEnum.Ignore;
        topLeft.AddChild(_hudClock);

        _notice = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _notice.SetAnchorsPreset(LayoutPreset.CenterTop);
        _notice.OffsetLeft = -400;
        _notice.OffsetRight = 400;
        _notice.OffsetTop = 8;
        _notice.OffsetBottom = 40;
        _notice.AddThemeColorOverride("font_color", UiTheme.Red);
        _notice.AddThemeFontSizeOverride("font_size", 16);
        _notice.MouseFilter = Control.MouseFilterEnum.Ignore;
        _notice.Visible = false;
        hud.AddChild(_notice);

        _prompt = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _prompt.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _prompt.OffsetLeft = -400;
        _prompt.OffsetRight = 400;
        _prompt.OffsetTop = -64;
        _prompt.OffsetBottom = -32;
        _prompt.AddThemeColorOverride("font_color", UiTheme.Gold);
        _prompt.AddThemeFontSizeOverride("font_size", 17);
        _prompt.MouseFilter = Control.MouseFilterEnum.Ignore;
        _prompt.Visible = false;
        hud.AddChild(_prompt);

        var hint = UiTheme.DimLabel("WASD/arrows: move • E: talk • 1–9: panels • Tab: next panel • Esc: explore", 12);
        hint.SetAnchorsPreset(LayoutPreset.BottomRight);
        hint.OffsetLeft = -460;
        hint.OffsetRight = -12;
        hint.OffsetTop = -28;
        hint.OffsetBottom = -8;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.MouseFilter = Control.MouseFilterEnum.Ignore;
        hud.AddChild(hint);

        _flash = new ColorRect
        {
            Color = new Color(1, 1, 1),
            Modulate = new Color(1, 1, 1, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hud.AddChild(_flash);
    }

    public void Refresh()
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        var loc = s.World.GetCharacter("c_player").CurrentLocationId;
        if (loc != _locationId)
            BuildLocation(loc);
        else
            RefreshNpcs();
        RefreshHud();
        RefreshInitiatives();
        UpdateProximity();
    }

    /// <summary>Rebuilds the whole per-location procedural scene (floor, decor, exits, actors).</summary>
    public void BuildLocation(string locationId)
    {
        var g = GameController.Instance;
        if (!g.HasRun) return;
        var s = g.Session!;
        _locationId = locationId;
        _runSeed = s.State.Meta.RunSeed;

        foreach (var c in _worldRoot.GetChildren()) c.QueueFree();
        _npcNodes.Clear();
        _exits.Clear();
        _pendingIds.Clear();

        var rng = DeterministicRandom.Derive(_runSeed, "2d." + locationId);
        var locName = s.Content.GetLocation(locationId).Name;

        _worldRoot.AddChild(new ColorRect
        {
            Color = UiTheme.Bg,
            Position = Vector2.Zero,
            Size = new Vector2(WorldW, WorldH),
        });

        var decor = new ShapeLayer();
        int decorCount = rng.NextInt(6, 11);
        var decorColors = new[] { UiTheme.Panel, UiTheme.PanelLight, UiTheme.Inset };
        for (var i = 0; i < decorCount; i++)
        {
            var col = decorColors[rng.NextInt(0, decorColors.Length)];
            if (rng.Chance(0.5))
            {
                var r = new Rect2(
                    rng.NextInt(60, 1300), rng.NextInt(120, 800),
                    rng.NextInt(60, 260), rng.NextInt(50, 180));
                decor.Rects.Add((r, col));
            }
            else
            {
                decor.Circles.Add((new Vector2(rng.NextInt(80, 1520), rng.NextInt(140, 900)),
                    rng.NextInt(24, 90), col));
            }
        }
        // Gold-dim accent ring around the plaza centre + world border.
        decor.RingCenters.Add((new Vector2(WorldW / 2f, WorldH / 2f), 120f, UiTheme.GoldDim));
        decor.BorderRect = new Rect2(8, 8, WorldW - 16, WorldH - 16);
        _worldRoot.AddChild(decor);

        var banner = new Label
        {
            Text = locName,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(WorldW / 2f - 350, 20),
            Size = new Vector2(700, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        banner.AddThemeColorOverride("font_color", UiTheme.Gold);
        banner.AddThemeFontSizeOverride("font_size", 26);
        _worldRoot.AddChild(banner);

        foreach (var (destId, minutes) in s.Content.Neighbors(locationId)
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var marker = new ExitMarker
            {
                Minutes = minutes,
                DestName = s.Content.GetLocation(destId).Name,
                Position = EdgePosition(rng),
            };
            _worldRoot.AddChild(marker);
            _exits.Add((destId, marker.Position, minutes));
        }

        _playerPos = new Vector2(WorldW / 2f, WorldH / 2f + 140);
        _player = new ActorNode { Body = PlayerBody, Ring = UiTheme.Gold, ActorName = "You" };
        _player.Position = _playerPos;
        _worldRoot.AddChild(_player);
        _camera.Position = _playerPos;
        _camera.ResetSmoothing();

        RebuildNpcNodes();
        RefreshHud();
        RefreshInitiatives();
    }

    private static Vector2 EdgePosition(DeterministicRandom rng)
    {
        const float m = 70f;
        float along = 0.2f + (float)rng.NextDouble() * 0.6f;
        return rng.NextInt(0, 4) switch
        {
            0 => new Vector2(WorldW * along, m),
            1 => new Vector2(WorldW - m, WorldH * along),
            2 => new Vector2(WorldW * along, WorldH - m),
            _ => new Vector2(m, WorldH * along),
        };
    }

    private static (Vector2 Base, float Phase, float Speed) NpcVisualParams(
        ulong seed, string locationId, string npcId)
    {
        // Independent named stream per NPC: an NPC's look never shifts when the
        // cast at this location changes.
        var rng = DeterministicRandom.Derive(seed, $"2d.{locationId}.npc.{npcId}");
        return (new Vector2(rng.NextInt(140, 1461), rng.NextInt(180, 901)),
            (float)(rng.NextDouble() * Math.Tau),
            0.4f + (float)rng.NextDouble() * 0.6f);
    }

    private void RebuildNpcNodes()
    {
        var s = GameController.Instance.Session!;
        foreach (var id in _npcNodes.Keys) _npcNodes[id].QueueFree();
        _npcNodes.Clear();
        var ids = LiveNpcIds();
        foreach (var id in ids)
        {
            var c = s.World.GetCharacter(id);
            var node = new ActorNode
            {
                Body = c.Kind == CharacterKind.Police ? PoliceColor : UiTheme.Blue,
                Ring = UiTheme.Dim,
                ActorName = c.DisplayName,
                ShowTrustBar = true,
            };
            var (basePos, _, _) = NpcVisualParams(_runSeed, _locationId, id);
            node.Position = basePos;
            _worldRoot.AddChild(node);
            node.SetTrust(s.Social.View(id, "c_player").Trust);
            _npcNodes[id] = node;
        }
    }

    /// <summary>Refreshes every NPC node's trust bar from the NPC's trust toward
    /// the player. Called on rebuild and on a 0.5 s timer — never per frame.</summary>
    private void RefreshTrustBars()
    {
        var s = GameController.Instance.Session!;
        foreach (var (id, node) in _npcNodes)
            node.SetTrust(s.Social.View(id, "c_player").Trust);
    }

    private List<string> LiveNpcIds()
    {
        var s = GameController.Instance.Session!;
        return s.World.CharactersAt(_locationId)
            .Where(c => c.Id != "c_player" && c.IsAlive)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();
    }

    private void RefreshNpcs()
    {
        var ids = LiveNpcIds();
        if (!_npcNodes.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(ids))
            RebuildNpcNodes();
    }

    private void RefreshHud()
    {
        var s = GameController.Instance.Session!;
        _hudLocation.Text = s.Content.GetLocation(_locationId).Name;
        _hudClock.Text = $"{UiTheme.FmtTime(s.State.TotalMinutes)}  •  Alert: {s.State.Police.Alert}";
    }

    private void RefreshInitiatives()
    {
        var s = GameController.Instance.Session!;
        var pending = s.Initiative.Evaluate();
        _pendingIds = new HashSet<string>(pending.Select(p => p.NpcId), StringComparer.Ordinal);
        foreach (var (id, node) in _npcNodes)
            node.AlertVisible = _pendingIds.Contains(id);
    }

    public override void _Process(double delta)
    {
        var g = GameController.Instance;
        if (!g.HasRun || !Visible || _locationId == "") return;

        HandleMovement(delta);
        UpdateWander();
        UpdateProximity();
        CheckExits();
        TickClock(delta);
        UpdateNotice();
        UpdateTrustBars(delta);

        _camera.Position = _playerPos; // smoothing glides the follow
    }

    private void HandleMovement(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) dir.Y += 1;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) dir.X += 1;
        if (dir != Vector2.Zero)
            _playerPos += dir.Normalized() * PlayerSpeed * (float)delta;
        _playerPos.X = Mathf.Clamp(_playerPos.X, 40, WorldW - 40);
        _playerPos.Y = Mathf.Clamp(_playerPos.Y, 40, WorldH - 40);
        _player.Position = _playerPos;

        bool eDown = Input.IsPhysicalKeyPressed(Key.E);
        if (eDown && !_eLatch) OnInteract();
        _eLatch = eDown;
    }

    private void UpdateWander()
    {
        var s = GameController.Instance.Session!;
        float t = s.State.TotalMinutes;
        foreach (var (id, node) in _npcNodes)
        {
            var (basePos, phase, speed) = NpcVisualParams(_runSeed, _locationId, id);
            var off = new Vector2(
                Mathf.Cos(t * speed + phase),
                Mathf.Sin(t * speed * 1.3f + phase)) * 24f;
            node.Position = basePos + off;
        }
    }

    private void UpdateProximity()
    {
        var s = GameController.Instance.Session!;
        _nearestNpcId = null;
        float best = ProximityRadius;
        foreach (var (id, node) in _npcNodes)
        {
            float d = _playerPos.DistanceTo(node.Position);
            if (d < best)
            {
                best = d;
                _nearestNpcId = id;
            }
        }

        if (_nearestNpcId is null)
        {
            _prompt.Visible = false;
            return;
        }
        var name = s.View.PublicProfile(_nearestNpcId)?.DisplayName ?? _nearestNpcId;
        bool initiated = _pendingIds.Contains(_nearestNpcId);
        _prompt.Text = initiated ? $"E: Hear out {name} (!)" : $"E: Talk to {name}";
        _prompt.Visible = true;
    }

    private void OnInteract()
    {
        if (_nearestNpcId is null || _main is null) return;
        var g = GameController.Instance;
        var s = g.Session!;
        var npcId = _nearestNpcId;
        if (_pendingIds.Contains(npcId))
        {
            // NPC-initiated: accept their queued initiative; they speak first.
            if (s.Initiative.TryAccept(npcId, out var init) && init is not null)
            {
                var res = s.Dialogue.OpeningLine(npcId, "c_player", init);
                _main.OpenDialogueWith(npcId, res.ReplyText);
                return;
            }
        }
        g.SelectedNpcId = npcId;
        _main.ShowPanel("dialogue");
    }

    private void CheckExits()
    {
        var g = GameController.Instance;
        var s = g.Session!;
        foreach (var (destId, pos, minutes) in _exits)
        {
            if (_playerPos.DistanceTo(pos) > ExitRadius) continue;
            var evt = s.World.MoveCharacter("c_player", destId);
            if (evt is null)
            {
                ShowNotice("Access denied — sealed by police.");
                var push = (_playerPos - pos).Normalized();
                if (push == Vector2.Zero) push = Vector2.Down;
                _playerPos = pos + push * (ExitRadius + 50);
                _playerPos.X = Mathf.Clamp(_playerPos.X, 40, WorldW - 40);
                _playerPos.Y = Mathf.Clamp(_playerPos.Y, 40, WorldH - 40);
                _player.Position = _playerPos;
            }
            else
            {
                Flash();
                // Timed travel: the same Simulate.Advance the menu buttons use,
                // so the TimeAdvanced signal refreshes every panel afterwards.
                g.AdvanceMinutes(minutes);
            }
            break; // one exit per frame
        }
    }

    private void TickClock(double delta)
    {
        var g = GameController.Instance;
        if (g.IsRunOver || _main is null || _main.IsOverlayOpen())
        {
            _secondAccum = 0;
            return;
        }
        _secondAccum += delta;
        while (_secondAccum >= 1.0)
        {
            _secondAccum -= 1.0;
            g.AdvanceMinutes(Math.Max(1, g.MinutesPerSecond));
            if (g.IsRunOver) break; // resolution overlay took over; stop ticking
        }
    }

    private void ShowNotice(string text)
    {
        _notice.Text = text;
        _notice.Visible = true;
        _noticeUntilMsec = Time.GetTicksMsec() + 3000;
    }

    private void UpdateNotice()
    {
        if (_notice.Visible && Time.GetTicksMsec() >= _noticeUntilMsec)
            _notice.Visible = false;
    }

    /// <summary>Trust bars track live trust without per-frame cost.</summary>
    private void UpdateTrustBars(double delta)
    {
        _trustAccum += delta;
        if (_trustAccum < 0.5) return;
        _trustAccum = 0;
        if (_npcNodes.Count > 0)
            RefreshTrustBars();
    }

    private void Flash()
    {
        _flash.Modulate = new Color(1, 1, 1, 0.55f);
        var tw = CreateTween();
        tw.TweenProperty(_flash, "modulate:a", 0.0f, 0.35f);
    }

    /// <summary>Static background shapes drawn in one pass (programmer art).</summary>
    private sealed partial class ShapeLayer : Node2D
    {
        public readonly List<(Rect2 rect, Color color)> Rects = new();
        public readonly List<(Vector2 center, float radius, Color color)> Circles = new();
        public readonly List<(Vector2 center, float radius, Color color)> RingCenters = new();
        public Rect2 BorderRect;

        public override void _Draw()
        {
            foreach (var (r, c) in Rects) DrawRect(r, c);
            foreach (var (p, r, c) in Circles) DrawCircle(p, r, c);
            foreach (var (p, r, c) in RingCenters) DrawArc(p, r, 0, Mathf.Tau, 64, c, 2f);
            DrawRect(BorderRect, UiTheme.GoldDim, false, 2f);
        }
    }

    /// <summary>Gold exit marker: ring + destination name. Purely visual; travel
    /// itself goes through <c>WorldService.MoveCharacter</c>.</summary>
    private sealed partial class ExitMarker : Node2D
    {
        public int Minutes;
        public string DestName = "";

        public override void _Ready()
        {
            var l = new Label
            {
                Text = $"{DestName} ({Minutes}m)",
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Vector2(-110, 30),
                Size = new Vector2(220, 22),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            l.AddThemeColorOverride("font_color", UiTheme.GoldDim);
            l.AddThemeFontSizeOverride("font_size", 14);
            AddChild(l);
        }

        public override void _Draw()
        {
            DrawCircle(Vector2.Zero, 26, new Color(UiTheme.Gold, 0.15f));
            DrawArc(Vector2.Zero, 26, 0, Mathf.Tau, 40, UiTheme.Gold, 3f);
        }
    }

    /// <summary>One actor on the map: colored disc + ring, name tag, a hidden
    /// gold "!" shown when the NPC has a pending initiative, and (NPCs only) a
    /// small trust bar + number under the name tag showing the NPC's trust
    /// toward the player. Trust is refreshed on a 0.5 s timer, not per frame.</summary>
    private sealed partial class ActorNode : Node2D
    {
        public Color Body = UiTheme.Blue;
        public Color Ring = UiTheme.Gold;
        public string ActorName = "";
        private Label _alert = null!;
        private Label _trustLabel = null!;
        private int _trust = -1;

        /// <summary>True for NPC nodes; the player node has no trust bar.</summary>
        public bool ShowTrustBar { get; set; }

        public bool AlertVisible
        {
            get => _alert.Visible;
            set => _alert.Visible = value;
        }

        /// <summary>NPC's trust toward the player (0-100). Redraws the bar.</summary>
        public void SetTrust(int trust)
        {
            if (_trust == trust) return;
            _trust = trust;
            if (_trustLabel is not null)
            {
                _trustLabel.Text = trust.ToString();
                _trustLabel.Visible = ShowTrustBar;
            }
            QueueRedraw();
        }

        public override void _Ready()
        {
            var name = new Label
            {
                Text = ActorName,
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Vector2(-60, 24),
                Size = new Vector2(120, 20),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            name.AddThemeColorOverride("font_color", UiTheme.Text);
            name.AddThemeFontSizeOverride("font_size", 14);
            AddChild(name);

            _trustLabel = new Label
            {
                Text = _trust >= 0 ? _trust.ToString() : "",
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Vector2(-22, 52),
                Size = new Vector2(44, 16),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = ShowTrustBar,
            };
            _trustLabel.AddThemeColorOverride("font_color", UiTheme.Dim);
            _trustLabel.AddThemeFontSizeOverride("font_size", 11);
            AddChild(_trustLabel);

            _alert = new Label
            {
                Text = "!",
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Vector2(-30, -66),
                Size = new Vector2(60, 44),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
            };
            _alert.AddThemeColorOverride("font_color", UiTheme.Gold);
            _alert.AddThemeFontSizeOverride("font_size", 34);
            AddChild(_alert);
        }

        public override void _Draw()
        {
            DrawCircle(Vector2.Zero, 18, Body);
            DrawArc(Vector2.Zero, 18, 0, Mathf.Tau, 32, Ring, 3f);
            if (ShowTrustBar && _trust >= 0)
            {
                var bar = new Rect2(-22, 44, 44, 6);
                DrawRect(bar, new Color(0, 0, 0, 0.55f));
                var col = _trust >= 60 ? UiTheme.Green : _trust >= 30 ? UiTheme.Gold : UiTheme.Red;
                DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * _trust / 100f, bar.Size.Y)), col);
            }
        }
    }
}
