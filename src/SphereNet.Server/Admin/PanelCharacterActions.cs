using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Panel;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Server.Admin;

/// <summary>
/// The admin panel's character actions and detail view. Every action goes through
/// the engine path the same thing takes in game - SAY/EMOTE/KILL/RESURRECT/REVEAL
/// verbs, <see cref="Game.Objects.ObjBase.ExecuteVerbLine"/> for an arbitrary verb
/// line, the JAIL command's own jail/release - so script hooks, broadcasts and
/// death handling behave exactly as they do there.
///
/// The panel acts as the server console does in Source-X: the source of a verb is
/// the server (owner privilege, no character), and whatever the verb sends back to
/// its source (SRC.SYSMESSAGE) is collected into the answer's lines.
///
/// Main loop only.
/// </summary>
internal sealed class PanelCharacterActions
{
    private const int MaxOutputLines = 100;
    private const int MaxOutputLineLength = 500;

    private readonly GameWorld _world;
    private readonly Func<Character, ITextConsole?> _clientFor;
    private readonly Action<Character>? _selfRedraw;
    private readonly CommandHandler? _commands;

    /// <param name="world">The world characters are looked up in.</param>
    /// <param name="clientFor">The playing client of a character, or null offline.</param>
    /// <param name="selfRedraw">Redraws a character on its own client after a verb
    /// changed its body or hue (the observers' views pick it up on their own).</param>
    /// <param name="commands">The GM command handler, for jail; null = no jail.</param>
    public PanelCharacterActions(GameWorld world, Func<Character, ITextConsole?> clientFor,
        Action<Character>? selfRedraw = null, CommandHandler? commands = null)
    {
        _world = world;
        _clientFor = clientFor;
        _selfRedraw = selfRedraw;
        _commands = commands;
    }

    private Character? Find(uint serial)
    {
        if (serial == 0 || new Serial(serial).IsItem)
            return null;
        var ch = _world.FindChar(new Serial(serial));
        return ch == null || ch.IsDeleted ? null : ch;
    }

