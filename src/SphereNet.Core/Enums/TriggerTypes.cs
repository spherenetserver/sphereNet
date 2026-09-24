namespace SphereNet.Core.Enums;

/// <summary>
/// Item trigger types. Maps exactly to ITRIG_TYPE in Source-X CObjBase.h.
/// These are the script events that can fire on items (@Create, @DClick, etc.).
/// </summary>
public enum ItemTrigger
{
    AddObj = 1,
    AddRedCandle,
    AddWhiteCandle,
    AfterClick,
    Buy,
    CarveCorpse,
    Click,
    ClientTooltip,
    ClientTooltipAfterDefault,
    Complete,
    ContextMenuRequest,
    ContextMenuSelect,
    Create,
    Damage,
    DClick,
    DelObj,
    DelRedCandle,
    DelWhiteCandle,
    Destroy,
    DropOnChar,
    DropOnGround,
    DropOnItem,
    DropOnSelf,
    DropOnTrade,
    Dye,
    Equip,
    EquipTest,
    GetHit,
    Hit,
    Level,
    MemoryEquip,
    PickupGround,
    PickupPack,
    PickupSelf,
    PickupStack,
    PreSpawn,
    Redeed,
    RegionEnter,
    RegionLeave,
    ResourceGather,
    ResourceTest,
    Sell,
    ShipMove,
    ShipStop,
    ShipTurn,
    Smelt,
    Spawn,
    SpellEffect,
    Start,
    Step,
    Stop,
    TargOnCancel,
    TargOnChar,
    TargOnGround,
    TargOnItem,
    Timer,
    Tooltip,
    Unequip,

    // Spoken speech heard by a nearby item (Source-X item/multi OnHear coverage).
    // Appended (not alphabetical) so existing trigger values are unchanged.
    Hear,

    Qty,
}

/// <summary>
/// Character trigger types. Maps to CTRIG_TYPE in Source-X CObjBase.h.
/// Includes both character-specific and mirrored item triggers.
/// </summary>
public enum CharTrigger : short
{
    // Character-specific triggers
    AfterClick = 1,
    Attack,
    CallGuards,
    CharAttack,
    CharClick,
    CharClientTooltip,
    CharContextMenuRequest,
    CharContextMenuSelect,
    CharDClick,
    CharTradeAccepted,
    Click,
    ClientTooltip,
    ClientTooltipAfterDefault,
    CombatAdd,
    CombatDelete,
    CombatEnd,
    CombatStart,
    ContextMenuRequest,
    ContextMenuSelect,
    Create,
    CreateLoot,
    Criminal,
    DClick,
    Death,
    DeathCorpse,
    Destroy,
    Dismount,
    Drink,
    Eat,
    EffectAdd,
    EnvironChange,
    ExpChange,
    ExpLevelChange,
    FameChange,
    Follow,
    GetHit,
    Hit,
    HitCheck,
    HitIgnore,
    HitMiss,
    HitParry,
    HitTry,
    HouseDesignCommit,
    HouseDesignExit,
    Hunger,
    Jail,
    KarmaChange,
    Kill,
    LogIn,
    LogOut,
    Mount,
    MurderDecay,
    MurderMark,
    NotoSend,
    NPCAcceptItem,
    NPCActCast,
    NPCActFight,
    NPCActFollow,
    NPCAction,
    NPCActWander,
    NPCHearGreeting,
    NPCHearUnknown,
    NPCLookAtChar,
    NPCLookAtItem,
    NPCLostTeleport,
    NPCRefuseItem,
    NPCRestock,
    NPCSeeNewPlayer,
    NPCSeeWantItem,
    NPCSpecialAction,
    PartyAdd,
    PartyDisband,
    PartyInvite,
    PartyLeave,
    PartyRemove,
    PersonalSpace,
    PetDesert,
    PetRelease,
    Profile,
    ReceiveItem,
    RegionEnter,
    RegionLeave,
    RegionStep,
    Rename,
    RoomEnter,
    RoomLeave,
    RoomStep,
    Resurrect,
    Reveal,
    SeeCrime,
    SeeSnoop,
    SkillAbort,
    SkillChange,
    SkillFail,
    SkillGain,
    SkillMakeItem,
    SkillMenu,
    SkillPreStart,
    SkillSelect,
    SkillStart,
    SkillStroke,
    SkillSuccess,
    SkillTargetCancel,
    SkillUseQuick,
    SkillWait,
    SpellBook,
    SpellCast,
    SpellEffect,
    SpellEffectAdd,
    SpellEffectRemove,
    SpellEffectTick,
    SpellFail,
    SpellInterrupt,
    SpellSelect,
    SpellSuccess,
    SpellTargetCancel,
    StatChange,
    StepStealth,
    Targon_Cancel,
    ToolTip,
    TradeAccepted,
    TradeClose,
    TradeCreate,

