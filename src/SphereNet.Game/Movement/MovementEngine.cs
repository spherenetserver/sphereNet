using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;

namespace SphereNet.Game.Movement;

/// <summary>
/// Movement engine. Maps to CClient::Event_Walk and CChar::CanMoveWalkTo in Source-X.
/// Validates movement requests, checks collision, stamina, and speed.
/// </summary>
public sealed class MovementEngine
{
    private readonly World.GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private readonly WalkCheck _walkCheck;

    /// <summary>Optional SpellEngine for interrupting casts on movement.</summary>
    public SpellEngine? SpellEngine { get; set; }

    /// <summary>Fired when a character is teleported (telepad/moongate step-on).
    /// Program.cs wires this to send DrawPlayer + resync to the client.</summary>
    public Action<Objects.Characters.Character, Point3D, byte>? OnTeleport { get; set; }

    /// <summary>Source-X CClient::SysMessage hook used by region enter/leave
    /// announcements. Program.cs wires this so the moving character receives
    /// MSG_REGION_ENTER / MSG_REGION_GUARDED / MSG_REGION_PVPSAFE strings on
    /// the matching client only.</summary>
    public Action<Objects.Characters.Character, string>? OnSysMessage { get; set; }

    /// <summary>Optional housing ban check. Returns false if character cannot enter the tile.</summary>
    public Func<Objects.Characters.Character, Point3D, bool>? CanEnterHouse { get; set; }

    /// <summary>Optional ship-boarding ban check (enforced through the ship region).
    /// Returns false if the character is barred from the ship occupying the tile.</summary>
    public Func<Objects.Characters.Character, Point3D, bool>? CanBoardShip { get; set; }

    /// <summary>Source-X CChar::Use_Item for an item the character stepped onto (a
    /// step-activated switch). Program.cs routes it through the mover's client so the
    /// switch's LINK chain runs; returns false when nobody took it (no client), and the
    /// engine then just flips the switch graphic (CItem::SetSwitchState).</summary>
    public Func<Objects.Characters.Character, Objects.Items.Item, bool>? OnStepUseItem { get; set; }

    public static int WalkDelayFoot { get; set; } = 400;
    public static int WalkDelayMount { get; set; } = 200;
    public static int RunDelayFoot { get; set; } = 200;
    public static int RunDelayMount { get; set; } = 100;

    /// <summary>STAMINALOSSATWEIGHT — the load percent at which a step costs stamina
    /// half the time. See <see cref="SphereNet.Core.Configuration.SphereConfig"/> for
    /// the contract; 200 switches the effect off.</summary>
    public static int StaminaLossAtWeight { get; set; } = 150;

    /// <summary>STAMINALOSSOVERWEIGHT — stamina charged on EVERY step taken over the
    /// carry weight, before the per-5-stones growth and the mounted third.</summary>
    public static int StaminaLossOverweight { get; set; } = 5;

    /// <summary>RUNNINGPENALTY — points added to the load percent while flying or
    /// hovering (the reference checks the flags, not running).</summary>
    public static int RunningPenalty { get; set; } = 50;

    /// <summary>RUNNINGPENALTYOVERWEIGHT — percent uplift on the overweight step cost
    /// while flying or hovering.</summary>
    public static int RunningPenaltyOverweight { get; set; } = 100;

    /// <summary>MEDITATIONMOVEMENTABORT — whether a step cancels meditation. Upstream's
    /// default is OFF: a meditating character may walk.</summary>
    public static bool MeditationMovementAbort { get; set; }

    /// <summary>NPCSHOVENPC — whether one creature may push past another. Default OFF;
    /// an individual may still be excused with TAG.OVERRIDE.SHOVE.</summary>
    public static bool NpcShoveNpc { get; set; }

    /// <summary>The die a weight-loss chance is rolled against, and the source of it.
    /// Tests replace it to make the S-curve's verdict observable rather than
    /// occasional.</summary>
    internal static Func<int, int> WeightLossRoll { get; set; } = DefaultWeightLossRoll;

    private static int DefaultWeightLossRoll(int max) => Random.Shared.Next(max);

    /// <summary>Put the die back (test teardown).</summary>
    public static void ResetWeightLossRoll() => WeightLossRoll = DefaultWeightLossRoll;

    public MovementEngine(World.GameWorld world, TriggerDispatcher? triggerDispatcher = null)
    {
        _world = world;
        _triggerDispatcher = triggerDispatcher;
        _walkCheck = new WalkCheck(world);
        if (triggerDispatcher != null)
            world.OnRegionTransition = FireRegionTransition;
    }

