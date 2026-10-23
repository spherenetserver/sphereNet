using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Runes. A caster with Mark and Recall in its book keeps a small library of runes in
/// its pack: one for each town bank it stays at (its home town's first) and, for one
/// who hunts, the mouths of the dungeons it goes to. A blank rune is bought from a mage
/// shop like any player's (<c>shopping.txt</c>, the <c>rune</c> need) and marked on the
/// spot with the Mark spell through the bot's client - mana, reagents and skill as for
/// anyone; a rune worn out by use is marked again. The runes take it home (Recall) and
/// are where it can open a gate to for a player (Gate Travel). What a rune leads to is
/// read from its mark: the town of the nearest place within <see cref="RunePlaceRange"/>
/// tiles, else the dungeon's name, else the region's.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>The rune a bot of an older build was given; read no more (its runes
    /// are whatever its pack holds).</summary>
    public const string RuneTag = "PLAYERBOT.RUNE";

    /// <summary>Most runes a bot keeps marked.</summary>
    public int RuneCap { get; set; } = 8;
    /// <summary>Farthest a bot walks to a mage shop for blank runes.</summary>
    private const int RuneShopRange = 300;
    /// <summary>A rune marked within this many tiles of a place leads to it.</summary>
    private const int RunePlaceRange = 40;

    /// <summary>The runes in the bot's pack that are its own (not one held for a player).</summary>
    private List<Item> OwnRunes(Character ch)
    {
        var list = new List<Item>();
        if (ch.Backpack is not { } pack) return list;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == ItemType.Rune && HeldFor(ch, item) == 0)
                list.Add(item);
        return list;
    }

    /// <summary>A rune that takes one somewhere: marked and not worn out.</summary>
    private static bool IsUsableRune(Item rune) => rune.HasRuneMark && (int)rune.More1 > 0;

    private int UsableRuneCount(Character ch) => OwnRunes(ch).Count(IsUsableRune);

    /// <summary>Blank or worn-out runes in the pack: what can be marked.</summary>
    private int MarkableRuneCount(Character ch) => OwnRunes(ch).Count(r => !IsUsableRune(r));

    private static bool KnowsTravel(BotMind mind) =>
        mind.IsCaster && mind.Char.GetSkill(SkillType.Magery) >= 500;

    /// <summary>A caster that marks runes: Mark and Recall in its book.</summary>
    private static bool KeepsRunes(BotMind mind) =>
        KnowsTravel(mind) && HasSpell(mind.Char, SpellType.Mark) && HasSpell(mind.Char, SpellType.Recall);

    /// <summary>The place a mark leads to: the nearest destination within
    /// <see cref="RunePlaceRange"/> tiles of it on its map.</summary>
    internal BotDestination? RunePlace(Point3D mark)
    {
        BotDestination? best = null;
        int bestD = RunePlaceRange + 1;
        foreach (var d in Nav.Destinations)
        {
            if (d.Pos.Map != mark.Map) continue;
            int dd = d.Pos.GetDistanceTo(mark);
            // A town's place counts for its town; the dungeon mouth for the dungeon.
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    /// <summary>How a rune's place is said: the town, the dungeon, the region; empty
    /// when the mark is nowhere anyone has a name for.</summary>
    private string RuneWord(Item rune)
    {
        if (!rune.TryGetRuneMark(out var mark)) return "";
        var place = RunePlace(mark);
        if (place != null)
        {
            if (place.City.Length > 0) return place.City.ToLowerInvariant();
            if (place.Type.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase)) return ShortPlaceName(place.Name);
        }
        string? region = _world.FindRegion(mark)?.Name;
        return string.IsNullOrWhiteSpace(region) ? "" : region.ToLowerInvariant();
    }

    /// <summary>The places the bot's usable runes lead to, as said ("britain, trinsic").</summary>
    private List<string> RuneWords(Character ch)
    {
        var words = new List<string>();
        foreach (var rune in OwnRunes(ch))
        {
            if (!IsUsableRune(rune)) continue;
            string w = RuneWord(rune);
            if (w.Length > 0 && !words.Contains(w)) words.Add(w);
        }
        return words;
    }

    /// <summary>The usable rune of the bot's that leads where a player asked:
    /// <c>town:X</c> a rune of that town, <c>dest:X</c> one marked by that place, a place
    /// type one marked by such a place. Null when it has none.</summary>
    private Item? RuneFor(Character ch, string placeType)
    {
        foreach (var rune in OwnRunes(ch))
        {
            if (!IsUsableRune(rune) || !rune.TryGetRuneMark(out var mark)) continue;
            if (RuneLeadsTo(mark, placeType)) return rune;
        }
        return null;
    }

    private bool RuneLeadsTo(Point3D mark, string placeType)
    {
        if (placeType.StartsWith("town:", StringComparison.OrdinalIgnoreCase))
            return RunePlace(mark)?.City.Equals(placeType[5..], StringComparison.OrdinalIgnoreCase) == true;
        if (placeType.StartsWith("dest:", StringComparison.OrdinalIgnoreCase))
        {
            var dest = Nav.FindDestination(placeType[5..]);
            if (dest == null || dest.Pos.Map != mark.Map) return false;
            // A dungeon's mouth may be named by any of its levels' first room.
            return dest.Pos.GetDistanceTo(mark) <= RunePlaceRange ||
                   ShortPlaceName(dest.Name) == (RunePlace(mark) is { } p ? ShortPlaceName(p.Name) : "");
        }
        return RunePlace(mark) is { } place && place.Type.Equals(placeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bot's usable rune of its home town.</summary>
    private Item? HomeRune(BotMind mind) => RuneFor(mind.Char, "town:" + mind.Home);

    /// <summary>At a town's bank (or a dungeon's mouth, for a caster who hunts) it has no
    /// rune for, a caster marks a blank or worn-out rune there; its home town's bank
    /// first. One with nothing left to mark buys blank runes at a mage shop (see
    /// <see cref="FirstNeed"/>).</summary>
    private bool TryMarkRune(BotMind mind, long now)
    {
        if (IsRogueBot(mind.Char)) return false;
        var ch = mind.Char;
        if (mind.MarkCheckMs != 0 && now >= mind.MarkCheckMs)
        {
            mind.MarkCheckMs = 0;
            var marked = mind.MarkingRune;
            mind.MarkingRune = null;
            if (marked != null && !marked.IsDeleted && IsUsableRune(marked) && marked.TryGetRuneMark(out var at) &&
                at.GetDistanceTo(ch.Position) <= 2)
            {
                WindowMarksTaken++;
                mind.Note(now, $"its rune of {RuneWord(marked)} is marked");
            }
            else
            {
                WindowMarksFailed++;
                mind.Note(now, "the mark did not take");
                if (WindowMarksFailed <= 5)
                    _logger.LogInformation("[playerbots] {Name} mark did not take: {Trace}", ch.Name,
                        string.Join(" | ", mind.Trace().TakeLast(8).Select(t => t.Text)));
            }
        }
        if (mind.Client == null || !KeepsRunes(mind) || now < mind.NextMarkMs || mind.At is not { } here ||
            mind.Favor != null || ch.Mana < 20 || ch.TryGetCastingSpell(out _))
            return false;
        bool bank = here.Type.Equals("Bank", StringComparison.OrdinalIgnoreCase) && here.City.Length > 0;
        bool mouth = mind.IsFighter && here.Type.Equals("DungeonEntrance", StringComparison.OrdinalIgnoreCase);
        if (!bank && !mouth) return false;
        mind.NextMarkMs = now + 120_000;
        var runes = OwnRunes(ch);
        // Already has a way back here.
        foreach (var r in runes)
            if (IsUsableRune(r) && r.TryGetRuneMark(out var m) && m.Map == ch.MapIndex &&
                m.GetDistanceTo(ch.Position) <= 24)
                return false;
        int usable = runes.Count(IsUsableRune);
        bool home = bank && here.City.Equals(mind.Home, StringComparison.OrdinalIgnoreCase);
        // Room is kept for the home rune: a full library marks nothing else.
        if (usable >= RuneCap || (!home && usable >= RuneCap - 1 && HomeRune(mind) == null)) return false;
        // A worn-out rune of this very place first, then any blank one, then any worn-out one.
        Item? blank = runes.FirstOrDefault(r => r.HasRuneMark && !IsUsableRune(r) && r.TryGetRuneMark(out var m) &&
                                                m.Map == ch.MapIndex && m.GetDistanceTo(ch.Position) <= 24)
                      ?? runes.FirstOrDefault(r => !r.HasRuneMark)
                      ?? runes.FirstOrDefault(r => !IsUsableRune(r));
        if (blank == null) return false;
        mind.Client.HandleCastSpell(SpellType.Mark, blank.Uid.Value);
        mind.MarkingRune = blank;
        mind.MarkCheckMs = now + 8000;
        WindowMarks++;
        mind.Note(now, $"marks a rune at {here.Name}");
        mind.NextActMs = now + 4000;
        return true;
    }

    /// <summary>A caster with a rune of its home town recalls home. False when it cannot.</summary>
    private bool TryRecallHome(BotMind mind, long now, string why)
    {
        // A rune goes to a town bank; a player killer has no business there.
        if (IsPk(mind)) return false;
        var ch = mind.Char;
        if (mind.Client == null || !KnowsTravel(mind) || now < mind.NextRecallMs || ch.Mana < 11 ||
            ch.IsDead || ch.TryGetCastingSpell(out _) || !HasSpell(ch, SpellType.Recall))
            return false;
        var rune = HomeRune(mind);
        if (rune == null || !rune.TryGetRuneMark(out var mark)) return false;
        if (mark.Map == ch.MapIndex && mark.GetDistanceTo(ch.Position) < 64) return false; // walk it
        return Recall(mind, rune, now, $"home ({why})");
    }

    /// <summary>Recall on <paramref name="rune"/> through the bot's client.</summary>
    private bool Recall(BotMind mind, Item rune, long now, string why)
    {
        var ch = mind.Char;
        if (mind.Client == null || ch.Mana < 11 || ch.IsDead || ch.TryGetCastingSpell(out _) ||
            !HasSpell(ch, SpellType.Recall))
            return false;
        mind.NextRecallMs = now + 60_000;
        mind.RecallSinceMs = now;
        mind.Client.HandleCastSpell(SpellType.Recall, rune.Uid.Value);
        WindowRecalls++;
        mind.Note(now, $"recalls {why}");
        mind.Goal = null;
        ch.RemoveTag(GoalTag);
        mind.BeginLinger(null, now, _rng, short_: true);
        mind.NextActMs = now + 5000;
        return true;
    }
}
