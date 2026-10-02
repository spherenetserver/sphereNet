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

    /// <summary>Whether <paramref name="item"/> is a memory object that belongs on
    /// LAYER_SPECIAL beside any number of others: IT_EQ_MEMORY_OBJ by type, or the
    /// default memory graphic (i_memory, 02007) when the save's type was not
    /// resolved. A spell effect is not one - the spell engine owns those, and neither
    /// is an item that borrows the memory graphic under a type of its own: a script
    /// item written "ID=i_memory / TYPE=t_eq_script" is IT_EQ_SCRIPT upstream, ticks
    /// through its own @Timer and is never handed to Memory_OnTick
    /// (CChar::OnTickEquip, CCharAct.cpp:4082-4097).</summary>
    public static bool IsMemoryObject(Item item) =>
        item.ItemType == ItemType.EqMemoryObj ||
        (item.ItemType == ItemType.Normal && item.BaseId == MemoryObjectGraphic);

    /// <summary>Whether <paramref name="item"/> is one of the types LAYER_SPECIAL holds
    /// any number of: CanEquipLayer answers LAYER_SPECIAL for IT_EQ_TRADE_WINDOW,
    /// IT_EQ_MEMORY_OBJ and IT_EQ_SCRIPT alike, "we can have multiple items of these"
    /// (CCharStatus.cpp:360-367). Those share the memory list here; only a memory
    /// object is a memory.</summary>
    public static bool SharesSpecialLayer(Item item) =>
        IsMemoryObject(item) || item.ItemType is ItemType.EqScript or ItemType.EqTradeWindow;

    /// <summary>The layer CanEquipLayer settles on when an item names none of its own:
    /// LAYER_SPECIAL for the stacking types above, otherwise none - "not legal"
    /// (CCharStatus.cpp:360-367).</summary>
    public static Layer DefaultLayerFor(Item item) =>
        item.ItemType is ItemType.EqMemoryObj or ItemType.EqScript or ItemType.EqTradeWindow ||
        IsMemoryObject(item)
            ? Layer.Special
            : Layer.None;

    /// <summary>Graphic of the default memory object (ITEMDEF 02007, i_memory).</summary>
    public const ushort MemoryObjectGraphic = 0x2007;

    /// <summary>Wear a memory object on LAYER_SPECIAL. Upstream that layer holds any
    /// number of items (CChar::LayerAdd only clears a conflicting slot for the
    /// wearable layers), so a memory never displaces another one into the pack or
    /// onto the ground; here they share the memory list with the memories the engine
    /// makes itself. An item that came from a save keeps being saved as an item
    /// record (CONT + LAYER=30, the Source-X form); one the engine created stays a
    /// MEMORY line on its owner. <paramref name="fireEquip"/> runs @MemoryEquip - a
    /// live add, not a load (LayerAdd skips it while loading, CCharAct.cpp:266).</summary>
    public void AttachMemory(Item mem, bool fireEquip)
    {
        // Only an untyped i_memory becomes a memory object. Any other item keeps the
        // type it was given - LayerAdd never retypes what it wears (CCharAct.cpp:301);
        // turning an IT_EQ_SCRIPT item into a memory handed its timer to the memory
        // sweep, which deleted it unseen instead of running its @Timer.
        if (mem.ItemType == ItemType.Normal && mem.BaseId == MemoryObjectGraphic)
            mem.ItemType = ItemType.EqMemoryObj;
        mem.IsEquipped = true;
        mem.EquipLayer = Layer.Special;
        mem.ContainedIn = _owner.Uid;
        if (_memories.Contains(mem))
            return;
        _memories.Add(mem);
        if (fireEquip)
            Character.OnMemoryEquip?.Invoke(mem);
    }

    /// <summary>Wear <paramref name="item"/> on a slot another item already holds,
    /// because the save says so. Source-X LayerAdd runs no CanEquipLayer while loading
    /// (CCharAct.cpp:266): a character saved with a shield AND a lantern on LAYER_HAND2
    /// comes back wearing both, and nothing is bounced into the pack or onto the
    /// ground. The one equipment slot keeps the first; the second shares this list,
    /// which already holds any number of worn items, keeps its layer, saves as its own
    /// item record (CONT + LAYER) and goes with its wearer like any worn item. It
    /// leaves through <see cref="DetachMemory"/>.</summary>
    public void AttachStackedWorn(Item item, Layer layer)
    {
        item.IsEquipped = true;
        item.EquipLayer = layer;
        item.ContainedIn = _owner.Uid;
        if (!_memories.Contains(item))
            _memories.Add(item);
    }

    /// <summary>Whether <paramref name="item"/> is an ordinary wearable worn over an
    /// occupied slot (<see cref="AttachStackedWorn"/>) rather than a memory, a spell
    /// effect or a LAYER_SPECIAL script item.</summary>
    public static bool IsStackedWorn(Item item) =>
        !item.IsDeleted && item.IsEquipped &&
        !IsMemoryObject(item) &&
        item.ItemType is not (ItemType.EqScript or ItemType.EqTradeWindow or ItemType.Spell) &&
        item.EquipLayer > Layer.None && item.EquipLayer < Layer.Dragging &&
        item.EquipLayer != Layer.Special;

    /// <summary>Take a memory object off the list without deleting it, because it is
    /// moving somewhere else. Returns false when it is not one of this character's.</summary>
    public bool DetachMemory(Item mem)
    {
        if (mem.ItemType == ItemType.Spell || !_memories.Remove(mem))
            return false;
        mem.IsEquipped = false;
        mem.ContainedIn = Serial.Invalid;
        return true;
    }

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
            // Only a memory object is Memory_OnTick's (CChar::OnTickEquip,
            // CCharAct.cpp:4082-4097). A spell memory's timer is its effect's clock and
            // the spell engine runs it (Spell_Equip_OnTick); an IT_EQ_SCRIPT or trade
            // window item sharing the layer runs its own @Timer through the item timer
            // queue. Sweeping those here deleted them the moment their timer came due.
            if (mem.ItemType != ItemType.EqMemoryObj)
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