    /// <summary>Run one action. The request is expected to have passed the panel's
    /// validation; anything it lacks is refused here rather than guessed.</summary>
    public PlayerActionResult Execute(uint serial, PlayerActionRequest req)
    {
        var ch = Find(serial);
        if (ch == null)
            return PlayerActionResult.Missing(serial);

        var console = new PanelConsole();
        string name = ch.GetName();
        string text = req.Text ?? "";

        switch (req.Action)
        {
            case PlayerActions.Say:
                if (text.Length == 0) return PlayerActionResult.Fail("Text required.");
                // SAY sends the ASCII speech packet; a line the ASCII packet cannot
                // carry goes out through SAYU, the same verb in Unicode.
                ch.TryExecuteCommand(IsAscii(text) ? "SAY" : "SAYU", text, console);
                return Finish(console, true, $"{name} says: {text}");

            case PlayerActions.Emote:
                if (text.Length == 0) return PlayerActionResult.Fail("Text required.");
                ch.TryExecuteCommand("EMOTE", text, console);
                return Finish(console, true, $"{name} emotes: *{text}*");

            case PlayerActions.Message:
            {
                if (text.Length == 0) return PlayerActionResult.Fail("Text required.");
                var client = _clientFor(ch);
                if (client == null)
                    return PlayerActionResult.Fail($"{name} is not online.");
                if (req.Hue is { } hue)
                    client.SysMessage(text, (ushort)hue);
                else
                    client.SysMessage(text);
                return PlayerActionResult.Done($"Message sent to {name}.");
            }

            case PlayerActions.Verb:
                return RunVerb(ch, text, console);

            case PlayerActions.Heal:
                // The GM heal (.HEAL): a dead character is raised first, then
                // every pool is filled; poison is cured too.
                if (ch.IsDead)
                {
                    ch.TryExecuteCommand("RESURRECT", "", console);
                    if (ch.IsDead)
                        return Finish(console, false, $"{name} could not be resurrected.");
                }
                ch.CurePoison();
                ch.Hits = ch.MaxHits;
                ch.Mana = ch.MaxMana;
                ch.Stam = ch.MaxStam;
                return Finish(console, true, $"{name} healed.");

            case PlayerActions.Resurrect:
                if (!ch.IsDead)
                    return PlayerActionResult.Fail($"{name} is not dead.");
                ch.TryExecuteCommand("RESURRECT", "", console);
                return ch.IsDead
                    ? Finish(console, false, $"{name} could not be resurrected.")
                    : Finish(console, true, $"{name} resurrected.");

            case PlayerActions.Kill:
                if (ch.IsDead)
                    return PlayerActionResult.Fail($"{name} is already dead.");
                ch.TryExecuteCommand("KILL", "", console);
                // A script's @Death RETURN 1 keeps the character alive.
                return ch.IsDead
                    ? Finish(console, true, $"{name} killed.")
                    : Finish(console, false, $"{name} did not die (a script cancelled the death).");

            case PlayerActions.Freeze:
                ch.SetStatFlag(StatFlag.Freeze);
                return PlayerActionResult.Done($"{name} frozen.");

            case PlayerActions.Unfreeze:
                ch.ClearStatFlag(StatFlag.Freeze);
                return PlayerActionResult.Done($"{name} unfrozen.");

            case PlayerActions.Hide:
                ch.SetStatFlag(StatFlag.Hidden);
                return PlayerActionResult.Done($"{name} hidden.");

            case PlayerActions.Unhide:
                ch.TryExecuteCommand("REVEAL", "", console);
                return Finish(console, true, $"{name} revealed.");

            case PlayerActions.Teleport:
                return Teleport(ch, req);

            case PlayerActions.Jail:
                if (_commands == null)
                    return PlayerActionResult.Fail("Jail is not available.");
                int minutes = Math.Max(0, req.Minutes ?? 0);
                _commands.JailCharacter(_world, ch, minutes, 0);
                return PlayerActionResult.Done(minutes > 0
                    ? $"{name} jailed for {minutes} minute(s)."
                    : $"{name} jailed indefinitely.");

            case PlayerActions.Unjail:
                if (_commands == null)
                    return PlayerActionResult.Fail("Jail is not available.");
                if (!ch.TryGetTag("JAIL_RELEASE", out _))
                    return PlayerActionResult.Fail($"{name} is not jailed.");
                _commands.ReleaseJailedCharacter(_world, ch);
                return PlayerActionResult.Done($"{name} released from jail.");

            default:
                return PlayerActionResult.Fail($"Unknown action '{req.Action}'.");
        }
    }

    /// <summary>A verb line on the character, the way Source-X's r_Verb takes it
    /// (CObjBase::r_Verb): the verb table, then a script [FUNCTION] of that name,
    /// then a property assignment. <c>f_myfunc 5</c>, <c>HITS=100</c>,
    /// <c>GO 1000,1000</c> all work.</summary>
    private PlayerActionResult RunVerb(Character ch, string line, PanelConsole console)
    {
        ScriptCommandLine.Split(line, out string verb, out string args);
        if (verb.Length == 0)
            return PlayerActionResult.Fail("Verb required.");

        ushort bodyBefore = ch.BodyId;
        ushort hueBefore = ch.Hue.Value;

        bool ok = ch.ExecuteVerbLine(verb, args, console);

        // A move reaches every client through the world's move notification; a
        // new body or hue has to be redrawn on the character's own client.
        if (ok && !ch.IsDeleted && (ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore))
            _selfRedraw?.Invoke(ch);

        return Finish(console, ok, ok
            ? $"{verb} applied to {ch.GetName()}."
            : $"{verb} was not accepted by {ch.GetName()}.");
    }

