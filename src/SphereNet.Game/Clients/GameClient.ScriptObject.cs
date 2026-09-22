using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    IReadOnlyList<IScriptObj> IScriptObj.QueryScriptObjects(string query, string args, ITriggerArgs? triggerArgs) =>
        QueryScriptObjects(query, this, args, triggerArgs);

    // CClient is a script object in its own right: ARGO must not become the character.
    bool IScriptObj.TryGetProperty(string key, out string value)
    {
        string upper = key.ToUpperInvariant();
        value = "";
        if (upper is "CLIENTVERSION" or "REPORTEDCLIVER")
        {
            value = _netState.ClientVersion;
            return true;
        }
        if (upper.StartsWith("REPORTEDCLIVER.", StringComparison.Ordinal))
        {
            value = _netState.ClientVersionNumber.ToString();
            return true;
        }
        if (upper.StartsWith("ACCOUNT.", StringComparison.Ordinal))
            return _account != null && _account.TryGetProperty(key[8..], out value);
        if (upper is "CLIENTIS3D" or "CLIENTISKR" or "CLIENTISENHANCED")
        {
            byte kind = upper == "CLIENTIS3D" ? (byte)1 : upper == "CLIENTISKR" ? (byte)2 : (byte)3;
            value = _netState.ParsedClientType == kind ? "1" : "0";
            return true;
        }
        if (_character == null) return false;
        if (!IsClientProperty(upper)) return false;
        return TryResolveScriptVariable(key, _character, null, out value) ||
            _character.TryGetProperty(key, out value);
    }

    bool IScriptObj.TrySetProperty(string key, string value)
    {
        if (key.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase))
            return _account != null && _account.TrySetProperty(key[8..], value);
        return _character != null && IsClientProperty(key.ToUpperInvariant()) &&
            _character.TrySetProperty(key, value);
    }

    bool IScriptObj.TryExecuteCommand(string key, string args, ITextConsole source) =>
        _character != null && (TryExecuteScriptCommand(_character, key, args, null) ||
            key.Equals("CLEARCTAGS", StringComparison.OrdinalIgnoreCase) && _character.TryExecuteCommand(key, args, source));

    TriggerResult IScriptObj.OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;

    private static bool IsClientProperty(string key) => key is
        "ALLMOVE" or "ALLSHOW" or "CLIENTIS3D" or "CLIENTISKR" or "CLIENTISENHANCED" or
        "DEBUG" or "DETAIL" or "GM" or "HEARALL" or "PRIVSHOW" or "TARG" or "TARGPRV" or "TARGPROP" or "TARGTXT" or
        "TARGP" or "SCREENSIZE" || key.StartsWith("CTAG.", StringComparison.Ordinal) ||
        key.StartsWith("CTAG0.", StringComparison.Ordinal) || key.StartsWith("TARG.", StringComparison.Ordinal) ||
        key.StartsWith("TARGPRV.", StringComparison.Ordinal) || key.StartsWith("TARGPROP.", StringComparison.Ordinal) ||
        key.StartsWith("TARGP.", StringComparison.Ordinal) || key.StartsWith("SCREENSIZE.", StringComparison.Ordinal);
}