    /// <summary>
    /// Validate and execute a movement request.
    /// Maps to CClient::Event_Walk flow.
    /// Returns true if movement succeeded.
    /// </summary>
    public bool TryMove(Objects.Characters.Character ch, Direction dir, bool running, byte sequence)
    {
        bool moved = TryMoveDetailed(ch, dir, running, sequence, out _);
        // Load-profile counter (PLAN-701): one interlocked add at the single
        // public entry, so a soak run can say how much walking it actually did
        // and how much of it the walk check turned away.
        Diagnostics.LoadProfile.CountMove(moved);
        return moved;
    }

    /// <summary>Same as <see cref="TryMove"/> but returns a
    /// <see cref="WalkCheck.Diagnostic"/> describing which stage of the
    /// movement algorithm accepted/rejected the step. Used by the walk-reject
    /// log.</summary>
    public bool TryMoveDetailed(Objects.Characters.Character ch, Direction dir, bool running,
        byte sequence, out WalkCheck.Diagnostic diag)
    {
        diag = default;
        // Source-X CanMove (CCharAct.cpp:4571): a character in GM mode skips the
        // whole freeze test - FREEZE, STONE, NoMoveTill and freeze-on-cast alike -
        // so a staff member is never rooted by a script's freeze.
        bool gmMode = ch.PrivLevel >= PrivLevel.GM;
        // Source-X OnFreezeCheck: NoMoveTill is a world-clock deadline in tenths.
        // Expiration does not delete the script-owned tag.
        if (!gmMode && ch.TryGetTag("NOMOVETILL", out string? noMoveText) &&
            ScriptNumber.TryParseToken(noMoveText, out long noMoveTill) && noMoveTill > _world.GameClockMs / 100)
            return false;
        // IsDead is intentionally NOT a hard reject here. Source-X /
        // OSI ghosts can walk freely (just slower, can't open most doors,
        // can't mount). Treating death as "cannot move" leaves the player
        // stuck in place after dying, which manifests in the death log as
        // "client receives 0x2C death status, draws ghost body, then sends
        // no walk packets". We still block Freeze (paralyze, GM .freeze)
        // and Stone (stone form / petrified) since those are explicit
        // immobility states even on living characters.
        if (!gmMode &&
            (ch.IsStatFlag(StatFlag.Freeze) || ch.IsStatFlag(StatFlag.Stone) ||
             (CharDefHelper.GetCanFlags(ch) & (CanFlags.C_NonMover | CanFlags.C_Statue)) != 0))
            return false;

        // A cast that roots the caster (MAGICF_FREEZEONCAST / SPELLFLAG_FREEZEONCAST)
        // refuses the STEP; it does not cancel the spell. Source-X weighs this in
        // OnFreezeCheck alongside paralyze (CCharAct.cpp:4539).
        if (!gmMode && SpellEngine?.IsMovementFrozenByCast(ch) == true)
            return false;

        // Overweight running prevention — can't run when carrying more than max weight
        if (running && ch.IsPlayer && ch.GetTotalWeight() > ch.MaxWeight)
            running = false;

        var current = new Point3D(ch.X, ch.Y, ch.Z, ch.MapIndex);

        Point3D target;
        bool shoved = false;
        GetDirectionDelta(dir, out short dx, out short dy);

        // GM with AllMove, or an uninitialized world (no MapData — unit tests
        // and in-memory fixtures) bypass the full terrain algorithm: nothing
        // can block the step. The Z still follows the ground — freezing it let
        // a GM cross a dungeon at a stale height (the client computes its own
        // walk Z, so the drift stayed invisible until a self-redraw snapped
        // the char upward: the 5146,993 report, stored Z=10 over a floor at 1).
        if ((ch.PrivLevel >= PrivLevel.GM && ch.AllMove) || CharDefHelper.CanPassWalls(ch) || _world.MapData == null)
        {
            // The bypass skips collision ONLY — surface collection and Z
            // selection still run through the shared resolver (audit design:
            // GetEffectiveZ's closest-to-currentZ pick was self-reinforcing
            // for a drifted Z and saw neither multis nor headroom).
            var stand = _walkCheck.ResolveStandingSurface(ch, ch.MapIndex,
                ch.X + dx, ch.Y + dy, ch.Z, WalkCheck.StandingPolicy.IgnoreCollision);
            sbyte bypassZ = stand.Found ? stand.Z : ch.Z;
            target = new Point3D((short)(ch.X + dx), (short)(ch.Y + dy), bypassZ, ch.MapIndex);
            // Even the bypass branch may not step off the map — an off-map
            // char crashes the map readers on the next query.
            if (_world.GetSector(target) == null)
                return false;
        }
        else
        {
            if (!_walkCheck.CheckMovementDetailed(ch, current, dir, out int newZ, out diag))
                return false;

            target = new Point3D((short)(ch.X + dx), (short)(ch.Y + dy), (sbyte)newZ, ch.MapIndex);

            // Blocking mobiles at destination — WalkCheck already covers
            // ground-plane blockers; fall back to the existing shove rule for
            // anything it doesn't cover (mounted riders, invisible staff, etc.).
            foreach (var other in _world.GetCharsInRange(target, 0))
            {
                if (other == ch || other.IsDead) continue;
                if (other.X != target.X || other.Y != target.Y) continue;
                if (!CanShove(ch, other))
                {
                    diag = diag with { MobBlocked = true };
                    return false;
                }
                // @PersonalSpace on the one walked into, then @charShove on the
                // mover (ShoveCharAtPosition, CCharAct.cpp:4640-4658); either
                // RETURN 1 keeps the mover out.
                if (Character.OnPersonalSpace?.Invoke(other, ch) == true ||
                    Character.OnCharShove?.Invoke(ch, other) == true)
                {
                    diag = diag with { MobBlocked = true };
                    return false;
                }
                // A living blocker we pushed past = a real shove.
                shoved = true;
            }
        }

        if (CanEnterHouse != null && !CanEnterHouse(ch, target))
        {
            diag = diag with { MobBlocked = true };
            return false;
        }

        if (CanBoardShip != null && !CanBoardShip(ch, target))
        {
            diag = diag with { MobBlocked = true };
            return false;
        }

        ch.Direction = dir;

        // No spell interruption here: the reference lets a caster walk (the
        // rooting case was refused above). Meditation is a separate question, and
        // upstream makes it a setting whose DEFAULT is the permissive one: only
        // MEDITATIONMOVEMENTABORT fails the skill on a step (CCharAct.cpp:2495).
        // Cancelling it unconditionally was the stricter rule, not the reference one.
        if (MeditationMovementAbort)
            ch.InterruptMeditation();

        if (ch.HasActiveSkillPending() &&
            SkillEngine.HasFlag((SkillType)ch.SkillPendingId, SkillFlag.Immobile))
        {
            int skillId = ch.ClearActiveSkillPending();
            if (skillId >= 0)
                Character.ActiveSkillAborted?.Invoke(ch, skillId);
        }

        // @Falling: a step that drops ten or more (CanMoveWalkTo, CCharAct.cpp:4778).
        // ARGN1..3 = where the character lands.
        if (ch.Z - 10 >= target.Z && _triggerDispatcher != null &&
            _triggerDispatcher.IsCharTriggerUsed(CharTrigger.Falling))
        {
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Falling,
                new TriggerArgs { CharSrc = ch, N1 = target.X, N2 = target.Y, N3 = target.Z });
        }

