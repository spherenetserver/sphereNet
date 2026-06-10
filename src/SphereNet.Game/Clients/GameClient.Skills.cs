using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Skills handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientSkillsHandler"/>.</summary>
    internal ClientSkillsHandler SkillUse => _skillUse ??= new ClientSkillsHandler(this);
    private ClientSkillsHandler? _skillUse;

    internal void BeginInfoSkill(SkillType skill, int skillId) => SkillUse.BeginInfoSkill(skill, skillId);

    internal void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind) =>
        SkillUse.BeginActiveSkill(skill, skillId, kind);

    /// <summary>Advance delayed active skills (@SkillStroke loop + completion).</summary>
    public void TickPendingSkill() => SkillUse.TickPendingSkill();

    /// <summary>Source-X addObjMessage: overhead speech over any ObjBase.</summary>
    internal void ObjectMessage(Objects.ObjBase target, string text) => SkillUse.ObjectMessage(target, text);

    public void HandleHelpRequest() => SkillUse.HandleHelpRequest();

    public void HandleAOSTooltip(uint serial) => SkillUse.HandleAOSTooltip(serial);

    public void HandleTradeRequest(uint targetUid) => SkillUse.HandleTradeRequest(targetUid);

    public void HandlePartyInvite(uint targetUid) => SkillUse.HandlePartyInvite(targetUid);

    public void HandlePartyLeave() => SkillUse.HandlePartyLeave();

    public void HandleClientVersion(string version) => SkillUse.HandleClientVersion(version);

    /// <summary>
    /// Glue between the skill engines and the client's network layer.
    /// Implements both <see cref="Skills.Information.IInfoSkillSink"/> and
    /// <see cref="Skills.Information.IActiveSkillSink"/> so the engines can
    /// emit overhead text, emote poses, sounds, and consume backpack items.
    /// </summary>
    internal sealed class InfoSkillSink : Skills.Information.IActiveSkillSink
    {
        private readonly IClientContext _client;
        public InfoSkillSink(IClientContext client, Character self) { _client = client; Self = self; }
        public Character Self { get; }
        public Random Random => System.Random.Shared;
        public Game.World.GameWorld World => _client.World;

        public void SysMessage(string text) => _client.SysMessage(text);
        public void ObjectMessage(Objects.ObjBase target, string text) => _client.ObjectMessage(target, text);
        public void Emote(string text) => _client.NpcSpeech(Self, text);
        public void Sound(ushort soundId) =>
            _client.BroadcastNearby?.Invoke(Self.Position, 18,
                new PacketSound(soundId, (short)Self.Position.X, (short)Self.Position.Y, Self.Position.Z), 0);

        public void Animation(ushort animId) =>
            _client.BroadcastNearby?.Invoke(Self.Position, 18,
                new PacketAnimation(Self.Uid.Value, animId), 0);

        public Item? FindBackpackItem(Core.Enums.ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            foreach (var it in pack.Contents)
            {
                if (it.ItemType == type) return it;
            }
            // One level deep so common pouches resolve.
            foreach (var it in pack.Contents)
            {
                if (it.ItemType is Core.Enums.ItemType.Container or Core.Enums.ItemType.ContainerLocked)
                {
                    foreach (var inner in it.Contents)
                        if (inner.ItemType == type) return inner;
                }
            }
            return null;
        }

        public void ConsumeAmount(Item item, ushort amount = 1)
        {
            if (item.Amount > amount)
            {
                item.Amount = (ushort)(item.Amount - amount);
                return;
            }
            // Drop from container.
            var holder = _client.World.FindObject(item.ContainedIn);
            if (holder is Item parent) parent.RemoveItem(item);
            item.Delete();
        }

        public void DeliverItem(Item item)
        {
            var pack = Self.Backpack;
            if (pack == null)
            {
                _client.World.PlaceItemWithDecay(item, Self.Position);
                return;
            }

            var actual = pack.AddItemWithStack(item);
            if (actual != item)
                item.Delete();

            _client.NetState.Send(new PacketContainerItem(
                actual.Uid.Value, actual.DispIdFull, 0,
                actual.Amount, actual.X, actual.Y,
                pack.Uid.Value, actual.Hue,
                _client.NetState.IsClientPost6017));
        }

        public void ResurrectTarget(Objects.Characters.Character target)
        {
            if (!target.IsDead) return;
            _client.OnResurrectOther?.Invoke(target);
        }
    }

}
