using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using ScriptArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Global [FUNCTION] hooks the engine calls by name at fixed points, outside the
/// trigger chains. The Source-X hook comes first; a Sphere 56T custom-version hook
/// runs next to it - never instead of it - and only when the pack defines it.
/// </summary>
public static class GlobalHookCalls
{
    /// <summary>Whether a player character may be deleted.
    ///
    /// Source-X CChar::NotifyDelete (CChar.cpp:425-436): f_onchar_delete on the
    /// character, SRC = the character, ARGO = its own client when it has one; RETURN 1
    /// keeps it.
    ///
    /// Sphere 56T custom-version compatibility: f_onchar_delete_player, same SRC and
    /// veto, with ARGO = the client deleting the character from its character-selection
    /// screen and empty for any other deletion (the pack's own description of the
    /// hook; its body logs <c>&lt;QVAL (&lt;ARGO&gt;)?&lt;ARGO&gt;:&lt;SRC.NAME&gt;/&lt;SRC&gt;&gt;</c>
    /// and decides with RETURN 1).</summary>
    public static bool PlayerDeleteAllowed(TriggerRunner? runner, Character ch, ITextConsole? console,
        IScriptObj? ownClient, IScriptObj? charSelectClient)
    {
        if (runner == null)
            return true;
        if (runner.TryRunFunction("f_onchar_delete", ch, console,
                new ScriptArgs(ch) { Object1 = ownClient }, out var result) &&
            result == TriggerResult.True)
            return false;
        if (runner.TryRunFunction("f_onchar_delete_player", ch, console,
                new ScriptArgs(ch) { Object1 = charSelectClient }, out result) &&
            result == TriggerResult.True)
            return false;
        return true;
    }

    /// <summary>Sphere 56T custom-version compatibility: <c>f_onchar_armor_calculation</c>
    /// at the pre-AOS armour stage. Source-X has no such hook. False when the pack does
    /// not define it - the engine's own roll then stands.
    ///
    /// Contract, from the 56T engine's own strings (CChar::CalcArmorDefense names the
    /// locals ArmorRating, ArMin, ArMax, ParryDefense and Damage next to the function
    /// name) and the two pack bodies that implement it:
    /// <list type="bullet">
    /// <item>default object and SRC = the defender (<c>&lt;SRC.AR&gt;</c>,
    /// <c>&lt;SRC.NPC&gt;</c>, <c>SRC.SAY</c> "Parried"); ARGO = the attacker;</item>
    /// <item>ARGN1 = the blow before armour, ARGN2 = the defence the engine rolled,
    /// ARGN3 = the damage type; LOCAL.ArmorRating / ArMax / ArMin are the roll's
    /// inputs, LOCAL.ParryDefense 0 (no parry share at this stage);</item>
    /// <item>result: LOCAL.Damage when the script set it (the custom-version body
    /// always does), otherwise ARGN1 - ARGN2 read back (a stock 0.56T body writes only
    /// ARGN2 = its own defence and reports <c>&lt;EVAL &lt;ARGN1&gt;-&lt;ARGN2&gt;&gt;</c>).</item>
    /// </list>
    /// The pack's f_onchar_armor_calculation_npc is a helper its own @GetHit calls; the
    /// engine does not call it.</summary>
    public static bool RunArmorCalculation(TriggerRunner? runner, Combat.CombatEngine.ArmorCalculationContext ctx,
        ITextConsole? console = null)
    {
        const string Function = "f_onchar_armor_calculation";
        if (runner == null || !runner.HasFunction(Function))
            return false;

        int seeded = Math.Max(0, ctx.Damage - ctx.Defense);
        var locals = new SphereNet.Scripting.Variables.VarMap();
        locals.SetInt("ArmorRating", ctx.ArmorRating);
        locals.SetInt("ArMax", ctx.ArMax);
        locals.SetInt("ArMin", ctx.ArMin);
        locals.SetInt("ParryDefense", 0);
        locals.SetInt("Damage", seeded);
        var args = new ScriptArgs(ctx.Defender, ctx.Damage, ctx.Defense, "")
        {
            Number3 = unchecked((long)(uint)ctx.DamageType),
            Object1 = ctx.Attacker,
            Object2 = ctx.Defender,
            SharedLocals = locals,
        };
        if (!runner.TryRunFunction(Function, ctx.Defender, console, args, out _))
            return false;

        string damageText = locals.GetKeyStr("Damage", zero: true);
        if (SphereNet.Core.Types.ScriptNumber.TryParseToken(damageText, out long localDamage) &&
            localDamage != seeded)
            ctx.Result = (int)Math.Clamp(localDamage, 0, int.MaxValue);
        else
            ctx.Result = (int)Math.Clamp(args.Number1 - args.Number2, 0, int.MaxValue);
        return true;
    }

    /// <summary>Sphere 56T custom-version compatibility: <c>f_onmulti_create</c>, run once
    /// a house or ship has been placed from its deed. Source-X has no such hook.
    ///
    /// Contract inferred from the pack's own body (ship_functions.scp): the default
    /// object is the new multi (<c>REF1 &lt;UID&gt;</c>, <c>&lt;COLOR&gt;</c>,
    /// <c>&lt;LINK&gt;</c>), SRC is the character who placed it, and MORE1 names that
    /// character (Source-X m_itShip.m_UIDCreator). To refuse a placement the script
    /// removes the multi itself and hands back a new deed itself (it reads the deed id
    /// from the multi's TAG.CONTRACT); a house refusal also says RETURN 1. So the
    /// engine only has to notice: the placement stands when the multi still exists and
    /// the function did not RETURN 1. The original deed is consumed either way - the
    /// refund is the script's.</summary>
    public static bool MultiCreateStands(TriggerRunner? runner, Item multi, Character placer,
        ITextConsole? console)
    {
        if (runner == null || !runner.HasFunction("f_onmulti_create"))
            return !multi.IsDeleted;
        runner.TryRunFunction("f_onmulti_create", multi, console,
            new ScriptArgs(placer) { Object2 = placer }, out var result);
        return !multi.IsDeleted && result != TriggerResult.True;
    }
}
