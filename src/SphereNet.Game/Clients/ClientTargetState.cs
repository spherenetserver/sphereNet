using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Clients;

/// <summary>
/// Target-cursor state machine extracted from GameClient (decomposition
/// phase 1 — see docs/GAMECLIENT_DECOMPOSITION_TR.md): every pending-target
/// mode the 0x6C response can resolve into, plus the generic callback slot.
/// Pure state relocation — the call sites read/write these members exactly
/// as they did the former GameClient fields; <see cref="Clear"/> is the old
/// ClearPendingTargetState body.
/// </summary>
public sealed class ClientTargetState
{
    /// <summary>True while a target cursor is open on the client.</summary>
    public bool CursorActive;

    /// <summary>Cursor session id sent in the 0x6C request. A response whose
    /// echoed id doesn't match belongs to a REPLACED cursor (the client's
    /// cancel-echo for the old one races the newly armed target) and must be
    /// ignored instead of cancelling/consuming the new target. 0 = no check
    /// (requests armed without an id, e.g. legacy paths).</summary>
    public uint CursorId;

    // Script-driven targeting (TARGET/TARGETF function + args, ground rules).
    public string? Function;
    public string FunctionArgs = "";
    public bool AllowGround;
    public Serial ItemUid = Serial.Invalid;
    public Item? ScriptNewItem;
    public Point3D? LastScriptPoint;

    // GM verb target modes (Source-X parity cursors).
    public bool Tele;
    public bool Remove;
    public bool Resurrect;
    public bool Inspect;
    public string? AddToken;
    /// <summary>Optional ADD/ADDITEM stack amount carried until the target
    /// response. Source-X stores this beside m_tmAdd.m_id.</summary>
    public ushort AddAmount = 1;
    public string? ShowArgs;
    public string? EditArgs;
    /// <summary>X-prefix verb fallback (CClient.cpp:921): the inner verb +
    /// args applied to the picked object.</summary>
    public string? XVerb;
    public string XVerbArgs = "";
    /// <summary>NUKE/NUKECHAR/NUDGE area verbs; <see cref="AreaRange"/> is
    /// the half-extent around the picked tile.</summary>
    public string? AreaVerb;
    public int AreaRange;
    /// <summary>Optional payload for the area verb (Source-X: NUKE takes a
    /// verb line to run instead of deleting, NUDGE takes "dx dy dz").</summary>
    public string AreaVerbArgs = "";

    /// <summary>Serial of the last object the client picked with any target
    /// cursor (Source-X m_Targ_UID). Deliberately NOT reset by
    /// <see cref="Clear"/> — LAST / GOTARG re-use it after the cursor closes.</summary>
    public uint LastPickedSerial;
    public bool Control;
    public bool Dupe;
    public bool Heal;
    public bool Kill;
    public bool Bank;
    public bool SummonTo;
    public bool Mount;
    public bool SummonCage;

    /// <summary>Callback-based cursor (housing, pets, ...).</summary>
    public Action<uint, short, short, sbyte, ushort>? Callback;
    /// <summary>Skill id whose pending use is aborted when this cursor is
    /// cancelled (-1 = none).</summary>
    public int SkillCancelId = -1;

    /// <summary>Hard-reset every pending target flow, including callbacks and
    /// the skill id associated with the cursor.</summary>
    public void Clear()
    {
        CursorId = 0;
        Tele = false;
        AddToken = null;
        AddAmount = 1;
        ShowArgs = null;
        EditArgs = null;
        XVerb = null;
        XVerbArgs = "";
        AreaVerb = null;
        AreaRange = 0;
        AreaVerbArgs = "";
        Control = false;
        Dupe = false;
        Heal = false;
        Bank = false;
        SummonTo = false;
        Mount = false;
        SummonCage = false;
        Remove = false;
        Resurrect = false;
        Inspect = false;
        Function = null;
        FunctionArgs = "";
        AllowGround = false;
        ItemUid = Serial.Invalid;
        ScriptNewItem = null;
        LastScriptPoint = null;
        Callback = null;
        SkillCancelId = -1;
        CursorActive = false;
    }
}
