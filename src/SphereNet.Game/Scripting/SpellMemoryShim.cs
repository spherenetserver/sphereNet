using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Read-only ARGO stand-in for spell-effect triggers on effects the engine
/// tracks as plain state instead of a worn memory item (Source-X equips a
/// real spell-memory CItem; our poison lives in CharacterPoisonState).
/// Scripts address the memory as &lt;ARGO.BASEID&gt; / &lt;ARGO.MOREY&gt; /
/// &lt;ARGO.LINK&gt;, so this shim answers exactly those reads — e.g. the
/// reference @SpellEffectTick handler gates on
/// IF (&lt;ARGO.BASEID&gt; == I_RUNE_POISON) and scales by &lt;ARGO.MOREY&gt;.
/// Writes and verbs are rejected: the shim is not a world object.
/// </summary>
public sealed class SpellMemoryShim : IScriptObj
{
    /// <summary>Spell id the memory represents (MOREX in Source-X memories).</summary>
    public int SpellId { get; init; }

    /// <summary>Base item id the memory reports — the spell's RUNE_ITEM, which
    /// is what Source-X poison memories carry and what scripts compare against.</summary>
    public ushort BaseId { get; init; }

    /// <summary>Effect strength on the Sphere poison scale (MOREY).</summary>
    public int MoreY { get; init; }

    /// <summary>Caster/poisoner UID (LINK), 0 when unattributed.</summary>
    public uint LinkUid { get; init; }

    public string Name { get; init; } = "";

    public string GetName() => Name;

    public bool TryGetProperty(string key, out string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "BASEID":
            case "ID":
                value = $"0{BaseId:X}";
                return true;
            case "MOREY":
                value = MoreY.ToString();
                return true;
            case "MOREX":
                value = SpellId.ToString();
                return true;
            case "LINK":
                value = LinkUid != 0 ? $"0{LinkUid:X8}" : "0";
                return true;
            case "NAME":
                value = Name;
                return true;
            default:
                value = "";
                return false;
        }
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;

    public bool TrySetProperty(string key, string value) => false;

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
        => TriggerResult.Default;
}
