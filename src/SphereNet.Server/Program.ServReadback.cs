using System.Globalization;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Server;

/// <summary>
/// SERV.&lt;key&gt; read-back for the parts of the upstream SERV chain that the main
/// switch in Program.Scripting.cs does not spell out one by one.
///
/// Upstream a SERV read walks g_Cfg.r_WriteVal (CServerConfig.cpp:1620), then
/// g_World.r_WriteVal, then CServerDef::r_WriteVal (CServerDef.cpp:417). The config
/// part is one table, sm_szLoadKeys (CServerConfig.cpp:759), read back through
/// CElementDef::GetValStr (CSAssoc.cpp:90) unless r_WriteVal special-cases the key.
/// Every key of that table that has a setting in this engine answers here, in the
/// form upstream writes it; before, all of them fell through to the defname lookup
/// and a script reading &lt;SERV.MAXFAME&gt; or &lt;SERV.LIGHTNIGHT&gt; got nothing.
/// </summary>
public static partial class Program
{
    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>CSString::FormatHex: upper-case hex behind a leading zero, "00" for 0.</summary>
    private static string Hx(long value) =>
        value == 0 ? "00" : "0" + ((uint)value).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>The config table. A value is read from the live <see cref="SphereConfig"/>
    /// on every call. Keys whose upstream read-back differs from the ini value are
    /// commented with the r_WriteVal case that makes them differ.</summary>
    private static readonly Dictionary<string, Func<SphereConfig, string>> s_servConfigReadback =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACCTFILES"] = c => c.AccountDir,
        ["ADVANCEDLOS"] = c => I(c.AdvancedLos),
        ["ALLOWLIGHTOVERRIDE"] = c => B(c.AllowLightOverride),
        ["ATTACKINGISACRIME"] = c => B(c.AttackingIsACrime),
        // /WEIGHT_UNITS on write undoes the *WEIGHT_UNITS on load: the ini value.
        ["BACKPACKOVERLOAD"] = c => I(c.BackpackOverload),
        ["BACKUPLEVELS"] = c => I(c.BackupLevels),
        ["BANKMAXITEMS"] = c => I(c.BankMaxItems),
        ["BANKMAXWEIGHT"] = c => I(c.BankMaxWeight),
        ["CANPETSDRINKPOTION"] = c => B(c.CanPetsDrinkPotion),
        ["CANUNDRESSPETS"] = c => B(c.CanUndressPets),
        ["CLIENTLOGINMAXTRIES"] = c => I(c.ClientLoginMaxTries),
        // Loaded as minutes * 60 * MSECS_PER_SEC with no r_WriteVal case, so the
        // generic ELEM_INT read hands back the milliseconds.
        ["CLIENTLOGINTEMPBAN"] = c => ((long)c.ClientLoginTempBanMinutes * 60_000L).ToString(CultureInfo.InvariantCulture),
        ["CLIENTMAX"] = c => I(c.ClientMax),
        ["CLIENTMAXIP"] = c => I(c.ClientMaxIP),
        // RC_COLORHIDDEN .. RC_COLORINVISSPELL are written with FormatHex.
        ["COLORHIDDEN"] = c => Hx(c.ColorHidden),
        ["COLORINVIS"] = c => Hx(c.ColorInvis),
        ["COLORINVISITEM"] = c => Hx(c.ColorInvisItem),
        ["COLORINVISSPELL"] = c => Hx(c.ColorInvisSpell),
        ["COLORNOTOCRIMINAL"] = c => I(c.ColorNotoCriminal),
        ["COLORNOTODEFAULT"] = c => I(c.ColorNotoDefault),
        ["COLORNOTOEVIL"] = c => I(c.ColorNotoEvil),
        ["COLORNOTOGOOD"] = c => I(c.ColorNotoGood),
        ["COLORNOTOGOODNPC"] = c => I(c.ColorNotoGoodNpc),
        ["COLORNOTOGUILDSAME"] = c => I(c.ColorNotoGuildSame),
        ["COLORNOTOGUILDWAR"] = c => I(c.ColorNotoGuildWar),
        ["COLORNOTOINVUL"] = c => I(c.ColorNotoInvul),
        ["COLORNOTOINVULGAMEMASTER"] = c => I(c.ColorNotoInvulGameMaster),
        ["COLORNOTONEUTRAL"] = c => I(c.ColorNotoNeutral),
        ["COMBATARCHERYMOVEMENTDELAY"] = c => I(c.CombatArcheryMovementDelay),
        ["COMBATDAMAGEERA"] = c => I(c.CombatDamageEra),
        ["COMBATHITCHANCEERA"] = c => I(c.CombatHitChanceEra),
        ["COMBATPARRYINGERA"] = c => I(c.CombatParryingEra),
        ["COMBATSPEEDERA"] = c => I(c.CombatSpeedEra),
        ["COMMANDLOG"] = c => I(c.CommandLog),
        // ELEM_BYTE over the prefix character: its code, not the character.
        ["COMMANDPREFIX"] = c => string.IsNullOrEmpty(c.CommandPrefix) ? "0" : I(c.CommandPrefix[0]),
        ["COMMANDTRIGGER"] = c => c.CommandTrigger,
        ["CONNECTINGMAX"] = c => I(c.ConnectingMax),
        ["CONTAINERMAXITEMS"] = c => I(c.ContainerMaxItems),
        ["CORPSENPCDECAY"] = c => I(c.CorpseNpcDecay),
        ["CORPSEPLAYERDECAY"] = c => I(c.CorpsePlayerDecay),
        ["CRIMINALTIMER"] = c => I(c.CriminalTimer),
        ["DEADCANNOTSEELIVING"] = c => I(c.DeadCannotSeeLiving),
        ["DEADSOCKETTIME"] = c => I(c.DeadSocketTime),
        ["DEFAULTCOMMANDLEVEL"] = c => I(c.DefaultCommandLevel),
        ["DISTANCETALK"] = c => I(c.DistanceTalk),
        ["DISTANCEWHISPER"] = c => I(c.DistanceWhisper),
        ["DISTANCEYELL"] = c => I(c.DistanceYell),
        ["DRAGWEIGHTMAX"] = c => I(c.DragWeightMax),
        ["DUNGEONLIGHT"] = c => I(c.DungeonLight),
        ["EMOTEFLAGS"] = c => ((uint)c.EmoteFlags).ToString(CultureInfo.InvariantCulture),
        ["EQUIPPEDCAST"] = c => B(c.EquippedCast),
        ["EVENTSITEM"] = c => c.EventsItem,
        ["EVENTSPET"] = c => c.EventsPet,
        ["EVENTSPLAYER"] = c => c.EventsPlayer,
        ["EVENTSREGION"] = c => c.EventsRegion,
        ["FEATUREAOS"] = c => I(c.FeatureAOS),
        ["FEATUREEXTRA"] = c => I(c.FeatureExtra),
        ["FEATUREKR"] = c => I(c.FeatureKR),
        ["FEATURELBR"] = c => I(c.FeatureLBR),
        ["FEATUREML"] = c => I(c.FeatureML),
        ["FEATURESA"] = c => I(c.FeatureSA),
        ["FEATURESE"] = c => I(c.FeatureSE),
        ["FLIPDROPPEDITEMS"] = c => B(c.FlipDroppedItems),
        ["FORCEGARBAGECOLLECT"] = c => B(c.ForceGarbageCollect),
        ["FREEZERESTARTTIME"] = c => I(c.FreezeRestartTime),
        ["GAMEMINUTELENGTH"] = c => I(c.GameMinuteLength),
        ["GUARDLINGER"] = c => I(c.GuardLinger),
        ["GUARDSONMURDERERS"] = c => B(c.GuardsOnMurderers),
        ["HELPINGCRIMINALSISACRIME"] = c => B(c.HelpingCriminalsIsACrime),
        ["HITPOINTPERCENTONREZ"] = c => I(c.HitpointPercentOnRez),
        ["HITSHUNGERLOSS"] = c => I(c.HitsHungerLoss),
        ["LIGHTDAY"] = c => I(c.LightDay),
        ["LIGHTNIGHT"] = c => I(c.LightNight),
        ["LOG"] = c => c.LogDir,
        ["LOOTINGISACRIME"] = c => B(c.LootingIsACrime),
        ["LOSTNPCTELEPORT"] = c => I(c.LostNpcTeleport),
        ["MAGICUNLOCKDOOR"] = c => I(c.MagicUnlockDoor),
        ["MANALOSSABORT"] = c => B(c.ManaLossAbort),
        ["MANALOSSFAIL"] = c => B(c.ManaLossFail),
        ["MANALOSSPERCENT"] = c => I(c.ManaLossPercent),
        ["MAPVIEWRADAR"] = c => I(c.MapViewRadar),
        ["MAPVIEWSIZE"] = c => I(c.MapViewSize),
        ["MAPVIEWSIZEMAX"] = c => I(c.MapViewSizeMax),
        ["MAXBASESKILL"] = c => I(c.MaxBaseSkill),
        ["MAXCHARSPERACCOUNT"] = c => I(c.MaxCharsPerAccount),
        ["MAXFAME"] = c => I(c.MaxFame),
        ["MAXHOUSESGUILD"] = c => I(c.MaxHousesGuild),
        ["MAXITEMCOMPLEXITY"] = c => I(c.MaxItemComplexity),
        ["MAXKARMA"] = c => I(c.MaxKarma),
        ["MAXLOOPTIMES"] = c => I(c.MaxLoopTimes),
        ["MAXPACKETSPERTICK"] = c => I(c.MaxPacketsPerTick),
        ["MAXPOLYSTATS"] = c => I(c.MaxPolyStats),
        ["MAXSHIPPLANKTELEPORT"] = c => I(c.MaxShipPlankTeleport),
        ["MAXSHIPSACCOUNT"] = c => I(c.MaxShipsAccount),
        ["MAXSHIPSPLAYER"] = c => I(c.MaxShipsPlayer),
        ["MD5PASSWORDS"] = c => B(c.Md5Passwords),
        ["MEDITATIONMOVEMENTABORT"] = c => B(c.MeditationMovementAbort),
        ["MINCHARDELETETIME"] = c => I(c.MinCharDeleteTime),
        ["MINKARMA"] = c => I(c.MinKarma),
        ["MONSTERFEAR"] = c => B(c.MonsterFear),
        ["MONSTERFIGHT"] = c => B(c.MonsterFight),
        ["MOVERATE"] = c => I(c.MoveRate),
        ["MULFILES"] = c => c.MulFilesDir,
        // Loaded as SECONDS (* MSECS_PER_SEC) but written back / (60 * MSECS_PER_SEC):
        // the read-back is the ini value in minutes.
        ["MURDERDECAYTIME"] = c => I(c.MurderDecayTime / 60),
        ["MYSQLDATABASE"] = c => c.MySQLDatabase,
        ["MYSQLHOST"] = c => c.MySQLHost,
        ["MYSQLPASSWORD"] = c => c.MySQLPassword,
        ["MYSQLUSER"] = c => c.MySQLUser,
        ["NETTTL"] = c => I(c.NetTTL),
        ["NETWORKTHREADS"] = c => I(c.NetworkThreads),
        ["NORESROBE"] = c => B(c.NoResRobe),
        ["NOTOTIMEOUT"] = c => I(c.NotoTimeout),
        ["NOWEATHER"] = c => B(c.NoWeather),
        ["NPCAI"] = c => I(c.NpcAi),
        ["NPCCANFIZZLEONHIT"] = c => B(c.NpcCanFizzleOnHit),
        ["NPCDISTANCEHEAR"] = c => I(c.NpcDistanceHear),
        ["NPCHEALTHRESHOLD"] = c => I(c.NpcHealThreshold),
        ["NPCNOFAMETITLE"] = c => B(c.NpcNoFameTitle),
        ["NPCSHOVENPC"] = c => B(c.NpcShoveNpc),
        ["NPCSKILLSAVE"] = c => I(c.NpcSkillSave),
        ["NPCTRAINPERCENT"] = c => I(c.TrainSkillPercent),
        ["NPCWANDERLOOKAROUNDCHANCE"] = c => I(c.NpcWanderLookAroundChance),
        ["OVERSKILLMULTIPLY"] = c => I(c.OverSkillMultiply),
        // ELEM_BOOL: set by "GetArgVal() > 0".
        ["PACKETDEATHANIMATION"] = c => c.PacketDeathAnimation > 0 ? "1" : "0",
        ["PAYFROMPACKONLY"] = c => B(c.PayFromPackOnly),
        ["PETSINHERITNOTORIETY"] = c => I(c.PetsInheritNotoriety),
        ["PLAYEREVIL"] = c => I(c.PlayerKarmaEvil),
        ["PLAYERNEUTRAL"] = c => I(c.PlayerKarmaNeutral),
        ["RACIALFLAGS"] = c => ((uint)c.RacialFlags).ToString(CultureInfo.InvariantCulture),
        ["REAGENTLOSSABORT"] = c => B(c.ReagentLossAbort),
        ["REAGENTLOSSFAIL"] = c => B(c.ReagentLossFail),
        ["REAGENTSREQUIRED"] = c => B(c.ReagentsRequired),
        ["REVEALFLAGS"] = c => ((uint)c.RevealFlags).ToString(CultureInfo.InvariantCulture),
        ["RUNNINGPENALTY"] = c => I(c.RunningPenalty),
        ["RUNNINGPENALTYOVERWEIGHT"] = c => I(c.RunningPenaltyOverweight),
        ["SAVEBACKGROUND"] = c => I(c.SaveBackgroundMinutes),
        ["SAVEPERIOD"] = c => I(c.SavePeriodMinutes),
        ["SAVESECTORSPERTICK"] = c => I(c.SaveSectorsPerTick),
        ["SAVESTEPMAXCOMPLEXITY"] = c => I(c.SaveStepMaxComplexity),
        ["SECTORSLEEP"] = c => I(c.SectorSleep),
        ["SECURE"] = c => B(c.Secure),
        ["SKILLPRACTICEMAX"] = c => I(c.SkillPracticeMax),
        ["SNOOPCRIMINAL"] = c => B(c.SnoopCriminal),
        ["SPEECHPET"] = c => c.SpeechPet,
        ["SPEECHSELF"] = c => c.SpeechSelf,
        ["SPEEDSCALEFACTOR"] = c => I(c.SpeedScaleFactor),
        ["SPELLTIMEOUT"] = c => I(c.SpellTimeout),
        ["STAMINALOSSATWEIGHT"] = c => I(c.StaminaLossAtWeight),
        ["STAMINALOSSOVERWEIGHT"] = c => I(c.StaminaLossOverweight),
        ["TELEPORTEFFECTNPC"] = c => I(c.TeleportEffectNpc),
        ["TELEPORTEFFECTPLAYERS"] = c => I(c.TeleportEffectPlayers),
        ["TELEPORTEFFECTSTAFF"] = c => I(c.TeleportEffectStaff),
        ["TELEPORTSOUNDNPC"] = c => I(c.TeleportSoundNpc),
        ["TELEPORTSOUNDPLAYERS"] = c => I(c.TeleportSoundPlayers),
        ["TELEPORTSOUNDSTAFF"] = c => I(c.TeleportSoundStaff),
        ["TIMERCALL"] = c => I(c.TimerCallMinutes),
        ["TOOLTIPCACHE"] = c => I(c.ToolTipCache),
        ["TOOLTIPMODE"] = c => I(c.ToolTipMode),
        ["USECRYPT"] = c => B(c.UseCrypt),
        ["USEHTTP"] = c => I(c.UseHttpMode),
        ["USEMAPDIFFS"] = c => B(c.UseMapDiffs),
        ["USENOCRYPT"] = c => B(c.UseNoCrypt),
        ["VENDORMARKUP"] = c => I(c.VendorMarkup),
        ["VENDORMAXSELL"] = c => I(c.VendorMaxSell),
        // Loaded * MSECS_PER_TENTH and written back raw (FormatLLVal(m_iWalkBuffer)).
        ["WALKBUFFER"] = c => ((long)c.WalkBuffer * 100L).ToString(CultureInfo.InvariantCulture),
        ["WALKREGEN"] = c => I(c.WalkRegen),
        ["WOOLGROWTHTIME"] = c => I(c.WoolGrowthTime),
        ["WORLDSAVE"] = c => c.WorldSaveDir,

