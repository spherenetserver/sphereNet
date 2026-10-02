using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.Accounts;

/// <summary>
/// Player account. Maps to CAccount in Source-X.
/// Stores credentials, character references, and account-level properties.
/// </summary>
public sealed class Account : IScriptObj
{
    private string _name = "";
    private string _passwordHash = "";
    // Player, as upstream: a new account is PLEVEL_Player and only a GUEST-named one is
    // PLEVEL_Guest (CAccount.cpp:593).
    private PrivLevel _privLevel = PrivLevel.Player;
    private readonly Serial[] _charSlots = new Serial[7];
    private int _charCount;
    private DateTime _lastLogin;
    private DateTime _createDate;
    private string _lastIp = "";
    private uint _totalConnectTime;
    private bool _isBanned;

    // New fields for wiki-complete account property support
    private readonly VarMap _tags = new();
    private string _chatName = "";
    private DateTime _firstConnectDate;
    private string _firstIp = "";
    private Serial _lastCharUid = Serial.Invalid;
    private int _maxChars = 7;
    private bool _guest;
    private bool _jail;
    private string _lang = "";
    private uint _priv;
    private byte _resDisp;
    private uint _lastConnectTime;

    public string Name { get => _name; set => _name = value; }
    public string PasswordHash { get => _passwordHash; set => _passwordHash = value; }
    public PrivLevel PrivLevel
    {
        get => _privLevel;
        set
        {
            _privLevel = value;
            if (value != PrivLevel.Owner) ExtendedPlevelName = null;
        }
    }

    /// <summary>Sphere 56T custom-version compatibility: account files from that version
    /// carry levels above Owner (8 "Founder", 9 "Root"). They run as Owner here and the
    /// original token is written back while the account is still Owner, so the file
    /// keeps loading on the 56T server.</summary>
    public string? ExtendedPlevelName { get; set; }

    /// <summary>Runtime only: an account the engine made for its own use (the load-test
    /// bots, AccountManager.CreateInternalAccount). Never written to the account file
    /// and never loaded from one.</summary>
    public bool IsEngineInternal { get; init; }
    public int CharCount => _charCount;
    public DateTime LastLogin { get => _lastLogin; set => _lastLogin = value; }
    public DateTime CreateDate { get => _createDate; set => _createDate = value; }
    public string LastIp { get => _lastIp; set => _lastIp = value; }
    public uint TotalConnectTime { get => _totalConnectTime; set => _totalConnectTime = value; }
    /// <summary>Length of the previous in-game session in minutes (Source-X LASTCONNECTTIME).</summary>
    public uint LastConnectTime { get => _lastConnectTime; set => _lastConnectTime = value; }
    public bool IsBanned { get => _isBanned; set => _isBanned = value; }
    public VarMap Tags => _tags;
    public string ChatName { get => _chatName; set => _chatName = value; }
    public DateTime FirstConnectDate { get => _firstConnectDate; set => _firstConnectDate = value; }
    public string FirstIp { get => _firstIp; set => _firstIp = value; }
    public Serial LastCharUid { get => _lastCharUid; set => _lastCharUid = value; }
    public int MaxChars { get => _maxChars; set => _maxChars = Math.Clamp(value, 1, 7); }
    public bool Guest { get => _guest; set => _guest = value; }
    public bool Jail { get => _jail; set => _jail = value; }
    public string Lang { get => _lang; set => _lang = value; }
    public uint Priv { get => _priv; set => _priv = value; }
    public byte ResDisp { get => _resDisp; set => _resDisp = value; }

    public Account()
    {
        Array.Fill(_charSlots, Serial.Invalid);
        _createDate = DateTime.UtcNow;
    }

    public Serial GetCharSlot(int index) =>
        index >= 0 && index < _charSlots.Length ? _charSlots[index] : Serial.Invalid;

    public bool SetCharSlot(int index, Serial charUid)
    {
        if (index < 0 || index >= _charSlots.Length) return false;
        _charSlots[index] = charUid;
        _charCount = _charSlots.Count(s => s.IsValid);
        return true;
    }

    public int FindFreeSlot()
    {
        for (int i = 0; i < _charSlots.Length; i++)
        {
            if (!_charSlots[i].IsValid) return i;
        }
        return -1;
    }

    public string[] GetCharNames(Func<Serial, string?> nameResolver)
    {
        var names = new List<string>();
        foreach (var uid in _charSlots)
        {
            if (uid.IsValid)
                names.Add(nameResolver(uid) ?? "?");
            else
                names.Add("");
        }
        return names.ToArray();
    }

    // TAG methods
    public void SetTag(string key, string value) => _tags.Set(key, value);
    public bool TryGetTag(string key, out string value)
    {
        var val = _tags.Get(key);
        value = val ?? "";
        return val != null;
    }
    public bool RemoveTag(string key) => _tags.Remove(key);

