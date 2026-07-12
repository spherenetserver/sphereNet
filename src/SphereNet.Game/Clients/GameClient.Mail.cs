using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Scripting;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>
    /// Handles the legacy 0xBB mail-bag carrier. Source-X ignores the second
    /// serial, fires @UserMailBag on the recipient with the sender as SRC, and
    /// only then delivers the notification.
    /// </summary>
    public void HandleMailMessage(uint targetSerial, uint attachmentSerial)
    {
        _ = attachmentSerial;
        if (_character == null)
            return;

        var target = _world.FindChar(new Serial(targetSerial));
        if (target == null)
        {
            SysMessage(ServerMessages.Get(Msg.MsgMailbagDrop1));
            return;
        }

        if (_triggerDispatcher?.IsCharTriggerUsed(CharTrigger.UserMailBag) == true)
        {
            var args = new TriggerArgs
            {
                CharSrc = _character,
                ScriptConsole = this,
            };
            if (_triggerDispatcher.FireCharTrigger(target, CharTrigger.UserMailBag, args) == TriggerResult.True)
                return;
        }

        if (ReferenceEquals(target, _character))
            return;

        string text = ServerMessages.GetFormatted(Msg.MsgMailbagDrop2, _character.GetName());
        SendToChar?.Invoke(target.Uid, new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", text));
    }
}
