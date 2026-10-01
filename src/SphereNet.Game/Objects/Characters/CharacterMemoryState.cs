using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Memory-item subsystem extracted from Character (decomposition slice 3):
/// the EqMemoryObj list, fight/aggressor memories and their timeout ticks.
/// Maps to Source-X CChar Memory_* methods. Character keeps thin delegating
/// members (Memory_* / Memories), so the public API and behaviour are
/// unchanged — the logic moved verbatim. Static hooks (OnMemoryEquip,
/// NotoSaveUpdate, SendOwnerMessage) stay on Character and fire with the
/// owner exactly as before.
/// </summary>
public sealed class CharacterMemoryState
{
    private readonly Character _owner;
    private readonly List<Item> _memories = [];

    public CharacterMemoryState(Character owner)
    {
        _owner = owner;
    }

    public IReadOnlyList<Item> Items => _memories;

    public void Clear() => _memories.Clear();

    public Item? FindObj(Serial uid)
    {
        for (int i = 0; i < _memories.Count; i++)
        {
            var m = _memories[i];
            if (m.ItemType == ItemType.EqMemoryObj && m.Link == uid)
                return m;
        }
        return null;
    }

    public Item? FindTypes(MemoryType flags)
    {
        if (flags == MemoryType.None) return null;
        for (int i = 0; i < _memories.Count; i++)
        {
            if (_memories[i].IsMemoryTypes(flags))
                return _memories[i];
        }
        return null;
    }

    public Item? FindObjTypes(Serial uid, MemoryType flags)
    {
        var mem = FindObj(uid);
        if (mem == null) return null;
        return mem.IsMemoryTypes(flags) ? mem : null;
    }

    public Item CreateObj(Serial uid, MemoryType flags)
    {
        var mem = new Item
        {
            ItemType = ItemType.EqMemoryObj,
            BaseId = 0x2007,
            Name = "Memory",
        };
        mem.Link = uid;
        mem.SetAttr(ObjAttributes.Newbie);
        mem.IsSavedWithOwner = true;
        mem.IsEquipped = true;
        mem.EquipLayer = Layer.Special;
        mem.ContainedIn = _owner.Uid;

        mem.SetMemoryTypes(flags);
        AddTypes(mem, flags);

        _memories.Add(mem);
        // @MemoryEquip (Source-X) — a memory item was equipped on this character.
        Character.OnMemoryEquip?.Invoke(mem);
        return mem;
    }

    /// <summary>Make an equipped IT_SPELL memory and wear it, as Source-X
    /// CChar::Spell_Effect_Create does: the graphic is the spell's RUNE_ITEM (fallback
    /// 0x2053), it sits on the spell's own hidden layer (LAYER_SPELL_STATS and up, never
    /// sent to the client), MOREX is the spell, MOREY its level and LINK the caster.
    /// With a world it is a registered object - FINDLAYER, UID.x.REMOVE, and an ordinary
    /// equipped item in the save. This only wears the item; the spell engine is what
    /// runs an effect (SpellEngine.CreateEffect).</summary>
    public Item CreateSpellEffect(int spellId, ushort graphic, int level, Serial source, string name,
        Layer layer = Layer.Special, World.GameWorld? world = null)
    {
        var mem = world != null ? world.CreateItem() : new Item();
        mem.ItemType = ItemType.Spell;
        mem.BaseId = graphic != 0 ? graphic : (ushort)0x2053; // ITEMID_RHAND_POINT_NW fallback
        mem.Name = string.IsNullOrEmpty(name) ? "spell effect" : name;
        mem.MoreP = new Point3D((short)spellId, (short)level, 0, 0); // MOREX = spell, MOREY = strength
        mem.Link = source;
        mem.SetAttr(ObjAttributes.Newbie | ObjAttributes.Magic); // ATTR_NEWBIE|ATTR_MAGIC (dispellable)
        AttachSpellEffect(mem, layer);
        return mem;
    }

    /// <summary>Wear a spell memory on <paramref name="layer"/>. Spell layers can carry
    /// more than one item (stacked stat spells), so these live in the memory list
    /// rather than in an equipment slot; FINDLAYER searches both. No @MemoryEquip:
    /// upstream's LayerAdd skips it for a spellable item (CCharAct.cpp:278-280). A
    /// world load wears a saved memory through here as well - without re-running its
    /// add, exactly as upstream's load does.</summary>
    public void AttachSpellEffect(Item mem, Layer layer)
    {
        mem.IsEquipped = true;
        mem.EquipLayer = layer;
        mem.ContainedIn = _owner.Uid;
        mem.IsSpellMemory = true;
        if (!_memories.Contains(mem))
            _memories.Add(mem);
    }

