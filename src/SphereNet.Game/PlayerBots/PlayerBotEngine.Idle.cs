using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// Standing about with a purpose. Where a player sees it, a bot at a place does what
/// people do there instead of shuffling in a circle: it stands by the banker with its
/// box open, sits on a free chair with a drink, looks over a vendor's wares, watches
/// the forge, practises on a training dummy or an archery butte (a double click: the
/// real practice), looks at the water from a dock, reads the bulletin board - and
/// otherwise stands mostly still, facing the place, with a bite or a small gesture now
/// and then.
///
/// What a place offers is looked up once (a bounded scan of the items, the map's own
/// furniture and the townsfolk about its middle) and kept for a couple of minutes for
/// every bot that stops there. Nothing here runs for a bot no player sees.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>How far about a place's middle its furniture is looked for.</summary>
    private const int PoiRange = 10;
    private const long PoiKeepMs = 120_000;
    /// <summary>Weapon skill (tenths) above which a dummy or a butte teaches nothing
    /// by default; a pack may let its own aids teach further, the bots keep to this.</summary>
    private const int PracticeSkillMax = 300;
    /// <summary>Weight of just standing there among a place's pastimes (a place with
    /// nothing to do leaves only this).</summary>
    public int IdleStandWeight { get; set; } = 12;

    /// <summary>What a place offers someone standing about it.</summary>
    private sealed class PlacePoi
    {
        public long UntilMs;
        public readonly List<Point3D> Seats = [];
        public readonly List<Point3D> Works = [];
        public readonly List<Item> Dummies = [];
        public readonly List<Item> Buttes = [];
        public readonly List<Item> Boards = [];
        public readonly List<Character> Bankers = [];
        public readonly List<Character> Keepers = [];
        public Point3D? Water;
    }

    private readonly Dictionary<BotDestination, PlacePoi> _poi = [];
    /// <summary>Seats a bot could not get to (behind a table, in a corner): not tried again.</summary>
    private readonly HashSet<(int X, int Y, byte Map)> _badSeats = [];

    // Furniture that is part of the map: forges and anvils by their art.
    private static bool IsForgeArt(int id) => id == 0x0FB1 || (id >= 0x197A && id <= 0x19A9);
    private static bool IsAnvilArt(int id) => id is 0x0FAF or 0x0FB0;

    private PlacePoi PoiOf(BotDestination at, long now)
    {
        if (_poi.TryGetValue(at, out var poi) && now < poi.UntilMs) return poi;
        _poi[at] = poi = new PlacePoi { UntilMs = now + PoiKeepMs };
        WindowPoiScans++;
        var mid = at.Pos;
        foreach (var item in _world.GetItemsInRange(mid, PoiRange))
        {
            if (item.IsDeleted || !item.IsOnGround || item.MapIndex != mid.Map || Math.Abs(item.Z - mid.Z) > 20) continue;
            switch (item.ItemType)
            {
                case ItemType.TrainDummy: poi.Dummies.Add(item); break;
                case ItemType.ArcheryButte: poi.Buttes.Add(item); break;
                case ItemType.BBoard: poi.Boards.Add(item); break;
                case ItemType.Forge or ItemType.Anvil: poi.Works.Add(item.Position); break;
                default:
                    if (SourceXWalk.ChairIds.Contains(item.BaseId)) poi.Seats.Add(item.Position);
                    break;
            }
        }
        foreach (var c in _world.GetCharsInRange(mid, PoiRange))
        {
            if (c.IsDeleted || c.IsDead || c.IsPlayer || c.MapIndex != mid.Map) continue;
            if (c.NpcBrain == NpcBrainType.Banker) poi.Bankers.Add(c);
            else if (c.NpcBrain is NpcBrainType.Healer or NpcBrainType.Stable || VendorEngine.IsVendorLike(c)) poi.Keepers.Add(c);
        }
        if (_world.MapData is { } map)
        {
            bool dock = at.Type.Equals("Dock", StringComparison.OrdinalIgnoreCase);
            int waterDist = int.MaxValue;
            var (w, h) = map.GetMapSize(mid.Map);
            for (int dy = -PoiRange; dy <= PoiRange; dy++)
                for (int dx = -PoiRange; dx <= PoiRange; dx++)
                {
                    int x = mid.X + dx, y = mid.Y + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    foreach (var s in map.GetStatics(mid.Map, x, y))
                    {
                        if (Math.Abs(s.Z - mid.Z) > 20) continue;
                        if (SourceXWalk.ChairIds.Contains(s.TileId))
                            poi.Seats.Add(new Point3D((short)x, (short)y, s.Z, mid.Map));
                        else if (IsForgeArt(s.TileId) || IsAnvilArt(s.TileId))
                            poi.Works.Add(new Point3D((short)x, (short)y, s.Z, mid.Map));
                    }
                    if (!dock) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (d >= waterDist || d < 2) continue;
                    var land = map.GetTerrainTile(mid.Map, x, y);
                    if (!map.GetLandTileData(land.TileId).IsWet) continue;
                    waterDist = d;
                    poi.Water = new Point3D((short)x, (short)y, land.Z, mid.Map);
                }
        }
        return poi;
    }

    /// <summary>The skill the weapon in hand trains, as the practice aids read it.</summary>
    private static SkillType PracticeSkill(Character ch)
    {
        var weapon = EquippedWeapon(ch);
        if (weapon == null || !weapon.IsWeaponType) return SkillType.Wrestling;
        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponMaceSharp => SkillType.Swordsmanship,
            ItemType.WeaponFence => SkillType.Fencing,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or ItemType.WeaponMacePick
                => SkillType.MaceFighting,
            ItemType.WeaponBow or ItemType.WeaponXBow => SkillType.Archery,
            ItemType.WeaponThrowing => SkillType.Throwing,
            _ => SkillType.Wrestling,
        };
    }

    /// <summary>The practice aid this bot would use here: a dummy for a blade or a
    /// fist, a butte for a bow; none for one past what an aid teaches, or in the saddle.</summary>
    private Item? PracticeAid(BotMind mind, PlacePoi poi)
    {
        var ch = mind.Char;
        if (mind.Client == null || ch.IsMounted || !mind.IsFighter) return null;
        var skill = PracticeSkill(ch);
        if (ch.GetSkill(skill) > PracticeSkillMax) return null;
        var aids = skill == SkillType.Archery ? poi.Buttes : skill == SkillType.Throwing ? null : poi.Dummies;
        return aids == null ? null : Nearest(aids, ch.Position, i => i.IsDeleted ? null : i.Position);
    }

    private static T? Nearest<T>(List<T> list, Point3D from, Func<T, Point3D?> where) where T : class
    {
        T? best = null;
        int bestDist = int.MaxValue;
        foreach (var x in list)
        {
            if (where(x) is not { } p || p.Map != from.Map) continue;
            int d = p.GetDistanceTo(from);
            if (d < bestDist) { bestDist = d; best = x; }
        }
        return best;
    }

    /// <summary>Choose what the bot does for this part of its stay, from what the place
    /// offers and the kind of place it is.</summary>
    private void PlanIdle(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        m.EndIdle();
        m.IdleAt = mind.At;
        m.IdleAnchor = mind.LingerAnchor;
        m.IdleUntilMs = now + _rng.Next(45_000, 110_000);
        m.Idle = BotIdleKind.Stand;
        if (mind.At is not { } at) return;
        var poi = PoiOf(at, now);
        bool Is(string type) => at.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
        bool shop = at.Type.StartsWith("Vendor", StringComparison.OrdinalIgnoreCase);
        bool inn = Is("Inn") || Is("Tavern");

        Point3D? seat = null;
        if (!ch.IsMounted)
        {
            int seatDist = int.MaxValue;
            foreach (var s in poi.Seats)
            {
                int d = s.GetDistanceTo(ch.Position);
                if (d >= seatDist || d > PoiRange + 4 || Math.Abs(s.Z - ch.Z) > 8 || SomeoneOn(s, ch) ||
                    _badSeats.Contains((s.X, s.Y, s.Map)))
                    continue;
                seat = s;
                seatDist = d;
            }
        }
        var banker = Is("Bank") ? Nearest(poi.Bankers, ch.Position, c => c.IsDeleted || c.IsDead ? null : c.Position) : null;
        var keeper = Nearest(poi.Keepers, ch.Position, c => c.IsDeleted || c.IsDead ? null : c.Position);
        var aid = PracticeAid(mind, poi);
        var board = Nearest(poi.Boards, ch.Position, i => i.IsDeleted ? null : i.Position);
        Point3D? work = null;
        foreach (var p in poi.Works)
            if (work is not { } best || p.GetDistanceTo(ch.Position) < best.GetDistanceTo(ch.Position)) work = p;

        var options = new List<(BotIdleKind Kind, int Weight)>(8) { (BotIdleKind.Stand, IdleStandWeight) };
        if (banker != null) options.Add((BotIdleKind.Banker, 70));
        if (seat != null) options.Add((BotIdleKind.Seat, inn ? 75 : 15));
        if (keeper != null) options.Add((BotIdleKind.Vendor, shop || inn || Is("Healer") || Is("Stables") ? 40 : 8));
        if (work != null) options.Add((BotIdleKind.Craft, Is("Forge") || Is("VendorSmith") || Is("VendorWeaponer") ? 45 : 8));
        if (aid != null) options.Add((BotIdleKind.Train, 60));
        if (poi.Water != null) options.Add((BotIdleKind.Water, 60));
        if (board != null && mind.Client != null) options.Add((BotIdleKind.Board, 20));
        m.Idle = WeightedPick(options, o => o.Weight).Kind;
        switch (m.Idle)
        {
            case BotIdleKind.Banker: m.IdleChar = banker; break;
            case BotIdleKind.Seat: m.IdleSpot = seat!.Value; break;
            case BotIdleKind.Vendor: m.IdleChar = keeper; break;
            case BotIdleKind.Craft: m.IdleSpot = work!.Value; break;
            case BotIdleKind.Train: m.IdleItem = aid; break;
            case BotIdleKind.Water: m.IdleSpot = poi.Water!.Value; break;
            case BotIdleKind.Board: m.IdleItem = board; break;
        }
        mind.Note(now, $"idles: {m.Idle}");
    }

    /// <summary>One act of a bot standing about where a player sees it. Always spends
    /// the act: a bot with nothing to do stands still.</summary>
    private void ActIdle(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        if (ActBackOff(mind, now)) return;
        // Talking with someone: it stands and looks at them.
        if (m.LookAt is { } who)
        {
            if (now < m.LookUntilMs && !who.IsDeleted && Within(who.Position, ch.Position, 12))
            {
                Face(mind, who.Position);
                mind.NextActMs = now + _rng.Next(1500, 3000);
                return;
            }
            m.LookAt = null;
        }
        if (m.Idle == BotIdleKind.None || m.IdleAt != mind.At || now >= m.IdleUntilMs ||
            !Within(m.IdleAnchor, mind.LingerAnchor, 0))
            PlanIdle(mind, now);
        // A rest is the default; each pastime sets its own when it does something.
        mind.NextActMs = now + _rng.Next(3000, 8000);
        switch (m.Idle)
        {
            case BotIdleKind.Banker: IdleAtBanker(mind, now); break;
            case BotIdleKind.Seat: IdleOnSeat(mind, now); break;
            case BotIdleKind.Vendor: IdleByKeeper(mind, now); break;
            case BotIdleKind.Craft: IdleLookAt(mind, m.IdleSpot, 2, "emote_smith", now); break;
            case BotIdleKind.Water:
                // Looked at from where the shore lets it stand.
                Face(mind, m.IdleSpot);
                IdleFidget(mind, now, "emote_dock");
                break;
            case BotIdleKind.Train: IdlePractise(mind, now); break;
            case BotIdleKind.Board: IdleReadBoard(mind, now); break;
            default: IdleStand(mind, now); break;
        }
    }

    /// <summary>One step toward where the bot means to stand. True while it walks;
    /// false once within <paramref name="reach"/> (<paramref name="there"/> set) or when
    /// the walk makes no ground and the pastime is dropped.</summary>
    private bool IdleWalk(BotMind mind, Point3D to, int reach, long now, out bool there)
    {
        var ch = mind.Char;
        int dist = Chebyshev(ch.Position, to);
        there = dist <= reach;
        if (there) return false;
        if (dist > PoiRange * 2 + 4 || mind.NoGainMs("idle", to, dist, now) > 8000)
        {
            mind.Manners.Idle = BotIdleKind.Stand;
            return false;
        }
        bool stepped = StepToward(mind, to, run: false, searchWithin: 14);
        mind.NextActMs = now + StepDelay(mind, stepped, false, now);
        return true;
    }

    /// <summary>The little things between: a bite out of the pack, a small gesture.</summary>
    private void IdleFidget(BotMind mind, long now, string? placeEmote)
    {
        var m = mind.Manners;
        // First time looked at: not everybody yawns the moment a player walks in.
        if (m.NextEmoteMs == 0) m.NextEmoteMs = now + _rng.Next(15_000, 120_000);
        if (m.NextEatMs == 0) m.NextEatMs = now + _rng.Next(60_000, 600_000);
        if (now >= m.NextEatMs)
        {
            TryEat(mind, now, drink: m.Idle == BotIdleKind.Seat);
            return;
        }
        if (now >= m.NextEmoteMs && _rng.Next(100) < 40)
            Emote(mind, placeEmote != null && _rng.Next(100) < 60 && Chat.Has(placeEmote) ? placeEmote : "emotes", now);
    }

    private void IdleStand(BotMind mind, long now)
    {
        var ch = mind.Char;
        // Mostly still, facing the place; a single step now and then.
        if (mind.At is { } at && !Within(at.Pos, ch.Position, 1) && _rng.Next(100) < 30)
            Face(mind, at.Pos);
        else if (_rng.Next(100) < 12)
            WanderNear(mind, mind.LingerAnchor, 2, run: false);
        IdleFidget(mind, now, null);
    }

    private void IdleAtBanker(BotMind mind, long now)
    {
        var m = mind.Manners;
        var banker = m.IdleChar;
        if (banker == null || banker.IsDeleted || banker.IsDead)
        {
            m.Idle = BotIdleKind.Stand;
            return;
        }
        if (IdleWalk(mind, banker.Position, 4, now, out bool there) || !there) return;
        Face(mind, banker.Position);
        // The box is opened the way players open it: said to the banker.
        if (!m.IdleDone && mind.BankStage == 0)
        {
            m.IdleDone = true;
            if (now - m.BankSaidMs > 60_000) SayBank(mind, now);
            return;
        }
        IdleFidget(mind, now, "emote_bank");
    }

    private void IdleOnSeat(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        var seat = m.IdleSpot;
        bool onIt = ch.X == seat.X && ch.Y == seat.Y;
        if (!onIt)
        {
            // Taken meanwhile, or in the saddle after all: another pastime.
            if (ch.IsMounted || SomeoneOn(seat, ch))
            {
                m.Idle = BotIdleKind.None;
                return;
            }
            IdleWalk(mind, seat, 0, now, out _);
            // No way onto it: nobody tries that seat again.
            if (m.Idle == BotIdleKind.Stand && _badSeats.Count < 4096) _badSeats.Add((seat.X, seat.Y, seat.Map));
            return;
        }
        if (!m.IdleDone)
        {
            // Seated (the client draws the pose for someone on a chair): it stays.
            m.IdleDone = true;
            m.IdleUntilMs = long.MaxValue;
            mind.LingerAnchor = ch.Position;
            m.IdleAnchor = ch.Position;
            WindowSeats++;
            mind.Note(now, $"sits at {seat}");
            if (m.NextEatMs == 0 || m.NextEatMs - now > 20_000) m.NextEatMs = now + _rng.Next(4000, 20_000);
        }
        mind.NextActMs = now + _rng.Next(5000, 10_000);
        IdleFidget(mind, now, "emote_sit");
    }

    private void IdleByKeeper(BotMind mind, long now)
    {
        var m = mind.Manners;
        var keeper = m.IdleChar;
        if (keeper == null || keeper.IsDeleted || keeper.IsDead)
        {
            m.Idle = BotIdleKind.Stand;
            return;
        }
        if (IdleWalk(mind, keeper.Position, 4, now, out bool there) || !there) return;
        Face(mind, keeper.Position);
        IdleFidget(mind, now, "emote_shop");
    }

    private void IdleLookAt(BotMind mind, Point3D spot, int reach, string emote, long now)
    {
        if (IdleWalk(mind, spot, reach, now, out bool there) || !there) return;
        Face(mind, spot);
        IdleFidget(mind, now, emote);
    }

    private void IdlePractise(BotMind mind, long now)
    {
        var ch = mind.Char;
        var m = mind.Manners;
        var aid = m.IdleItem;
        if (aid == null || aid.IsDeleted || mind.Client == null || ch.IsMounted)
        {
            m.Idle = BotIdleKind.Stand;
            return;
        }
        if (aid.ItemType == ItemType.ArcheryButte)
        {
            // A butte is shot at from a few paces, with the way to it clear.
            int dist = Chebyshev(ch.Position, aid.Position);
            if (dist < 3)
            {
                var away = aid.Position.GetDirectionTo(ch.Position);
                foreach (int turn in StraightFirst)
                    if (Move(mind, (Direction)(((int)away + turn) & 7), run: false)) break;
                mind.NextActMs = now + 500;
                return;
            }
            if (dist > 8 || !_world.CanSeeLOS(ch.Position, aid.Position))
            {
                IdleWalk(mind, aid.Position, 6, now, out _);
                return;
            }
        }
        else if (IdleWalk(mind, aid.Position, 1, now, out bool there) || !there)
            return;
        Face(mind, aid.Position);
        mind.Client.HandleDoubleClick(aid.Uid.Value);
        WindowPractice++;
        mind.NextActMs = now + _rng.Next(3200, 4600);
        if (_rng.Next(100) < 10) IdleFidget(mind, now, "emote_train");
    }

    private void IdleReadBoard(BotMind mind, long now)
    {
        var m = mind.Manners;
        var board = m.IdleItem;
        if (board == null || board.IsDeleted || mind.Client == null)
        {
            m.Idle = BotIdleKind.Stand;
            return;
        }
        if (IdleWalk(mind, board.Position, 2, now, out bool there) || !there) return;
        Face(mind, board.Position);
        if (!m.IdleDone)
        {
            m.IdleDone = true;
            mind.Client.HandleDoubleClick(board.Uid.Value);
            m.IdleUntilMs = Math.Min(m.IdleUntilMs, now + _rng.Next(12_000, 30_000));
            if (m.NextEmoteMs - now < 30_000 && Speak(mind, "emote_board", fallback: null, mode: EmoteMode))
                WindowEmotes++;
            return;
        }
        IdleFidget(mind, now, "emote_board");
    }

    /// <summary>A few steps away from a fight, then a look back at it.</summary>
    private bool ActBackOff(BotMind mind, long now)
    {
        var m = mind.Manners;
        if (m.BackOffTo is not { } from) return false;
        var ch = mind.Char;
        if (now >= m.BackOffUntilMs || from.Map != ch.MapIndex || Chebyshev(ch.Position, from) >= 7)
        {
            m.BackOffTo = null;
            mind.LingerAnchor = ch.Position;
            Face(mind, from);
            mind.NextActMs = now + _rng.Next(2000, 4000);
            return true;
        }
        var away = from.X == ch.X && from.Y == ch.Y ? (Direction)_rng.Next(8) : from.GetDirectionTo(ch.Position);
        bool stepped = false;
        foreach (int turn in new[] { 0, 1, -1, 2, -2 })
            if (Move(mind, (Direction)(((int)away + turn) & 7), run: m.BackOffRun))
            {
                stepped = true;
                WindowSteps++;
                break;
            }
        if (!stepped) m.BackOffUntilMs = now; // cornered: it stays and looks
        mind.NextActMs = now + StepDelay(mind, stepped, m.BackOffRun, now);
        return true;
    }
}
