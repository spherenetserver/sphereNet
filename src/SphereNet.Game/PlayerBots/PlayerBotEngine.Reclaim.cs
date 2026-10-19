using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>Getting the gear back. A bot raised away from its body walks back to it
/// and takes its things off it one at a time through its client - pick up, then wear
/// on the layer it came off or drop into the pack - which is the T2A way: there is
/// no self-loot, a player walked back and dragged it all off the corpse.</summary>
public sealed partial class PlayerBotEngine
{
    internal const string CorpseGoalType = "Corpse";

    private Item? FindOwnCorpse(Character ch)
    {
        foreach (var item in _world.GetItemsInRange(ch.Position, 3))
            if (IsOwnCorpse(item, ch)) return item;
        return null;
    }

    private Serial SavedCorpse(Character ch) =>
        ch.TryGetTag(CorpseTag, out string? text) && ScriptNumber.TryParseUInt(text, out uint uid) &&
        IsOwnCorpse(_world.FindItem(new Serial(uid)), ch)
            ? new Serial(uid)
            : Serial.Invalid;

    private static bool IsOwnCorpse(Item? item, Character ch) =>
        item is { IsDeleted: false, ItemType: ItemType.Corpse } &&
        !item.ContainedIn.IsValid &&
        item.TryGetTag("OWNER_UID", out string? owner) &&
        ScriptNumber.TryParseUInt(owner, out uint uid) && uid == ch.Uid.Value;

    /// <summary>After being raised: head for the corpse, if there still is one.</summary>
    private void StartCorpseTrip(BotMind mind, long now)
    {
        var ch = mind.Char;
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (!IsOwnCorpse(corpse, ch) || mind.Client == null)
        {
            _logger.LogInformation("[playerbots] {Name} no corpse to go back to ({Why})", ch.Name,
                corpse == null ? "gone" : "not its own any more");
            mind.CorpseUid = Serial.Invalid;
            ch.RemoveTag(CorpseTag);
            return;
        }
        // Its own things are coming back: off with the robe it was raised in, so
        // they go back on where they came off instead of into the pack.
        DropResRobe(ch);
        // Long enough for the walk back on a road trip; reset to four minutes at the
        // body (see Arrive).
        mind.ReclaimUntilMs = now + 900_000;
        mind.ReclaimSkipped.Clear();
        mind.CorpseOpened = false;
        int node = Nav.FindNearest(corpse!.Position, 24);
        if (node < 0 || ch.Position.GetDistanceTo(corpse.Position) <= 12)
        {
            // Close, or off the roads: walk straight over.
            mind.Mode = BotMode.Reclaim;
            mind.NextActMs = now + 1500;
            return;
        }
        mind.Goal = new BotDestination
        {
            Name = "corpse",
            Type = CorpseGoalType,
            City = "",
            Pos = corpse.Position,
            NearestWaypoint = node,
        };
        mind.Mode = BotMode.Travel;
        mind.Route = null;
        mind.NextActMs = now + 1500;
    }

