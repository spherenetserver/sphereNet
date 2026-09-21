using SphereNet.Core.Enums;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    public void HandleTextCommand(byte type, string command, Action<int>? captureSkill = null)
    {
        if (!PrepareTextCommand(type, ref command, out int doorDistance)) return;
        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (type != 0x58 && parts.Length == 0) return;
        switch (type)
        {
            case 0x24:
                if (int.TryParse(parts[0], out int skillId))
                {
                    captureSkill?.Invoke(skillId);
                    HandleUseSkill(skillId);
                }
                break;
            case 0x27: // Cast from book and cast macro share the same path.
            case 0x56:
                if (int.TryParse(parts[0], out int spellId) && spellId > 0)
                    HandleCastSpell((SpellType)spellId, 0);
                break;
            case 0x58:
                OpenDoor(doorDistance);
                break;
            case 0xF4:
                if (parts[0][0] is >= '1' and <= '9')
                    HandleVirtueInvoke(parts[0][0] - '0');
                break;
        }
    }

    // Source-X CClient::Event_ExtCmd: scripts may rewrite ARGS or cancel;
    // changing ARGN1 does not change the command opcode used for dispatch.
    internal bool PrepareTextCommand(byte type, ref string command, out int doorDistance)
    {
        doorDistance = 1;
        if (_character == null || command.Length >= 300) return false;
        if (_triggerDispatcher?.IsTriggerNameUsed("UserExtCmd") != true) return true;

        var raw = new SphereNet.Scripting.Execution.TriggerArgs(_character);
        raw.InitFromRaw(command);
        var args = new TriggerArgs
        {
            CharSrc = _character, N1 = type, N2 = raw.Number2, N3 = raw.Number3,
            S1 = raw.ArgString, Locals = new VarMap()
        };
        if (type == 0x58) args.Locals.Set("DoorAutoDist", "1");
        if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.UserExtCmd, args) == TriggerResult.True)
            return false;
        command = args.S1.Length > 255 ? args.S1[..255] : args.S1;
        if (type == 0x58) doorDistance = (int)Math.Clamp(args.Locals.GetInt("DoorAutoDist"), 0, 14);
        return true;
    }
}
