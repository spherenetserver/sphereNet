namespace SphereNet.Game.Objects;

/// <summary>
/// Motor içi geçici tag'ler — Source-X'te field/struct olan, SphereNet'te
/// tag olarak tutulan runtime state. Script <c>TAG.x=</c> ile yazamaz;
/// save'e de gitmez.
/// </summary>
public static class EngineTags
{
    private static readonly HashSet<string> EphemeralExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPELL_CASTING", "SPELL_PRECAST", "CAST_TIMER",
        "SPELL_TARGET_UID",
        "SPELL_TARGET_X", "SPELL_TARGET_Y", "SPELL_TARGET_Z",
        "SPELL_TARGET_POS_X", "SPELL_TARGET_POS_Y", "SPELL_TARGET_POS_Z",
        "SKILL_PENDING_ID", "SKILL_DELAY_END", "SKILL_STROKE_NEXT",
        "SKILL_PENDING_TARGET",
        "SKILL_PENDING_X", "SKILL_PENDING_Y", "SKILL_PENDING_Z",
        "SKILL_MENU_PENDING",
        "CURRENT_REGION", "CURRENT_REGION_UID", "CURRENT_ROOM",
        "DRAGGING", "OBJ", "help_type", "PARTY_INVITE_FROM", "PARTY_INVITE_TIME",
        "SCROLL_UID", "WAND_UID", "StealthSteps", "FOCUS_BUFF",
        "TRACKING_TARGET", "TRACKING_UNTIL", "TRACKING_ARROW_NEXT",
        "CAMPING_SAFE_LOGOUT_UNTIL", "CLIENT_LINGER_UNTIL",
        "ATTACK_TARGET", "FOLLOW_TARGET", "GUARD_TARGET", "GO_TARGET",
        "TARGP", "TARG.X", "TARG.Y", "TARG.Z", "TARG.MAP", "TARG.UID",
    };

    /// <summary>True if scripts must not persist or override this tag key.</summary>
    public static bool IsEphemeral(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (EphemeralExact.Contains(key)) return true;
        return key.StartsWith("SPELL_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("SKILL_PENDING_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remove all ephemeral keys from an object's tag map.</summary>
    public static int StripEphemeral(ObjBase obj)
    {
        int removed = 0;
        foreach (var (key, _) in obj.Tags.GetAll().ToList())
        {
            if (!IsEphemeral(key)) continue;
            obj.RemoveTag(key);
            removed++;
        }
        return removed;
    }
}