        // CServerDef::r_WriteVal (CServerDef.cpp:417) keys the main switch lacks.
        ["ACCAPP"] = c => I(c.AccApp),
        ["ACCAPPS"] = c => AccAppName(c.AccApp),
        ["ADMINEMAIL"] = c => c.AdminEmail,
        ["CLIENTVERSION"] = c => c.ClientVersion,
        ["SERVPORT"] = c => I(c.ServPort),
        // SC_URLLINK: the name alone without a URL, else an anchor to it.
        ["URLLINK"] = c => string.IsNullOrEmpty(c.Url)
            ? c.ServName
            : $"<a href=\"https://{c.Url}\">{c.ServName}</a>",
    };

    /// <summary>sm_AccAppTable (CServerDef.cpp:292).</summary>
    private static string AccAppName(int accApp) => accApp switch
    {
        0 => "CLOSED",
        2 => "FREE",
        3 => "GUESTAUTO",
        4 => "GUESTTRIAL",
        6 => "UNSPECIFIED",
        _ => "UNUSED",
    };

    /// <summary>Everything the SERV switch hands on before the defname lookup: the
    /// config table above, then the reference forms CServerConfig answers
    /// (ROOM, SKILLCLASS, CLIENT., MULTIS., TILEDATA.). Null when none claims it.</summary>
    private static string? ResolveServReadback(string property, string upper)
    {
        if (_config != null && s_servConfigReadback.TryGetValue(upper, out var read))
            return read(_config);

        if (upper.StartsWith("ROOM(", StringComparison.Ordinal))
            return ResolveServRoom(property[4..]);
        if (upper.StartsWith("ROOM.", StringComparison.Ordinal))
            return ResolveServRoom(property[5..]);
        if (upper.StartsWith("SKILLCLASS.", StringComparison.Ordinal))
            return ResolveServSkillClass(property[11..]);
        if (upper.StartsWith("CLIENT.", StringComparison.Ordinal))
            return ResolveServClient(property[7..]);
        if (upper.StartsWith("MULTIS.", StringComparison.Ordinal))
            return ResolveServMultis(property[7..]);
        if (upper.StartsWith("TILEDATA.", StringComparison.Ordinal))
            return ResolveServTileData(upper[9..]);
        return null;
    }

    /// <summary>A world object reference (LASTNEWITEM / LASTNEWCHAR): with no key the
    /// uid in hex, "0" when nothing is there; otherwise the key read off the object.</summary>
    private static string ResolveLastNewRef(SphereNet.Core.Types.Serial? uid, string field)
    {
        var obj = _world != null && uid is { IsValid: true } serial ? _world.FindObject(serial) : null;
        if (string.IsNullOrWhiteSpace(field))
            return obj == null ? "0" : $"0{obj.Uid.Value:X}";
        return obj != null && obj.TryGetProperty(field, out string value) ? value : "0";
    }

    /// <summary>Split "(sel).field" or "sel.field" the way CServerConfig::r_GetRef does.</summary>
    private static (string Selector, string Field) SplitRefSelector(string sub)
    {
        if (sub.StartsWith('('))
        {
            int close = sub.IndexOf(')');
            if (close < 0) return (sub[1..].Trim(), "");
            return (sub[1..close].Trim(), sub[(close + 1)..].TrimStart('.'));
        }
        int dot = sub.IndexOf('.');
        return dot < 0 ? (sub.Trim(), "") : (sub[..dot].Trim(), sub[(dot + 1)..]);
    }

    /// <summary>SERV.ROOM(&lt;defname&gt;).&lt;key&gt; / SERV.ROOM.&lt;defname&gt;.&lt;key&gt;
    /// (r_GetRef with RES_ROOM, CServerConfig.cpp:443): the room resource by defname
    /// or index. A bare reference answers 1 when the room exists, as any non-object
    /// reference does (CScriptObj::r_WriteVal, CScriptObj.cpp:505).</summary>
    private static string ResolveServRoom(string sub)
    {
        if (_world == null) return "0";
        var (selector, field) = SplitRefSelector(sub);
        if (selector.Length == 0) return "0";

        SphereNet.Game.World.Regions.Room? room = null;
        var rid = _resources?.ResolveDefName(selector) ?? default;
        bool numeric = int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index);
        foreach (var r in _world.Rooms)
        {
            if ((rid.IsValid && r.ResourceId.Equals(rid)) ||
                (numeric && r.ResourceId.Index == index) ||
                string.Equals(r.Name, selector, StringComparison.OrdinalIgnoreCase))
            {
                room = r;
                break;
            }
        }
        if (room == null) return "0";
        if (string.IsNullOrWhiteSpace(field)) return "1";
        return room.TryGetProperty(field, out string value) ? value : "0";
    }

    /// <summary>SERV.SKILLCLASS.&lt;defname|index&gt;.&lt;key&gt; - CSkillClassDef::r_WriteVal
    /// (CSkillClassDef.cpp:40): NAME, DEFNAME, SKILLSUM, STATSUM, a skill (its cap,
    /// 1000 when the class sets none) or a stat (its cap).</summary>
    private static string ResolveServSkillClass(string sub)
    {
        var (selector, field) = SplitRefSelector(sub);
        var def = int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? DefinitionLoader.GetSkillClassDef(id)
            : DefinitionLoader.GetSkillClassDef(selector);
        if (def == null) return "0";

        string key = field.Trim().ToUpperInvariant();
        switch (key)
        {
            case "": return "1";
            case "NAME": return def.Name;
            case "DEFNAME": return def.DefName ?? "";
            case "SKILLSUM": return I(def.SkillSumMax);
            case "STATSUM": return I(def.StatSumMax);
            case "STR": return I(def.StrMax);
            case "INT": return I(def.IntMax);
            case "DEX": return I(def.DexMax);
        }

        SkillType skill;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int skillIdx) &&
            skillIdx >= 0 && skillIdx < (int)SkillType.Qty)
            skill = (SkillType)skillIdx;
        else if (!Enum.TryParse(key, true, out skill) || skill < 0 || skill >= SkillType.Qty)
            return "0";
        return I(def.SkillCaps.TryGetValue(skill, out int cap) ? cap : 1000);
    }

    /// <summary>SERV.CLIENT.&lt;n&gt;[.&lt;key&gt;] (CServerConfig.cpp "CLIENT."): the n-th
    /// connected client. With no key, 1 when it has a character; otherwise the key is
    /// read off that character. An index past the client count answers nothing.</summary>
    private static string ResolveServClient(string sub)
    {
        var (selector, field) = SplitRefSelector(sub);
        if (_world == null || _clients == null ||
            !int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0)
            return "0";
        var snapshot = BuildAllClientsSnapshot();
        if (index >= snapshot.Count) return "0";
        var target = snapshot[index].Target;
        if (string.IsNullOrWhiteSpace(field)) return target != null ? "1" : "0";
        return target != null && target.TryGetProperty(field, out string value) ? value : "0";
    }

    /// <summary>SERV.MULTIS.COUNT / SERV.MULTIS.&lt;n&gt;.&lt;key&gt; (CServerConfig.cpp
    /// "MULTIS."): houses, custom houses and ships, in world order.</summary>
    private static string ResolveServMultis(string sub)
    {
        if (_world == null) return "0";
        var multis = _world.GetAllObjects().OfType<Item>()
            .Where(i => i.ItemType is ItemType.Multi or ItemType.MultiCustom or ItemType.Ship)
            .ToList();
        string trimmed = sub.Trim();
        if (trimmed.StartsWith("COUNT", StringComparison.OrdinalIgnoreCase))
            return I(multis.Count);

        var (selector, field) = SplitRefSelector(trimmed);
        if (!int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
            index < 0 || index >= multis.Count)
            return "0";
        var multi = multis[index];
        if (string.IsNullOrWhiteSpace(field)) return $"0{multi.Uid.Value:X}";
        return multi.TryGetProperty(field, out string value) ? value : "0";
    }

    /// <summary>SERV.TILEDATA.ITEM(id).&lt;attr&gt; / TERRAIN(id).&lt;attr&gt;
    /// (CServerConfig.cpp "TILEDATA."). Number formats follow the upstream cases:
    /// FLAGS decimal, the byte/word fields FormatBVal/FormatWVal hex. Attributes this
    /// engine's tiledata reader does not keep (UNK, UNK11, HUE, LIGHT) answer nothing.</summary>
    private static string? ResolveServTileData(string upperSub)
    {
        bool terrain;
        string rest;
        if (upperSub.StartsWith("TERRAIN(", StringComparison.Ordinal)) { terrain = true; rest = upperSub[8..]; }
        else if (upperSub.StartsWith("ITEM(", StringComparison.Ordinal)) { terrain = false; rest = upperSub[5..]; }
        else return null;

        int close = rest.IndexOf(')');
        if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != '.') return null;
        var parser = new SphereNet.Scripting.Expressions.ExpressionParser();
        long id = parser.Evaluate(rest.AsSpan(0, close));
        string attr = rest[(close + 2)..].Trim();
        if (_mapData == null || id < 0) return null;

        static string Bh(long v) => v == 0 ? "00" : "0" + v.ToString("X", CultureInfo.InvariantCulture);
        if (terrain)
        {
            if (id >= 0x4000) return null; // TERRAIN_QTY
            var land = _mapData.GetLandTileData((int)id);
            if (attr.StartsWith("FLAGS", StringComparison.Ordinal)) return Bh((uint)land.Flags);
            if (attr.StartsWith("INDEX", StringComparison.Ordinal)) return Bh(land.TextureId);
            if (attr.StartsWith("NAME", StringComparison.Ordinal)) return land.Name ?? "";
            return null;
        }

        var item = _mapData.GetItemTileData((int)id);
        if (attr.StartsWith("FLAGS", StringComparison.Ordinal))
            return ((ulong)item.Flags).ToString(CultureInfo.InvariantCulture);
        if (attr.StartsWith("WEIGHT", StringComparison.Ordinal)) return Bh(item.Weight);
        if (attr.StartsWith("LAYER", StringComparison.Ordinal)) return Bh(item.Quality);
        if (attr.StartsWith("ANIM", StringComparison.Ordinal)) return Bh(item.Animation);
        if (attr.StartsWith("HEIGHT", StringComparison.Ordinal)) return Bh(item.Height);
        if (attr.StartsWith("NAME", StringComparison.Ordinal)) return item.Name ?? "";
        return null;
    }
}
