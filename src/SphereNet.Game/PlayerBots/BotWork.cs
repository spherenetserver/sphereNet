using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.PlayerBots;

/// <summary>The kinds of job a bot does for a living: work a resource (ore, wood,
/// fish), smelt ore at a forge, make things of its craft, sell goods to a vendor.</summary>
public enum BotJob { Gather, Smelt, Craft, Sell }

/// <summary>How far a job has got: on the road to its place, or at it.</summary>
public enum BotWorkStage { Trip, Site }

/// <summary>A gathering trade (work.txt <c>gather</c>): the skill, the type of tool
/// whose double click starts it, and the kinds of place it is plied at.</summary>
public sealed record BotGatherTrade(SkillType Skill, ItemType Tool, string[] Places);

/// <summary>A craft (work.txt <c>craft</c>): the skill, its tool (held in the hand for
/// a smith, carried for the rest), the item types it works with - kept, never sold,
/// bought when the bot gathers nothing itself - and the kinds of place it is plied at.</summary>
public sealed record BotCraftTrade(SkillType Skill, ItemType Tool, bool InHand, ItemType[] Materials, string[] Places);

/// <summary>Where goods of an item type are sold (work.txt <c>sell</c>); a null type
/// is every kind not named by another line.</summary>
public sealed record BotSaleRule(ItemType? Type, string[] Places);

/// <summary>
/// One job in hand, from the trip to its place until it ends. Runtime only: what a
/// restart needs - the job, its skill and its place - is the character's
/// PLAYERBOT.WORK tag, and the job is taken up again from that.
/// </summary>
public sealed class BotWork
{
    public required BotJob Job { get; init; }
    /// <summary>The gathering or crafting skill; Mining for a smelt, None for a sale.</summary>
    public SkillType Skill { get; init; } = SkillType.None;
    /// <summary>The place worked at.</summary>
    public required BotDestination Place { get; init; }
    /// <summary>What the trip there is planned to: the place, or - for one the data
    /// gives no stopping spot - the end of its road.</summary>
    public required BotDestination TripGoal { get; init; }
    /// <summary>The item type a sale is for (it chose the shop).</summary>
    public ItemType SaleType { get; init; }
    public BotWorkStage Stage { get; set; } = BotWorkStage.Trip;
    public long StartedMs { get; init; }
    /// <summary>When the shift is over.</summary>
    public long UntilMs { get; set; }

    // What the bot was told since its last action: the outcome of a swing, a smelt,
    // a craft is read from this.
    private readonly List<string> _told = [];
    public IReadOnlyList<string> Told => _told;
    public void Hear(string text)
    {
        if (_told.Count < 16) _told.Add(text);
    }
    public void ForgetTold() => _told.Clear();

    // Gathering: the tile being worked, the tiles found empty or out of reach, the
    // point walked to when nothing is in sight.
    public Point3D? Tile { get; set; }
    public ushort TileGraphic { get; set; }
    public HashSet<(int X, int Y)> Spent { get; } = [];
    public long NextScanMs { get; set; }
    public Point3D? RoamTo { get; set; }
    public int Roams { get; set; }
    /// <summary>The skill is running: its strokes are still to come.</summary>
    public bool Busy { get; set; }
    /// <summary>What the pack held when the action started (a gain is a success).</summary>
    public int UnitsBefore { get; set; }
    /// <summary>Actions in a row that did nothing at all.</summary>
    public int Misfires { get; set; }
    /// <summary>Tiles in a row that gave nothing.</summary>
    public int DryTiles { get; set; }
    /// <summary>Swings in a row on this tile that ran and gave nothing.</summary>
    public int Fails { get; set; }
    /// <summary>Successful swings, smelts, pieces made, sales this job.</summary>
    public int Done { get; set; }

    // A forge, a fire: the tile the work is done at.
    public Point3D? Site { get; set; }
    public int SiteTries { get; set; }

    // Smelting: piles that would not melt. Crafting: the piece being made.
    public HashSet<uint> Skipped { get; } = [];
    public CraftRecipe? Recipe { get; set; }
    public int ToolTries { get; set; }
    /// <summary>Double clicks spent taking a gathering tool into the hand.</summary>
    public int EquipTries { get; set; }

    // Selling: the vendor, whether its list is open, the rounds sold.
    public Character? Vendor { get; set; }
    public bool ListOpen { get; set; }
    public int Rounds { get; set; }
}