    private void ActReclaim(BotMind mind, long now)
    {
        var ch = mind.Char;
        var client = mind.Client;
        var corpse = mind.CorpseUid.IsValid ? _world.FindItem(mind.CorpseUid) : null;
        if (client == null || !IsOwnCorpse(corpse, ch) || now > mind.ReclaimUntilMs)
        {
            FinishReclaim(mind, now, gotAll: false);
            return;
        }

        int dist = ch.Position.GetDistanceTo(corpse!.Position);
        if (dist > 2)
        {
            bool walked = StepToward(mind, corpse.Position, run: true);
            mind.NextActMs = now + (walked ? Movement.MovementEngine.GetMoveDelay(ch.IsMounted, true) : 400);
            return;
        }

        // Open it first, as a player does: only an opened container can be
        // reached into (Source-X m_openedContainers).
        if (!mind.CorpseOpened)
        {
            client.HandleDoubleClick(corpse.Uid.Value);
            mind.CorpseOpened = true;
            mind.NextActMs = now + _rng.Next(500, 1000);
            return;
        }

        Item? next = null;
        foreach (var item in corpse.Contents)
        {
            if (item.IsDeleted || item.TryGetTag("CORPSE_HAIR", out _) || mind.ReclaimSkipped.Contains(item.Uid.Value))
                continue;
            next = item;
            break;
        }
        if (next == null)
        {
            FinishReclaim(mind, now, gotAll: true);
            return;
        }

        client.HandleItemPickup(next.Uid.Value, next.Amount);
        Layer layer = Layer.None;
        if (next.TryGetTag("EQUIPLAYER", out string? layerText) && ScriptNumber.TryParseByte(layerText, out byte lb))
            layer = (Layer)lb;
        if (layer != Layer.None && layer != Layer.Pack && ch.GetEquippedItem(layer) == null)
            client.HandleItemEquip(next.Uid.Value, (byte)layer, ch.Uid.Value);
        else if (ch.Backpack is { } pack)
            client.HandleItemDrop(next.Uid.Value, (short)_rng.Next(44, 140), (short)_rng.Next(65, 140), 0, pack.Uid.Value);

        // Anything the rules would not let it take (too heavy, say) is left.
        if (next.ContainedIn == corpse.Uid)
        {
            client.ReleaseDraggedItem(toGround: false);
            mind.ReclaimSkipped.Add(next.Uid.Value);
        }
        mind.NextActMs = now + _rng.Next(450, 900);
    }

    /// <summary>A body too far to walk back to: the server hands back what is still on
    /// it - worn on the layer it came off when that is free, else into the pack - and
    /// the empty corpse goes. Whatever others looted in the meantime stays theirs.</summary>
    private void HandBack(BotMind mind, Item? corpse)
    {
        var ch = mind.Char;
        mind.CorpseUid = Serial.Invalid;
        ch.RemoveTag(CorpseTag);
        if (!IsOwnCorpse(corpse, ch))
        {
            _logger.LogInformation("[playerbots] {Name} no corpse to hand back ({Why})", ch.Name,
                corpse == null ? "gone" : "not its own any more");
            return;
        }
        int returned = 0;
        var pack = ch.Backpack;
        if (corpse!.Contents.Any()) DropResRobe(ch);
        foreach (var item in corpse.Contents.ToList())
        {
            if (item.IsDeleted) continue;
            corpse.RemoveItem(item);
            // The hair and beard drawn on a corpse are copies; the ghost kept its own.
            if (item.TryGetTag("CORPSE_HAIR", out _))
            {
                _world.DeleteObject(item);
                continue;
            }
            Layer layer = Layer.None;
            if (item.TryGetTag("EQUIPLAYER", out string? layerText) && ScriptNumber.TryParseByte(layerText, out byte lb))
                layer = (Layer)lb;
            item.RemoveTag("EQUIPLAYER");
            if (layer != Layer.None && layer != Layer.Pack && ch.GetEquippedItem(layer) == null && ch.Equip(item, layer))
                returned++;
            else if (pack != null && pack.TryAddItem(item))
                returned++;
            else
            {
                _world.PlaceItem(item, ch.Position);
                returned++;
            }
        }
        _world.DeleteObject(corpse);
        WindowHandBacks++;
        OnAppeared?.Invoke(ch);
        _logger.LogInformation("[playerbots] {Name} handed back {Count} items from a far corpse", ch.Name, returned);
    }

    private void FinishReclaim(BotMind mind, long now, bool gotAll)
    {
        _logger.LogInformation("[playerbots] {Name} reclaim {Result} at {Pos}", mind.Char.Name,
            gotAll ? "done" : "gave up", mind.Char.Position);
        if (gotAll) WindowReclaims++;
        if (gotAll && _rng.Next(100) < 50) Speak(mind, "death_reclaim", fallback: null);
        mind.CorpseUid = Serial.Invalid;
        mind.Char.RemoveTag(CorpseTag);
        mind.ReclaimSkipped.Clear();
        mind.BeginLinger(null, now, _rng, short_: true);
    }
}