        // Region scripts run centrally for walking and every teleport path.
        var previousRegion = _world.FindRegion(ch.Position);
        if (!_world.MoveCharacter(ch, target)) return false;

        // Shove cost — applied once here (not in the two shove predicates) so a
        // player who pushes past a mobile spends 10 stamina and is revealed,
        // exactly once, only when the move actually commits.
        if (shoved && ch.PrivLevel < PrivLevel.Counsel && ch.MaxStam > 0)
        {
            ch.Stam = (short)Math.Max(0, ch.Stam - 10);
            // Walking into somebody gives YOU away, and REVEALF_OSILIKEPERSONALSPACE
            // is the flag that says not to - it is one of the three whose name means
            // the opposite of its neighbours (CCharAct.cpp:4679).
            ch.ClearHiddenState(RevealFlags.OsiLikePersonalSpace);
        }

        // What the load costs. Walking on foot has no per-step cost of its own -
        // Event_Walk really does charge nothing - but CARRYING does, and the charge
        // lives one level down, in CanMoveWalkTo's committed branch
        // (CCharAct.cpp:4787-4829). Reading only Event_Walk is how this engine
        // concluded there was no cost at all and shipped BACKPACKOVERLOAD=40 with
        // nothing to pay for it.
        ApplyWeightStaminaCost(ch);

        TickStealthStep(ch);

        // Region/item step effects. A region or room @Step RETURN 1 refuses the
        // step after the fact: upstream puts the walker back where it stood and
        // rejects the move (Event_Walk, CClientEvent.cpp:894-900 - SetUnkPoint, a
        // raw reposition that runs no region triggers).
        if (!CheckLocationEffects(ch, target, previousRegion))
        {
            if (!ch.IsDeleted && ch.Position.Equals(target))
            {
                _world.MoveCharacter(ch, current, fireRegionEvents: false);
                var standing = _world.FindRegion(current);
                ch.SetTag("CURRENT_REGION", standing?.Name ?? "");
                ch.SetTag("CURRENT_REGION_UID", standing?.Uid.ToString() ?? "");
            }
            return false;
        }

