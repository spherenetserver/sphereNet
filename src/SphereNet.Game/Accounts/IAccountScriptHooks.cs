using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.Accounts;

/// <summary>
/// The account lifecycle questions the script layer answers - Source-X's
/// f_onaccount_* global functions. Each runs on the server object, which is also SRC,
/// with ARGS = the account name (CAccount.cpp). A true answer is a script veto.
/// </summary>
public interface IAccountScriptHooks
{
    /// <summary>f_onaccount_create (CAccounts::Account_Add, CAccount.cpp:231-249).</summary>
    bool Create(Account account);

    /// <summary>f_onaccount_delete (CAccounts::Account_Delete, CAccount.cpp:212-229).</summary>
    bool Delete(Account account);

    /// <summary>f_onaccount_block / f_onaccount_unblock (CAccount::SetBlockStatus,
    /// CAccount.cpp:395-420), asked only when the state actually changes.</summary>
    bool BlockChange(Account account, bool block);

    /// <summary>f_onaccount_pwchange with LOCAL.password / LOCAL.oldPassword
    /// (CAccount::SetPassword, CAccount.cpp:996-1006).</summary>
    bool PasswordChange(Account account, string newPassword, string oldPassword);

    /// <summary>f_onaccount_connect with LOCAL.Account / LOCAL.Password, before the
    /// password is compared (CAccount::CheckPassword, CAccount.cpp:947-955).</summary>
    AccountConnectVerdict Connect(Account account, string password);
}

/// <summary>What f_onaccount_connect decided.</summary>
public enum AccountConnectVerdict
{
    /// <summary>No RETURN 1/6: compare the password as usual.</summary>
    Default,
    /// <summary>RETURN 1 (TRIGRET_RET_TRUE): the password is refused.</summary>
    Deny,
    /// <summary>RETURN 6 (TRIGRET_RET_HALFBAKED): accepted without comparing - the
    /// script did the check itself.</summary>
    SkipCheck,
}

/// <summary>The f_onaccount_* hooks run through the script engine, on the server object.</summary>
public sealed class ScriptAccountHooks : IAccountScriptHooks
{
    /// <summary>Source-X TRIGRET_RET_HALFBAKED.</summary>
    private const long ReturnHalfBaked = 6;

    private readonly ScriptSystemHooks _hooks;
    private readonly IScriptObj _server;

    public ScriptAccountHooks(ScriptSystemHooks hooks, IScriptObj server)
    {
        _hooks = hooks;
        _server = server;
    }

    public bool Create(Account account) => Veto("f_onaccount_create", account.Name);

    public bool Delete(Account account) => Veto("f_onaccount_delete", account.Name);

    public bool BlockChange(Account account, bool block) =>
        Veto(block ? "f_onaccount_block" : "f_onaccount_unblock", account.Name);

    public bool PasswordChange(Account account, string newPassword, string oldPassword)
    {
        // SetStrNew: string vars kept verbatim - a numeric password stays text and an
        // empty old password (a new account) reads empty, not 0.
        var locals = new VarMap();
        locals.SetStr("password", true, newPassword);
        locals.SetStr("oldPassword", true, oldPassword);
        return Veto("f_onaccount_pwchange", account.Name, locals);
    }

    public AccountConnectVerdict Connect(Account account, string password)
    {
        // Init() is never called here upstream: ARGS stays empty, the name and the
        // offered password travel in LOCAL only.
        var locals = new VarMap();
        locals.SetStr("Account", true, account.Name);
        locals.SetStr("Password", true, password);
        var args = new TriggerArgs { SharedLocals = locals };
        if (!_hooks.RunServerFunction("f_onaccount_connect", _server, args, out long? ret))
            return AccountConnectVerdict.Default;
        return ret switch
        {
            1 => AccountConnectVerdict.Deny,
            ReturnHalfBaked => AccountConnectVerdict.SkipCheck,
            _ => AccountConnectVerdict.Default,
        };
    }

    private bool Veto(string function, string accountName, VarMap? locals = null)
    {
        var args = new TriggerArgs { ArgString = accountName, SharedLocals = locals };
        return _hooks.RunServerFunction(function, _server, args, out long? ret) && ret == 1;
    }
}