    /// <summary>Source-X MD5PASSWORDS for this account's shard. Set from
    /// AccountManager on create and on load; decides how SetPassword stores.</summary>
    public bool UseMd5Passwords { get; set; } = true;

    public bool CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(_passwordHash)) return false;
        return Core.Configuration.PasswordHelper.Verify(password, _passwordHash, UseMd5Passwords);
    }

    public void SetPassword(string password)
    {
        // The flag used to be stored and never read, so MD5PASSWORDS=0 silently did
        // nothing. Source-X CAccount::SetPassword branches on it.
        _passwordHash = Core.Configuration.PasswordHelper.StoreForm(password, UseMd5Passwords);
    }

    /// <summary>The script side of the account lifecycle (the f_onaccount_* hooks).
    /// Null - the default, and what every load path relies on - runs nothing, the way
    /// Source-X skips the hooks while IsLoadingGeneric.</summary>
    public static IAccountScriptHooks? ScriptHooks { get; set; }

    /// <summary>Source-X CAccount::SetPassword at runtime (CAccount.cpp:986-1006):
    /// f_onaccount_pwchange runs first and RETURN 1 keeps the old password. False when
    /// a script refused the change.</summary>
    public bool ChangePassword(string password)
    {
        if (ScriptHooks?.PasswordChange(this, password, _passwordHash) == true)
            return false;
        SetPassword(password);
        return true;
    }

    /// <summary>Source-X CAccount::SetBlockStatus (CAccount.cpp:395-420): only an actual
    /// change of the blocked state runs f_onaccount_block / f_onaccount_unblock, and
    /// RETURN 1 leaves the state as it was. True when the state changed.</summary>
    public bool SetBlockStatus(bool blocked)
    {
        if (_isBanned == blocked)
            return false;
        if (ScriptHooks?.BlockChange(this, blocked) == true)
            return false;
        _isBanned = blocked;
        return true;
    }

    /// <summary>Source-X CAccount::CheckPassword (CAccount.cpp:935-984), the login-time
    /// check: an account with no password takes the one offered (through
    /// f_onaccount_pwchange), then f_onaccount_connect runs before the comparison -
    /// RETURN 1 refuses the password, RETURN 6 accepts it without comparing.</summary>
    public bool CheckLoginPassword(string password)
    {
        if (string.IsNullOrEmpty(_passwordHash) && !ChangePassword(password))
            return false;
        switch (ScriptHooks?.Connect(this, password) ?? AccountConnectVerdict.Default)
        {
            case AccountConnectVerdict.Deny: return false;
            case AccountConnectVerdict.SkipCheck: return true;
        }
        return CheckPassword(password);
    }

    public string GetName() => _name;

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        // TAG.name / TAG0.name - CAccount::r_WriteVal AC_TAG (CAccount.cpp:1345):
        // GetKeyStr(key, fZero), a number var in the DECIMALVARIABLES format.
        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
        {
            value = _tags.GetKeyStr(key[4..], zero: false);
            return true;
        }
        if (upper.StartsWith("TAG0.", StringComparison.Ordinal))
        {
            value = _tags.GetKeyStr(key[5..], zero: true);
            return true;
        }

        switch (upper)
        {
            case "ACCOUNT":
            case "NAME": value = _name; return true;
            case "BLOCK":
            case "BANNED": value = _isBanned ? "1" : "0"; return true;
            case "CHARS":
            case "CHARCOUNT": value = _charCount.ToString(); return true;
            case "CHATNAME": value = _chatName; return true;
            case "CREATEDATE": value = _createDate.ToString("O"); return true;
            case "FIRSTCONNECTDATE": value = FormatConnectDate(_firstConnectDate); return true;
            case "FIRSTIP": value = _firstIp; return true;
            case "GUEST": value = _guest ? "1" : "0"; return true;
            case "JAIL": value = _jail ? "1" : "0"; return true;
            case "LANG": value = _lang; return true;
            case "LASTCHARUID": value = _lastCharUid.IsValid ? $"0{_lastCharUid.Value:X8}" : "0"; return true;
            case "LASTCONNECTDATE": value = FormatConnectDate(_lastLogin); return true;
            case "LASTCONNECTTIME": value = _lastConnectTime.ToString(); return true;
            case "LASTLOGIN": value = _lastLogin.ToString("O"); return true;
            case "LASTIP": value = _lastIp; return true;
            case "MAXCHARS": value = _maxChars.ToString(); return true;
            case "PLEVEL": value = ((int)_privLevel).ToString(); return true;
            case "PRIV": value = _priv.ToString(); return true;
            case "RESDISP": value = _resDisp.ToString(); return true;
            case "TAGCOUNT": value = _tags.Count.ToString(); return true;
            case "TOTALCONNECTTIME": value = _totalConnectTime.ToString(); return true;
            default:
                // CHAR.n
                if (upper.StartsWith("CHAR.", StringComparison.Ordinal) && int.TryParse(upper.AsSpan(5), out int slotIdx))
                {
                    var uid = GetCharSlot(slotIdx);
                    value = uid.IsValid ? $"0{uid.Value:X8}" : "0";
                    return true;
                }
                return false;
        }
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        return false;
    }

    public bool TrySetProperty(string key, string value)
    {
        var upper = key.ToUpperInvariant();

        // TAG.name
        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
        {
            // CAccount::r_LoadVal AC_TAG (CAccount.cpp:1492): SetStr with the quote flag.
            _tags.SetStr(key[4..], SphereNet.Scripting.Execution.ScriptArgQuoting.IsQuoted(value), value);
            return true;
        }
        if (upper.StartsWith("TAG0.", StringComparison.Ordinal))
        {
            _tags.SetStr(key[5..], SphereNet.Scripting.Execution.ScriptArgQuoting.IsQuoted(value), value,
                deleteZero: true);
            return true;
        }

        switch (upper)
        {
            case "BLOCK":
            case "BANNED":
                // A script write is a runtime change: it goes through the block hooks
                // (CAccount.cpp:1378-1392 -> SetBlockStatus).
                SetBlockStatus(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
                return true;
            case "CHATNAME":
                _chatName = value;
                return true;
            case "FIRSTCONNECTDATE":
                if (DateTime.TryParse(value, out var fcd)) _firstConnectDate = fcd;
                return true;
            case "FIRSTIP":
                _firstIp = value;
                return true;
            case "GUEST":
                _guest = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "JAIL":
                _jail = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "LANG":
                _lang = value;
                return true;
            case "LASTCHARUID":
                if (TryParseHexOrDec(value, out uint lcuid))
                    _lastCharUid = new Serial(lcuid);
                return true;
            case "LASTCONNECTDATE":
            case "LASTLOGIN":
                if (DateTime.TryParse(value, out var lcd)) _lastLogin = lcd;
                return true;
            case "LASTIP":
                _lastIp = value;
                return true;
            case "MAXCHARS":
                if (int.TryParse(value, out int mc))
                    _maxChars = Math.Clamp(mc, 1, 7);
                return true;
            case "PASSWORD":
            case "NEWPASSWORD":
                ChangePassword(value);
                return true;
            case "PLEVEL":
                if (int.TryParse(value, out int pl) && pl >= (int)PrivLevel.Guest && pl <= (int)PrivLevel.Owner)
                {
                    _privLevel = (PrivLevel)pl;
                    return true;
                }
                return false;
            case "PRIV":
                if (uint.TryParse(value, out uint pv)) _priv = pv;
                return true;
            case "RESDISP":
                if (byte.TryParse(value, out byte rd)) _resDisp = rd;
                return true;
            case "TOTALCONNECTTIME":
                if (uint.TryParse(value, out uint tct)) _totalConnectTime = tct;
                return true;
            case "LASTCONNECTTIME":
                if (uint.TryParse(value, out uint lct)) _lastConnectTime = lct;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Source-X CSTime::Format(nullptr): local "yyyy/MM/dd HH:mm:ss",
    /// empty when the date was never recorded.</summary>
    public static string FormatConnectDate(DateTime date)
    {
        if (date == default) return "";
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        return local.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Source-X CAccount::OnLogin: every login refreshes LASTIP; until
    /// the account has any recorded play time the login also (re)stamps
    /// FIRSTIP / FIRSTCONNECTDATE.</summary>
    public void RecordLogin(string ip)
    {
        _lastIp = ip;
        if (_totalConnectTime == 0)
        {
            _firstIp = ip;
            _firstConnectDate = DateTime.UtcNow;
        }
    }

    /// <summary>Source-X Setup_Play: the previous LASTCONNECTDATE moves to
    /// TAG.LastLogged, then LASTCONNECTDATE becomes now.</summary>
    public void RecordCharacterEnter()
    {
        _tags.Set("LastLogged", FormatConnectDate(_lastLogin));
        _lastLogin = DateTime.UtcNow;
    }

    /// <summary>Source-X CAccount::OnLogout (with a character): the session
    /// length in minutes becomes LASTCONNECTTIME and is added to TOTALCONNECTTIME.</summary>
    public void RecordLogout(TimeSpan session)
    {
        uint minutes = session <= TimeSpan.Zero ? 0u : (uint)Math.Min(session.TotalMinutes, uint.MaxValue);
        _lastConnectTime = minutes;
        _totalConnectTime += minutes;
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
    {
        return TriggerResult.Default;
    }

    private static bool TryParseHexOrDec(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(val, out result);
    }
}