        return true;
    }

    /// <summary>
    /// Charge a committed step for what the character is carrying (Source-X
    /// CCharAct.cpp:4787-4829).
    ///
    /// Two branches, and they are not variations of one another. UNDER the carry
    /// weight it is a CHANCE: the load percent is measured against
    /// STAMINALOSSATWEIGHT through the same S-curve a skill roll uses, and a step that
    /// loses the roll costs a single point. OVER it the cost is CERTAIN and it grows -
    /// STAMINALOSSOVERWEIGHT plus one for every five stones past the limit, a third of
    /// that when mounted. Flying or hovering makes the first branch likelier and the
    /// second dearer.
    ///
    /// Upstream charges this inside the !fCheckOnly arm, after the step is decided, so
    /// a probe or a pathfinding look-ahead is free; a GM returns before reaching it.
    /// </summary>
    private static void ApplyWeightStaminaCost(Objects.Characters.Character ch)
    {
        if (ch.PrivLevel >= PrivLevel.GM || ch.MaxStam <= 0)
            return;

        int maxWeight = ch.MaxWeight;
        if (maxWeight <= 0)
            return;

        int weight = ch.GetTotalWeight();
        bool airborne = ch.IsStatFlag(StatFlag.Fly) || ch.IsStatFlag(StatFlag.Hovering);
        int penalty;

        if (weight < maxWeight)
        {
            int loadPercent = weight * 100 / maxWeight;
            if (airborne)
                loadPercent += RunningPenalty;

            // The midpoint is the setting, the variance is upstream's fixed 10 - a
            // narrow curve, so the chance climbs steeply either side of it. At the
            // default 150 an ordinary load never gets near paying.
            int chance = Skills.SkillEngine.CalcSCurve(loadPercent - StaminaLossAtWeight, 10);
            penalty = chance > WeightLossRoll(1000) ? 1 : 0;
        }
        else
        {
            penalty = StaminaLossOverweight + (weight - maxWeight) / 5;
            if (ch.IsStatFlag(StatFlag.OnHorse))
                penalty /= 3;
            if (airborne)
                penalty += penalty * RunningPenaltyOverweight / 100;
        }

        if (penalty > 0)
            ch.Stam = (short)Math.Max(0, ch.Stam - penalty);
    }

    /// <summary>
    /// Check if a character can walk to the given adjacent position.
    /// Used by pathfinding / AI / teleporters that already know the target
    /// tile. Delegates to the ServUO movement algorithm for consistency with
    /// player walk packets.
    /// </summary>
    public bool CanWalkTo(Objects.Characters.Character ch, Point3D target)
    {
        if (ch.PrivLevel >= PrivLevel.GM && ch.AllMove)
            return true;
        if (CharDefHelper.CanPassWalls(ch))
            return true;

        if (target.X < 0 || target.Y < 0)
            return false;

        // Derive direction from delta; non-adjacent tiles are not walkable.
        int dx = target.X - ch.X;
        int dy = target.Y - ch.Y;
        if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || (dx == 0 && dy == 0))
            return false;

        // In-memory / unit-test fixtures without loaded map data — accept any
        // adjacent tile so long as no character blocks it. The ServUO algorithm
        // cannot run without terrain + statics.
        if (_world.MapData == null)
        {
            foreach (var other in _world.GetCharsInRange(target, 0))
            {
                if (other == ch || other.IsDead) continue;
                if (other.X != target.X || other.Y != target.Y) continue;
                if (!CanShove(ch, other)) return false;
            }
            return true;
        }

        Direction d = (dx, dy) switch
        {
            (0, -1) => Direction.North,
            (1, -1) => Direction.NorthEast,
            (1, 0) => Direction.East,
            (1, 1) => Direction.SouthEast,
            (0, 1) => Direction.South,
            (-1, 1) => Direction.SouthWest,
            (-1, 0) => Direction.West,
            (-1, -1) => Direction.NorthWest,
            _ => Direction.North,
        };

        var here = new Point3D(ch.X, ch.Y, ch.Z, ch.MapIndex);
        if (!_walkCheck.CheckMovement(ch, here, d, out _))
            return false;

        foreach (var other in _world.GetCharsInRange(target, 0))
        {
            if (other == ch || other.IsDead) continue;
            if (other.X != target.X || other.Y != target.Y) continue;
            if (!CanShove(ch, other))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if one character can push past another.
    /// </summary>
    private static bool CanShove(Objects.Characters.Character mover, Objects.Characters.Character blocker)
    {
        if ((CharDefHelper.GetCanFlags(blocker) & CanFlags.C_Statue) != 0) return false;
        // ServUO / RunUO Mobile.CheckShove parity.
        if (mover.PrivLevel >= PrivLevel.Counsel) return true;

        if (blocker.IsDead || mover.IsDead)
            return true;

        // One creature does not push past another (Source-X CCharAct.cpp:4624), unless
        // the shard says they may or this one carries TAG.OVERRIDE.SHOVE. Players shove
        // creatures; creatures hold each other up, which is what keeps a guard behind
        // the crowd it is meant to be stuck behind. The check sits after the dead and
        // staff cases so a corpse-walk still works.
        if (!mover.IsPlayer && !blocker.IsPlayer && !NpcShoveNpc &&
            !(mover.TryGetTag("OVERRIDE.SHOVE", out string? ovr) &&
              SphereNet.Core.Types.ScriptNumber.TryParseToken(ovr, out long o) && o != 0))
            return false;

        if ((blocker.IsStatFlag(StatFlag.Hidden) || blocker.IsStatFlag(StatFlag.Invisible))
            && blocker.PrivLevel >= PrivLevel.Counsel)
            return true;

        if (mover.Stam == mover.MaxStam && mover.MaxStam > 0)
            return true;

        return false;
    }

    /// <summary>
    /// The region-crossing triggers of Source-X CChar::MoveToRegion
    /// (CCharAct.cpp:5102-5195): the old area's @Exit, the character's @RegionLeave,
    /// the new area's @Enter, the character's @RegionEnter - each able to refuse the
    /// move with RETURN 1. Returns false on a refusal.
    ///
    /// The refusal only counts when there is somewhere to stay: leaving is refused
    /// only with a new area to go to, entering only with an old area to stay in, and
    /// a GM is never refused. @RegionLeave/@RegionEnter hand the area as ARGO - the
    /// pack's key-gated areas read ARGO.TAG0/ARGO.DEFNAME from it. The
    /// f_onchar_regionleave/regionenter functions are reached through the char
    /// trigger's own function fallback; running them again here ran them twice.
    /// </summary>
    private bool FireRegionTransition(Character ch, World.Regions.Region? oldRegion,
        World.Regions.Region? newRegion, bool allowReject)
    {
        if (_triggerDispatcher == null) return true;
        if (allowReject && ch.PrivLevel >= PrivLevel.GM)
            allowReject = false;

        if (oldRegion != null)
        {
            bool mayRefuse = allowReject && newRegion != null;
            if (_triggerDispatcher.FireRegionEvents(oldRegion, "Exit", ch,
                    new TriggerArgs { CharSrc = ch, S1 = oldRegion.Name }) == TriggerResult.True && mayRefuse)
                return false;
            if (_triggerDispatcher.FireCharTrigger(ch, CharTrigger.RegionLeave,
                    new TriggerArgs { CharSrc = ch, S1 = oldRegion.Name, O1 = oldRegion }) == TriggerResult.True && mayRefuse)
                return false;
        }
        if (newRegion != null)
        {
            bool mayRefuse = allowReject && oldRegion != null;
            if (_triggerDispatcher.FireRegionEvents(newRegion, "Enter", ch,
                    new TriggerArgs { CharSrc = ch, S1 = newRegion.Name }) == TriggerResult.True && mayRefuse)
                return false;
            if (_triggerDispatcher.FireCharTrigger(ch, CharTrigger.RegionEnter,
                    new TriggerArgs { CharSrc = ch, S1 = newRegion.Name, O1 = newRegion }) == TriggerResult.True && mayRefuse)
                return false;
        }
        return true;
    }

    /// <summary>Check step effects (traps, fields, region enter/leave). Returns false
    /// when the area's or room's @Step refused the step.</summary>
    /// <summary>sphere.ini MAXSHIPPLANKTELEPORT: how far along the facing a plank looks
    /// for the shore (Source-X m_iMaxShipPlankTeleport).</summary>
    public static int MaxShipPlankTeleport { get; set; } = 18;

    /// <summary>Source-X CChar::MoveToValidSpot (CCharAct.cpp:5369) from a ship: walk
    /// <paramref name="dist"/> tiles along <paramref name="dir"/>, starting
    /// <paramref name="distStart"/> out, skipping any ship; the first tile the character
    /// can stand on at or below its own height plus a person's is the answer (water is
    /// no surface to a character that cannot swim, so the resolver never offers it). A tile
    /// that only offers something higher is a wall, and the search stops there rather
    /// than passing through it.</summary>
    internal bool MoveToValidSpot(Objects.Characters.Character ch, Direction dir, int dist, int distStart,
        out Point3D spot)
    {
        spot = default;
        GetDirectionDelta(dir, out short dx, out short dy);
        int x = ch.X + dx * distStart, y = ch.Y + dy * distStart;
        int startZ = ch.Z + WalkCheck.PersonHeight;
        var ships = Objects.Items.Item.ResolveShipEngine?.Invoke();
        var md = _world.MapData;
        for (int i = 0; i < dist; i++, x += dx, y += dy)
        {
            if (md == null) break;
            var (w, h) = md.GetMapSize(ch.MapIndex);
            if (x < 0 || y < 0 || x >= w || y >= h) break;
            var at = new Point3D((short)x, (short)y, (sbyte)Math.Clamp(startZ, sbyte.MinValue, sbyte.MaxValue), ch.MapIndex);
            if (ships?.FindShipAt(at) != null)
                continue;   // never onto another ship - it may be locked
            var stand = _world.Standing.ResolveStandingSurface(ch, ch.MapIndex, x, y, startZ,
                WalkCheck.StandingPolicy.Settle);
            if (!stand.Found)
                continue;
            if (stand.Z > startZ)
                break;      // a wall: do not pass through it
            spot = new Point3D((short)x, (short)y, stand.Z, ch.MapIndex);
            return true;
        }
        return false;
    }

    /// <summary>Spell_Teleport's fTakePets: the pets following their owner go too, or a
    /// disembarking owner leaves them on the ship.</summary>
    private void TakePetsAlong(Objects.Characters.Character owner, Point3D dest)
    {
        foreach (var pet in _world.GetCharsInRange(owner.Position, 12).ToList())
        {
            if (pet == owner || pet.IsPlayer || pet.IsDead || pet.IsStatFlag(StatFlag.Ridden)) continue;
            if (!pet.HasOwner(owner.Uid) || pet.PetAIMode != PetAIMode.Follow) continue;
            byte oldMap = pet.MapIndex;
            _world.MoveCharacter(pet, dest);
            OnTeleport?.Invoke(pet, dest, oldMap);
        }
    }

    private bool CheckLocationEffects(Objects.Characters.Character ch, Point3D originalPos, World.Regions.Region? previousRegion)
    {
        var pos = originalPos;
        bool spellHit = false;

        // Region @Step, then the room's, on EVERY walking step and before any item
        // underfoot is looked at (CChar::CheckLocationEffects, CCharAct.cpp:4904-4919).
        // It used to fire only when the step stayed inside one region, and nothing
        // read its RETURN 1.
        if (_triggerDispatcher != null)
        {
            var stepRegion = _world.FindRegion(pos);
            if (stepRegion != null &&
                _triggerDispatcher.FireRegionEvents(stepRegion, "Step", ch,
                    new TriggerArgs { CharSrc = ch, S1 = stepRegion.Name }) == TriggerResult.True)
                return false;
            var stepRoom = _world.FindRoom(pos);
            if (stepRoom != null &&
                _triggerDispatcher.FireRoomEvents(stepRoom, "Step", ch,
                    new TriggerArgs { CharSrc = ch, S1 = stepRoom.Name }) == TriggerResult.True)
                return false;
        }

        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            // Source-X CheckLocation weeds out anything the character cannot
            // actually reach in Z before it even looks at @STEP
            // (CCharAct.cpp:4934) - a trap, moongate or field on the floor below
            // shares the X/Y but is a storey away.
            if (!item.IsWithinStepHeight(ch.Z)) continue;

            // Source-X parity: @Step trigger gets first chance. RETURN 1
            // cancels the hard-coded effect (trap, teleport, moongate, …)
            // so scripts can fully replace native behaviour.
            // ARGN1 = fStanding (Source-X CCharAct m_iN1): 0 here — the char is
            // walking onto the item, not standing on it.
            var stepResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                new TriggerArgs { CharSrc = ch, ItemSrc = item, N1 = 0 });
            if (stepResult == TriggerResult.True)
                continue;

            switch (item.ItemType)
            {
                case ItemType.Web:
                    // Source-X Use_Item_Web: walking into a web sticks the char
                    // (giant spiders, ghosts and staff pass through). Freeze
                    // holds them until the web is destroyed by struggling
                    // (dclick, STR-based) or an outside hit knocks them free.
                    if (!ch.IsDead && ch.PrivLevel < PrivLevel.Counsel && ch.BodyId != 0x1C &&
                        !ch.IsStatFlag(StatFlag.Insubstantial))
                    {
                        if (item.HitsCur <= 0)
                            item.HitsCur = 60 + Random.Shared.Next(250); // Source-X CCharUse.cpp:638 web strength
                        ch.SetStatFlag(StatFlag.Freeze);
                        // Source-X LAYER_FLAG_Stuck (CCharAct.cpp:358) shows the
                        // paralyze icon while a char is held. No countdown here:
                        // the web is escaped by struggling, not by a timer.
                        Character.OnClientBuffChanged?.Invoke(
                            ch, BuffIcon.Paralyze, true, 0, null);
                    }
                    break;
                case ItemType.Trap:
                case ItemType.TrapActive:
                    // Source-X CCharAct CheckLocation: stepping springs the trap —
                    // Use_Trap() arms it and yields the MORE2 base damage.
                    int trapDamage = item.UseTrap();
                    if (!Combat.CombatEngine.IsDamageImmune(ch))
                    {
                        ch.Hits -= (short)Math.Min(trapDamage, ch.Hits);
                        if (ch.Hits <= 0 && !ch.IsDead)
                        {
                            if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                            else ch.Kill();
                        }
                    }
                    break;
                case ItemType.Switch:
                    // A switch with m_itSwitch.m_wStep (MOREX) set works by being
                    // walked onto: Use_Item on it (CheckLocationEffects,
                    // CCharAct.cpp:5026; CItem.h:550). Only a double-click ran it.
                    if (item.MoreP.X != 0 && OnStepUseItem?.Invoke(ch, item) != true)
                        item.SetSwitchState();
                    break;
                case ItemType.ShipPlank:
                case ItemType.Rope:
                {
                    // Walking onto an open plank (or a rope) puts you ashore:
                    // upstream looks along the way you face for the first spot you
                    // can stand on that is not a ship, up to MAXSHIPPLANKTELEPORT
                    // tiles, and teleports you there (CheckLocationEffects,
                    // CCharAct.cpp:5038 -> MoveToValidSpot :5369). Only double-
                    // clicking the plank did anything, so the water between plank
                    // and quay could not be crossed at all.
                    if (ch.IsStatFlag(StatFlag.Hovering) || item.IsAttr(ObjAttributes.Static))
                        break;
                    if (MoveToValidSpot(ch, ch.Direction, MaxShipPlankTeleport, 1, out var ashore))
                    {
                        byte oldMap = ch.MapIndex;
                        TakePetsAlong(ch, ashore);
                        _world.MoveCharacter(ch, ashore);
                        OnTeleport?.Invoke(ch, ashore, oldMap);
                        pos = ch.Position;
                    }
                    break;
                }
                case ItemType.Telepad:
                case ItemType.Moongate:
                {
                    var dest = item.MoreP;
                    var md = _world.MapData;
                    bool destPassable = md == null || md.IsPassable(dest.Map, dest.X, dest.Y, dest.Z);
                    if ((dest.X != 0 || dest.Y != 0) && dest.X >= 0 && dest.Y >= 0 &&
                        _world.GetSector(dest) != null && destPassable)
                    {
                        byte oldMap = ch.MapIndex;
                        _world.MoveCharacter(ch, dest);
                        OnTeleport?.Invoke(ch, dest, oldMap);
                        pos = ch.Position;
                    }
                    break;
                }
            }

            // Typed field step effect (fire damages, poison poisons, paralyze
            // freezes, barriers inert — Source-X field spell on step). Falls
            // back to the legacy flat FIELD_DAMAGE for script-made fields.
            // Source-X caps one location check at a single spell effect
            // (CCharAct.cpp:4996): stacking Fire Fields on a tile would otherwise
            // multiply the damage of one step, and a Paralyze+Fire stack would
            // re-freeze the victim at every damage tick with no way out. The cap
            // follows the RESULT, not the attempt - a field that landed nothing
            // leaves the next one its chance.
            bool isSpellField = item.TryGetTag("FIELD_SPELL", out _) ||
                (item.ItemType == ItemType.Spell && item.MoreP.X > 0);
            var touch = spellHit && isSpellField
                ? FieldTouchResult.Handled      // a spell field already went off here
                : Character.FieldTouchHook?.Invoke(ch, item) ?? FieldTouchResult.NotHandled;
            if (touch == FieldTouchResult.SpellHit)
                spellHit = true;

            if (touch == FieldTouchResult.NotHandled &&
                item.TryGetTag("FIELD_DAMAGE", out string? fdStr) && int.TryParse(fdStr, out int fieldDmg) &&
                !Combat.CombatEngine.IsDamageImmune(ch))
            {
                ch.Hits -= (short)Math.Min(fieldDmg, ch.Hits);
                if (ch.Hits <= 0 && !ch.IsDead)
                {
                    if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                    else ch.Kill();
                }
            }
        }

        // Exit/Enter already ran in GameWorld.MoveCharacter, before SRC.REGION
        // changed, and the area's own @Step ran at the top. The character-side
        // @RegionStep (an engine extension - upstream has no such trigger) keeps to
        // same-region footsteps.
        var newRegion = _world.FindRegion(pos);
        if (newRegion != null && newRegion == previousRegion && _triggerDispatcher != null)
        {
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RegionStep,
                new TriggerArgs { S1 = newRegion.Name });
        }

        // Room enter/leave/step detection
        var newRoom = _world.FindRoom(pos);
        ch.TryGetTag("CURRENT_ROOM", out string? prevRoomUid);
        string newRoomUid = newRoom?.Uid.ToString() ?? "";

        if (prevRoomUid != newRoomUid)
        {
            // Exit old room
            if (!string.IsNullOrEmpty(prevRoomUid) && uint.TryParse(prevRoomUid, out uint oldRoomId))
            {
                var oldRoom = _world.FindRoomByUid(oldRoomId);
                if (oldRoom != null && _triggerDispatcher != null)
                {
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomLeave,
                        new TriggerArgs { S1 = oldRoom.Name });
                    _triggerDispatcher.FireRoomEvents(oldRoom, "Exit", ch,
                        new TriggerArgs { CharSrc = ch, S1 = oldRoom.Name });
                }
            }

            // Enter new room
            if (newRoom != null && _triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomEnter,
                    new TriggerArgs { S1 = newRoom.Name });
                _triggerDispatcher.FireRoomEvents(newRoom, "Enter", ch,
                    new TriggerArgs { CharSrc = ch, S1 = newRoom.Name });
            }

            ch.SetTag("CURRENT_ROOM", newRoomUid);
        }
        else if (newRoom != null && _triggerDispatcher != null)
        {
            // Step within same room (the room's own @Step ran at the top).
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomStep,
                new TriggerArgs { S1 = newRoom.Name });
        }
        return true;
    }

    /// <summary>
    /// Get expected delay between movement steps.
    /// Maps to speed check in Event_Walk / Event_CheckWalkBuffer.
    /// </summary>
    public static int GetMoveDelay(bool mounted, bool running, bool warMode = false)
    {
        // War mode does NOT slow movement. Neither the 2D/CUO client
        // (MovementSpeed.TimeToCompleteMovement takes no war flag — foot steps
        // stay 400/200ms in or out of combat) nor Source-X (Event_CheckWalkBuffer
        // and the fastwalk delay in CClientEvent use pure foot/mount × walk/run
        // timings with no combat-stance factor) apply a penalty. Pacing the server
        // 20% slower than the client sends desyncs the client's unconfirmed-step
        // budget and produces an on-foot in-combat walk stutter. The warMode
        // parameter is retained for call-site compatibility.
        _ = warMode;
        return (mounted, running) switch
        {
            (true, true) => RunDelayMount,
            (true, false) => WalkDelayMount,
            (false, true) => RunDelayFoot,
            (false, false) => WalkDelayFoot,
        };
    }

    public static int GetMoveDelay(bool mounted, bool running, bool warMode, byte speedMode)
    {
        // Source-X Event_Walk treats SPEEDMODE bit 0 like mounted movement:
        // foot walk/run cadence becomes 200/100ms instead of 400/200ms.
        bool speedMounted = mounted || ((speedMode & 0x01) != 0);
        return GetMoveDelay(speedMounted, running, warMode);
    }

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }

    private static int GetPackWeight(Objects.Items.Item pack)
    {
        return pack.TotalWeight;
    }

    private static void TickStealthStep(Objects.Characters.Character ch)
    {
        if (ch.StepStealth <= 0)
            return;

        // A mounted sneak is given away by the horse when the shard says so
        // (REVEALF_ONHORSE, CCharAct.cpp:4850) - checked BEFORE the step is counted,
        // so being mounted ends the sneak at once rather than at the last step.
        if (ch.IsStatFlag(StatFlag.OnHorse) && ch.ClearHiddenState(RevealFlags.OnHorse))
            return;

        ch.StepStealth--;
        Character.OnStepStealth?.Invoke(ch);

        if (ch.StepStealth <= 0)
            ch.ClearHiddenState();
    }
}