    private PlayerActionResult Teleport(Character ch, PlayerActionRequest req)
    {
        if (req.X is not { } x || req.Y is not { } y)
            return PlayerActionResult.Fail("Teleport needs x and y.");
        int map = req.Map ?? ch.MapIndex;
        if (!_world.TryGetMapSize(map, out int width, out int height))
            return PlayerActionResult.Fail($"Map {map} is not loaded.");
        if (x < 0 || y < 0 || x >= width || y >= height)
            return PlayerActionResult.Fail($"{x},{y} is outside map {map} ({width}x{height}).");
        int z = req.Z ?? ch.Z;
        if (z is < sbyte.MinValue or > sbyte.MaxValue)
            return PlayerActionResult.Fail("z must be -128..127.");

        // The GO verb's move: the teleport effect shows, and the world's move
        // notification resyncs the character's own client.
        var dest = new Point3D((short)x, (short)y, (sbyte)z, (byte)map);
        if (!ch.TeleportWithEffect(dest) && !ch.Position.Equals(dest))
            return PlayerActionResult.Fail($"{ch.GetName()} could not be moved to {x},{y},{z},{map}.");
        return PlayerActionResult.Done($"{ch.GetName()} moved to {x},{y},{z},{map}.");
    }

    private static PlayerActionResult Finish(PanelConsole console, bool ok, string summary)
    {
        var lines = new List<string>(console.Lines.Count + 1);
        lines.AddRange(console.Lines);
        lines.Add(summary);
        return new PlayerActionResult(ok, lines);
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
            if (c > 0x7E) return false;
        return true;
    }

    // --- Detail --------------------------------------------------------------

    public PlayerDetail? Detail(uint serial)
    {
        var ch = Find(serial);
        if (ch == null)
            return null;

        var skills = new List<SkillValueInfo>();
        for (int i = 0; i < (int)SkillType.Qty; i++)
        {
            ushort value = ch.GetSkill((SkillType)i);
            if (value > 0)
                skills.Add(new SkillValueInfo(i, Enum.GetName((SkillType)i) ?? $"Skill{i}", value));
        }

        var allTags = ch.Tags.GetAll()
            .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tags = allTags
            .Take(PlayerDetail.MaxTags)
            .Select(t => new TagInfo(t.Key, t.Value.Length <= PlayerDetail.MaxTagValueLength
                ? t.Value
                : t.Value[..PlayerDetail.MaxTagValueLength] + "..."))
            .ToList();

        // Seen from itself, as the paperdoll colours it for its own player.
        byte noto = GameClient.ComputeNotoriety(_world, ch, ch);
        var pos = ch.Position;

        return new PlayerDetail(
            ch.Uid.Value,
            ch.GetName(),
            ch.Title,
            Character.ResolveAccountForChar?.Invoke(ch.Uid)?.Name ?? "",
            (int)ch.PrivLevel,
            _clientFor(ch) != null,
            ch.IsPlayer,
            ch.BodyId,
            pos.Map, pos.X, pos.Y, pos.Z,
            ch.Str, ch.Dex, ch.Int,
            ch.Hits, ch.MaxHits,
            ch.Mana, ch.MaxMana,
            ch.Stam, ch.MaxStam,
            ch.Fame, ch.Karma, ch.Kills,
            noto,
            NotorietyName(noto),
            ch.IsDead,
            ch.IsStatFlag(StatFlag.Freeze),
            ch.IsStatFlag(StatFlag.Hidden) || ch.IsStatFlag(StatFlag.Invisible),
            ch.IsPoisoned,
            ch.TryGetTag("JAIL_RELEASE", out _),
            skills,
            tags,
            allTags.Count > tags.Count);
    }

    /// <summary>The client's notoriety byte by name (UO NOTO_TYPE, 1..7).</summary>
    internal static string NotorietyName(byte noto) => noto switch
    {
        1 => "Innocent",
        2 => "Friend",
        3 => "Neutral",
        4 => "Criminal",
        5 => "Enemy",
        6 => "Murderer",
        7 => "Invulnerable",
        _ => "Unknown",
    };

    /// <summary>The panel as a verb's source: the server console of Source-X
    /// (owner privilege, no character), collecting what is sent back to it.</summary>
    internal sealed class PanelConsole : ITextConsole
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines => _lines;

        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "PANEL";

        public void SysMessage(string text)
        {
            if (_lines.Count >= MaxOutputLines || string.IsNullOrEmpty(text))
                return;
            _lines.Add(text.Length <= MaxOutputLineLength ? text : text[..MaxOutputLineLength] + "...");
        }
    }
}
