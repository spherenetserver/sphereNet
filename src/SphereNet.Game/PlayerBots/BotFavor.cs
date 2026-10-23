using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>Something a player asked a bot to do that takes more than one line: a gate
/// (where to, a rune, the reagent money, the cast, the gate held open), a rune marked,
/// a spell cast on them, the way led somewhere, a price looked at, things held or
/// handed back, a present taken.</summary>
public enum BotFavorKind { Gate, Mark, Buff, Guide, Appraise, Carry, Gift }

/// <summary>How far a favour has got.</summary>
public enum BotFavorStage
{
    /// <summary>The bot asked something back ("where to?") and waits for the answer.</summary>
    Asking,
    /// <summary>Waits for the player to put a rune in a trade window.</summary>
    AwaitRune,
    /// <summary>Waits for the reagent money in a trade window.</summary>
    AwaitPay,
    /// <summary>Waits for the player's things in a trade window (carry, a present, a
    /// price check).</summary>
    AwaitGoods,
    /// <summary>Getting it done: walking over, resting for the mana, casting.</summary>
    Working,
    /// <summary>The spell is on its way; its outcome is looked for.</summary>
    Casting,
    /// <summary>The gate stands open.</summary>
    Open,
}

/// <summary>One favour in hand. A player has at most one with the bots at a time
/// (<see cref="BotWatcher.Favor"/>), and a bot works on one at a time
/// (<see cref="BotMind.Favor"/>). Runtime only: a restart forgets an unfinished favour,
/// as a player would have to ask again; what the bot holds for the player is kept on
/// the character (PLAYERBOT.HELD).</summary>
public sealed class BotFavor
{
    public required BotFavorKind Kind { get; init; }
    public required Character Player { get; init; }
    public required BotMind Bot { get; init; }
    public string? Language { get; set; }
    public BotFavorStage Stage { get; set; }
    public long StartedMs { get; init; }
    /// <summary>Given up at this time, whatever stage it is in.</summary>
    public long UntilMs { get; set; }

    /// <summary>The place asked for, as the player said it, and its type
    /// (<c>town:</c>, <c>dest:</c> or a destination type).</summary>
    public string? PlaceType { get; set; }
    public string? PlaceWord { get; set; }
    /// <summary>The rune the gate opens on, or the player's blank rune to mark.</summary>
    public Item? Rune { get; set; }
    public bool RuneIsPlayers { get; set; }
    /// <summary>The spell a buff casts.</summary>
    public SpellType Spell { get; set; }
    /// <summary>The place a guided walk goes to.</summary>
    public BotDestination? To { get; set; }

    /// <summary>The reagent money asked (0: free) and whether it was paid.</summary>
    public long Fee { get; set; }
    public bool Paid { get; set; }

    public long CheckMs { get; set; }
    public int Tries { get; set; }
    public bool SaidMana { get; set; }
    public bool SaidWait { get; set; }

    /// <summary>The gate the bot opened, while it stands.</summary>
    public Item? Gate { get; set; }
    /// <summary>The bot goes through the gate too once the player has.</summary>
    public bool Along { get; set; }
    public bool SteppedAside { get; set; }
}
