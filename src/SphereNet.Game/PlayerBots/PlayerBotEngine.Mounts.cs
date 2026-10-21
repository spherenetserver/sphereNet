using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Riding. Some bots ride (<c>mounts.txt</c>: the share of riders and the creatures,
/// by weight); which ones is decided once per bot and kept (PLAYERBOT.RIDER). A rider
/// without a mount gets one in a guarded town, as from the stables: the creature is
/// made from its CHARDEF, owned by the bot and mounted by a double click through the
/// bot's client, the way a player mounts. Knocked off by death, the bot walks back to
/// its own creature when it is near, else leaves it and gets another in town.
/// </summary>
public sealed partial class PlayerBotEngine
{
    public const string RiderTag = "PLAYERBOT.RIDER";
    public const string MountTag = "PLAYERBOT.MOUNT";

    private readonly List<(string DefName, int Weight)> _mounts = [];

    /// <summary>Percent of bots that ride.</summary>
    public int RiderPercent { get; set; }

    /// <summary>Read <c>mounts.txt</c>: <c>riders N</c> (percent) and
    /// <c>chardef weight</c> lines.</summary>
    public void LoadMounts(string dataDir)
    {
        string path = Path.Combine(dataDir, "mounts.txt");
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            int value = parts.Length > 1 && int.TryParse(parts[1], out int v) ? v : 1;
            if (parts[0].Equals("riders", StringComparison.OrdinalIgnoreCase))
                RiderPercent = Math.Clamp(value, 0, 100);
            else
                _mounts.Add((parts[0], Math.Max(1, value)));
        }
    }

    public void AddMount(string defName, int weight = 1) => _mounts.Add((defName, weight));

    /// <summary>Whether this bot rides, decided the first time it is asked.</summary>
    private bool IsRider(BotMind mind)
    {
        var ch = mind.Char;
        if (ch.TryGetTag(RiderTag, out string? r) && r != null) return r == "1";
        bool rides = _rng.Next(100) < RiderPercent;
        ch.SetTag(RiderTag, rides ? "1" : "0");
        return rides;
    }

    /// <summary>Every few seconds: a rider on foot walks to its own creature and
    /// mounts it, or gets a new one in town. True when this act was spent on it.</summary>
    private bool CheckMount(BotMind mind, long now)
    {
        if (_mounts.Count == 0 || mind.Client == null || now < mind.NextMountCheckMs) return false;
        mind.NextMountCheckMs = now + 5000;
        var ch = mind.Char;
        if (ch.IsDead || ch.IsMounted || mind.Mode is BotMode.Fight or BotMode.Flee or BotMode.Reclaim ||
            mind.Scene != null || !IsRider(mind))
            return false;

        // A fight that is over leaves no target behind: mounting refuses a rider
        // with one (Horse_Mount), and the bot would click its horse for ever.
        if (ch.FightTarget.IsValid)
            ch.FightTarget = Serial.Invalid;

        if (OwnMount(mind) is { } horse)
        {
            int d = ch.Position.GetDistanceTo(horse.Position);
            if (d <= 1)
            {
                mind.Client.HandleDoubleClick(horse.Uid.Value);
                if (ch.IsMounted)
                {
                    mind.MountTries = 0;
                    mind.ResetLeg(now);
                    return true;
                }
                // Refused (a low ceiling, a script): a few tries, then it is left.
                if (++mind.MountTries < 3) return true;
                AbandonMount(mind, horse, now, "would not be mounted");
                return false;
            }
            // Walking back to it is for a bot at leisure; one on its way somewhere
            // goes on and finds it, or another, when it stops.
            if (d <= 20 && mind.Mode != BotMode.Linger)
                return false;
            if (d <= 20)
            {
                bool stepped = StepToward(mind, horse.Position, run: true, searchWithin: 24);
                if (mind.NoGainMs("mount", horse.Position, ch.Position.GetDistanceTo(horse.Position), now) > 15_000)
                {
                    AbandonMount(mind, horse, now, "cannot be reached");
                    return false;
                }
                mind.NextMountCheckMs = now + 300;
                mind.NextActMs = now + StepDelay(mind, stepped, true, now);
                return true;
            }
            // Too far to go back for: it is left to its fate.
            _world.DeleteObject(horse);
            ch.RemoveTag(MountTag);
        }

        if (_world.FindRegion(ch.Position)?.IsGuarded != true) return false;
        return GiveMount(mind, now);
    }

    /// <summary>Leave its creature (out of reach, or it will not be ridden) and try for
    /// another in a minute.</summary>
    private void AbandonMount(BotMind mind, Character horse, long now, string why)
    {
        WindowGiveUps++;
        mind.Note(now, $"leaves its mount at {horse.Position}: {why}");
        _world.DeleteObject(horse);
        mind.Char.RemoveTag(MountTag);
        mind.MountTries = 0;
        mind.NextMountCheckMs = now + 60_000;
    }

    /// <summary>The creature this bot owns and rode, standing about unridden.</summary>
    private Character? OwnMount(BotMind mind)
    {
        var ch = mind.Char;
        if (!ch.TryGetTag(MountTag, out string? tag) || !uint.TryParse(tag, out uint uid)) return null;
        var horse = _world.FindChar(new Serial(uid));
        if (horse == null || horse.IsDeleted || horse.IsDead || horse.MapIndex != ch.MapIndex ||
            horse.IsStatFlag(Core.Enums.StatFlag.Ridden) || !horse.HasOwner(ch.Uid))
        {
            ch.RemoveTag(MountTag);
            return null;
        }
        return horse;
    }

    private bool GiveMount(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client!;
        string? defName = WeightedPick(_mounts, m => m.Weight).DefName;
        int defIndex = defName != null ? ResolveCharDef(defName) : -1;
        if (defIndex < 0) return false;
        var horse = client.CreateNpcFromDefinition(defIndex, defName!);
        if (!_world.PlaceCharacter(horse, ch.Position) || !horse.TryAssignOwnership(ch))
        {
            _world.DeleteObject(horse);
            return false;
        }
        ch.SetTag(MountTag, horse.Uid.Value.ToString());
        client.HandleDoubleClick(horse.Uid.Value);
        WindowMounts++;
        if (ch.IsMounted) mind.ResetLeg(now);
        return true;
    }

    public long WindowMounts { get; private set; }
}
