using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>What a bot standing about a place busies itself with.</summary>
public enum BotIdleKind
{
    /// <summary>Nothing chosen yet (or the stay changed): choose again.</summary>
    None,
    /// <summary>Stands where it is, facing the middle of the place.</summary>
    Stand,
    /// <summary>By the banker, its box opened the way players open it.</summary>
    Banker,
    /// <summary>On a chair or a bench, with a drink.</summary>
    Seat,
    /// <summary>Looking over a vendor's wares (a healer, a stable master).</summary>
    Vendor,
    /// <summary>Watching the forge or the anvil.</summary>
    Craft,
    /// <summary>Practising on a training dummy or an archery butte.</summary>
    Train,
    /// <summary>Looking at the water.</summary>
    Water,
    /// <summary>Reading the bulletin board.</summary>
    Board,
}

/// <summary>A ghost on its own way to be raised.</summary>
public enum BotGhostStage
{
    None,
    /// <summary>Walking the road to a healer.</summary>
    Walking,
    /// <summary>There: by the healer, or at the ankh.</summary>
    AtHealer,
}

/// <summary>The presence-and-manners part of a bot's mind: where it looks, what it is
/// busy with while it stands about, whom it thanked, the pace it walks at, and how
/// far a ghost has got on its own way to a healer. Runtime only.</summary>
public sealed class BotManners
{
    // Body language
    public long NextGestureMs { get; set; }
    public long NextEmoteMs { get; set; }
    public long NextGlanceMs { get; set; }
    public long NextEatMs { get; set; }
    /// <summary>It went to eat and had nothing: food is on its shopping list.</summary>
    public bool WantsFood { get; set; }
    /// <summary>The one it is talking with: it keeps facing them until the time noted.</summary>
    public Character? LookAt { get; set; }
    public long LookUntilMs { get; set; }
    /// <summary>When it may next stop on the road for someone who spoke to it.</summary>
    public long NextStopMs { get; set; }

    // Standing about with a purpose
    public BotIdleKind Idle { get; set; }
    /// <summary>The stay the plan was made for (the place and where the bot stood).</summary>
    public BotDestination? IdleAt { get; set; }
    public Point3D IdleAnchor { get; set; }
    public long IdleUntilMs { get; set; }
    public Character? IdleChar { get; set; }
    public Item? IdleItem { get; set; }
    public Point3D IdleSpot { get; set; }
    /// <summary>The once-per-stay part is done (the box opened, the board read, seated).</summary>
    public bool IdleDone { get; set; }
    /// <summary>When it last opened its bank box by saying so.</summary>
    public long BankSaidMs { get; set; }

    // Reactions
    public Dictionary<uint, long> Thanked { get; } = [];
    public long NextBusyReplyMs { get; set; }
    public long NextFightReactMs { get; set; }
    public long NextTheftReactMs { get; set; }
    /// <summary>A few steps away from a fight: where to, until when.</summary>
    public Point3D? BackOffTo { get; set; }
    public long BackOffUntilMs { get; set; }
    public bool BackOffRun { get; set; }
    /// <summary>A player standing on its tile, or walking after it: who, since when.</summary>
    public uint CrowdedBy { get; set; }
    public long CrowdedSinceMs { get; set; }
    public long NextCrowdedSayMs { get; set; }
    public uint FollowedBy { get; set; }
    public long FollowedSinceMs { get; set; }
    public Point3D FollowedFrom { get; set; }
    public long NextFollowedSayMs { get; set; }

    // A ghost's own way to a healer
    public BotGhostStage Ghost { get; set; }
    /// <summary>When the walk (or the wait at the healer) is given up for a lift.</summary>
    public long GhostUntilMs { get; set; }
    public bool GhostPleaded { get; set; }
    /// <summary>The walk was tried and is not tried again this death.</summary>
    public bool GhostWalkTried { get; set; }
    /// <summary>The death this state belongs to (the mind's DeadSinceMs).</summary>
    public long GhostDeathMs { get; set; }

    /// <summary>The healer a lift of this death goes to, once worked out.</summary>
    public BotDestination? LiftHealer { get; set; }
    public long LiftHealerDeathMs { get; set; }
    /// <summary>This mind noted (or took up) the death its character's tag tells of.</summary>
    public bool DeathNoted { get; set; }
    /// <summary>The last open ground it stood on while alive: where a death underground
    /// belongs to (the mouth it went in by).</summary>
    public Point3D LastSurface { get; set; }
    /// <summary>On its way back to a body behind portals: until when, portals taken.</summary>
    public long CorpseRunUntilMs { get; set; }
    public int CorpseRunHops { get; set; }

    // Pace on the road
    public long PaceUntilMs { get; set; }
    public bool PaceRun { get; set; }
    /// <summary>Whether a player saw the bot when its pace was last looked at.</summary>
    public bool PaceSeen { get; set; }
    /// <summary>The route and leg the last step was taken on (a new leg is a junction).</summary>
    public List<int>? PaceRoute { get; set; }
    public int PaceLeg { get; set; } = -1;

    public void EndGhost()
    {
        Ghost = BotGhostStage.None;
        GhostUntilMs = 0;
        GhostPleaded = false;
        GhostWalkTried = false;
    }

    public void EndIdle()
    {
        Idle = BotIdleKind.None;
        IdleAt = null;
        IdleChar = null;
        IdleItem = null;
        IdleDone = false;
    }
}