    /// <summary>Take a spell memory off the list without deleting it - the first step
    /// of its removal (upstream's RemoveSelf before OnRemoveObj).</summary>
    public void DetachSpellEffect(Item mem) => _memories.Remove(mem);

    public Item AddObjTypes(Serial uid, MemoryType flags)
    {
        var mem = FindObj(uid);
        if (mem == null)
            return CreateObj(uid, flags);
        AddTypes(mem, flags);
        NotoSaveDelete(uid);
        return mem;
    }

    public void AddTypes(Item mem, MemoryType flags)
    {
        mem.SetMemoryTypes(mem.GetMemoryTypes() | flags);
        mem.MoreP = _owner.Position;
        mem.More1 = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        UpdateFlags(mem);
    }

    private static void SetTimeoutMs(Item mem, long delayMs)
    {
        if (delayMs < 0)
            mem.SetTimeout(-1);
        else if (delayMs == 0)
            mem.SetTimeout(0);
        else
            mem.SetTimeout(Environment.TickCount64 + delayMs);
    }

    private void NotifyNotoriety(Item mem)
    {
        Character.NotoSaveUpdate?.Invoke(_owner);
        if (!mem.Link.IsValid) return;
        var link = Objects.ObjBase.ResolveWorld?.Invoke()?.FindChar(mem.Link);
        if (link != null)
            Character.NotoSaveUpdate?.Invoke(link);
    }

    private static void NotoSaveDelete(Serial uid)
    {
        if (!uid.IsValid) return;
        var link = Objects.ObjBase.ResolveWorld?.Invoke()?.FindChar(uid);
        if (link != null)
            Character.NotoSaveUpdate?.Invoke(link);
    }

    public bool UpdateClearTypes(Item mem, MemoryType flags)
    {
        var prev = mem.GetMemoryTypes();
        var remaining = prev & ~flags;
        mem.SetMemoryTypes(remaining);

        if ((flags & MemoryType.IPet) != 0 && (prev & MemoryType.IPet) != 0)
        {
            if (FindTypes(MemoryType.IPet) == null)
                _owner.ClearStatFlag(StatFlag.Pet);
        }

        if (remaining == MemoryType.None)
            return false;

        return UpdateFlags(mem);
    }

    public bool ClearTypes(Item mem, MemoryType flags)
    {
        if (UpdateClearTypes(mem, flags))
            return true;
        Delete(mem);
        return false;
    }

    public void ClearAllTypes(MemoryType flags)
    {
        for (int i = _memories.Count - 1; i >= 0; i--)
        {
            var m = _memories[i];
            if (!m.IsMemoryTypes(flags)) continue;
            ClearTypes(m, flags);
        }
    }

    /// <summary>Drop a memory. One a script was handed has a UID, and leaves the
    /// world with it (deleting a memory item is how Source-X forgets).</summary>
    public void Delete(Item mem)
    {
        _memories.Remove(mem);
        if (mem.IsDeleted)
            return;
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        if (world != null && world.IsRegistered(mem))
            world.DeleteObject(mem);
    }

    /// <summary>The memory as a script sees it: a registered item with its own UID
    /// (Source-X Memory_FindTypes / Memory_FindObj return the CItemMemory itself).</summary>
    public Item Expose(Item mem)
    {
        Objects.ObjBase.ResolveWorld?.Invoke()?.RegisterDetachedItem(mem);
        return mem;
    }

    public bool UpdateFlags(Item mem)
    {
        var flags = mem.GetMemoryTypes();
        if (flags == MemoryType.None) return false;

        long timeout;
        if ((flags & MemoryType.IPet) != 0)
            _owner.SetStatFlag(StatFlag.Pet);

        if ((flags & MemoryType.Fight) != 0)
            timeout = 30_000;
        else if ((flags & (MemoryType.IPet | MemoryType.Guard | MemoryType.Guild | MemoryType.Town)) != 0)
            timeout = -1;
        else if (!_owner.IsPlayer)
            timeout = 5 * 60_000;
        else
            timeout = 20 * 60_000;

        SetTimeoutMs(mem, timeout);
        NotifyNotoriety(mem);
        return true;
    }

    public bool OnMemoryTick(Item mem)
    {
        if (mem.Link == Serial.Invalid)
            return false;

        if (mem.IsMemoryTypes(MemoryType.Fight))
            return Fight_OnTick(mem);

        if (mem.IsMemoryTypes(MemoryType.IPet | MemoryType.Guard | MemoryType.Guild | MemoryType.Town))
            return true;

        return false;
    }