    // Source-X character triggers that live outside the alphabetical run above.
    AfkMode,
    charShove,
    DelMulti,
    Falling,
    FollowersUpdate,
    HouseDesignCommitItem,
    PayGold,
    SeeHidden,
    ToggleFlying,

    // Mirrored item triggers on character (CTRIG_item*)
    itemAfterClick,
    itemBuy,
    itemClick,
    itemClientTooltip,
    itemContextMenuRequest,
    itemContextMenuSelect,
    itemCreate,
    itemDamage,
    itemDClick,
    itemDestroy,
    itemDropOnChar,
    itemDropOnGround,
    itemDropOnItem,
    itemDropOnSelf,
    itemDropOnTrade,
    itemEquip,
    itemEquipTest,
    itemMemoryEquip,
    itemPickupGround,
    itemPickupPack,
    itemPickupSelf,
    itemPickupStack,
    itemSell,
    itemSpellEffect,
    itemStep,
    itemTargOnCancel,
    itemTargOnChar,
    itemTargOnGround,
    itemTargOnItem,
    itemTimer,
    itemToolTip,
    itemUnequip,

    // Packet hooks
    UserBugReport,
    UserChatButton,
    UserExtCmd,
    UserExWalkLimit,
    UserGlobalChatButton,
    UserGuildButton,
    UserKRToolbar,
    UserMailBag,
    UserQuestArrowClick,
    UserQuestButton,
    UserSkills,
    UserSpecialMove,
    UserStats,
    UserUltimaStoreButton,
    UserVirtue,
    UserVirtueInvoke,
    UserWarmode,

    // Source-X multi/custom-housing hooks. Appended to preserve the numeric
    // values of the existing compatibility enum members.
    AddMulti,
    HouseDesignBegin,

    /// <summary>Fired on the gatherer when a resource vein is first found under a
    /// tile, with the resource marker as the object argument (CWorldMap.cpp:157).
    /// RETURN 1 empties the vein - the spot holds nothing.</summary>
    RegionResourceFound,

    /// <summary>Fired on the gatherer as a swing takes from the vein, alongside the
    /// resource definition's own @ResourceGather (CCharSkill.cpp:1035). ARGN1 is the
    /// amount, LOCAL.ResourceID the item about to be produced; RETURN 1 takes
    /// nothing.</summary>
    RegionResourceGather,

    /// <summary>Fired on the defender when reactive armour is about to bounce damage
    /// back at the attacker (CCharFight.cpp:965). The reflection is described in
    /// LOCAL.Sound/EffectID/Damage/ReflectDamage/ReduceDamage/DamageType and read
    /// back after the script runs.</summary>
    HitReactive,

    /// <summary>Fired on the character each time one of its four regenerating stats
    /// comes due (CCharStat.cpp:538-576). LOCAL.StatID (0 hits, 1 mana, 2 stamina,
    /// 3 food), LOCAL.Value (the amount), LOCAL.StatLimit, LOCAL.FocusValue (mana and
    /// stamina) and LOCAL.HitsHungerLoss (food) are read back; RETURN 1 skips this
    /// regeneration.</summary>
    RegenStat,

    /// <summary>Fired on the character when the ARROWQUEST verb points its quest
    /// arrow at x,y (CClientMsg.cpp:585-588). ARGN1 = x, ARGN2 = y, ARGN3 = the
    /// arrow id.</summary>
    ArrowQuestAdd,

    /// <summary>Fired on the character when ARROWQUEST clears the arrow (x of 0).
    /// Same arguments as <see cref="ArrowQuestAdd"/>.</summary>
    ArrowQuestClose,

    /// <summary>Fired on the character whose paperdoll is about to be sent, with the
    /// viewer as SRC, before the name line is built (CClientMsg.cpp addCharPaperdoll).
    /// A script can change TITLE or TAG.NAME.* here; the return value is ignored.</summary>
    SendPaperdoll,

    Qty,
}