    /// <summary>Per-tick timeout sweep over the memory items (called from
    /// Character.OnTick). Expired memories run OnMemoryTick and are removed
    /// when it returns false.</summary>
    public void Tick(long now)
    {
        for (int i = _memories.Count - 1; i >= 0; i--)
        {
            var mem = _memories[i];
            // A spell memory's timer is its effect's clock: the spell engine runs it
            // (Spell_Equip_OnTick), never this sweep.
            if (mem.ItemType == ItemType.Spell)
                continue;
            long mt = mem.Timeout;
            if (mt > 0 && now >= mt)
            {
                mem.SetTimeout(0);
                if (!OnMemoryTick(mem))
                    Delete(mem);
            }
        }
    }

    public void Fight_Start(Character target)
    {
        if (target == null || !target.Uid.IsValid)
            return;

        var mem = FindObj(target.Uid);
        if (_owner.FightTarget == target.Uid &&
            mem != null && mem.IsMemoryTypes(MemoryType.Fight))
            return;

        MemoryType aggFlags;

        if (mem == null)
        {
            var targMem = target.Memory_FindObj(_owner.Uid);
            if (targMem != null)
            {
                if (targMem.IsMemoryTypes(MemoryType.IAggressor))
                    aggFlags = MemoryType.HarmedBy;
                else if (targMem.IsMemoryTypes(MemoryType.HarmedBy | MemoryType.SawCrime | MemoryType.Aggreived))
                    aggFlags = MemoryType.IAggressor;
                else
                    aggFlags = MemoryType.None;
            }
            else
            {
                aggFlags = MemoryType.IAggressor;
            }
            CreateObj(target.Uid, MemoryType.Fight | aggFlags);
            return;
        }

        if (_owner.Attacker_GetIndex(target.Uid) >= 0)
            return;

        if (mem.IsMemoryTypes(MemoryType.HarmedBy | MemoryType.SawCrime | MemoryType.Aggreived))
            aggFlags = MemoryType.None;
        else
            aggFlags = MemoryType.IAggressor;

        AddTypes(mem, MemoryType.Fight | aggFlags);
    }

    private void Fight_Retreat(Character target, Item fightMem)
    {
        if (target == null || target.IsStatFlag(StatFlag.Dead))
            return;

        int myDistFromBattle = _owner.Position.GetDistanceTo(fightMem.MoreP);
        int hisDistFromBattle = target.Position.GetDistanceTo(fightMem.MoreP);
        bool cowardice = myDistFromBattle > hisDistFromBattle;
        _owner.Attacker_Delete(target.Uid);

        if (cowardice && !fightMem.IsMemoryTypes(MemoryType.IAggressor))
            return;

        if (_owner.IsPlayer)
        {
            string msg = cowardice
                ? ServerMessages.GetFormatted(Msg.MsgCoward1, target.GetName())
                : ServerMessages.GetFormatted(Msg.MsgCoward2, target.GetName());
            Character.SendOwnerMessage?.Invoke(_owner, msg);
        }

        if (cowardice && _owner.IsPlayer)
            _owner.Fame = (short)Math.Max(0, _owner.Fame - 1);
    }

    private bool Fight_OnTick(Item mem)
    {
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        if (world == null) return false;

        var target = world.FindChar(mem.Link);
        if (target == null || target.IsDeleted || target.IsStatFlag(StatFlag.Dead))
        {
            _owner.Attacker_Delete(mem.Link);
            ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        int radar = Character.MapViewRadarTiles > 0 ? Character.MapViewRadarTiles : 18;
        long elapsedSec = _owner.Attacker_GetElapsedSeconds(target.Uid);
        bool attackerTimedOut = Character.AttackerTimeoutSeconds > 0 && elapsedSec >= 0 &&
            elapsedSec > Character.AttackerTimeoutSeconds;

        if (_owner.Position.GetDistanceTo(target.Position) > radar || attackerTimedOut)
        {
            Fight_Retreat(target, mem);
            ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        long fightElapsedMs = GetElapsedMs(mem);
        if (fightElapsedMs > 60 * 60 * 1000L)
        {
            _owner.Attacker_Delete(target.Uid);
            ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        if (target.Hits >= target.MaxHits && fightElapsedMs > 2 * 60 * 1000L)
        {
            _owner.Attacker_Delete(target.Uid);
            ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        SetTimeoutMs(mem, 2000);
        return true;
    }

    private static long GetElapsedMs(Item mem)
    {
        if (mem.More1 == 0) return 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Math.Max(0L, (now - mem.More1) * 1000L);
    }
}
