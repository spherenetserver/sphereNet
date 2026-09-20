using SphereNet.Core.Enums;

namespace SphereNet.Core.Interfaces;

/// <summary>Source-X g_Serv context for TRYSRV: owner privilege, no character
/// or player client. Shared by direct object verbs and interpreter dispatch.</summary>
public sealed class ScriptServerConsole : ITextConsole
{
    public static readonly ScriptServerConsole Instance = new();
    private ScriptServerConsole() { }
    public PrivLevel GetPrivLevel() => PrivLevel.Owner;
    public string GetName() => "SERV";
    public void SysMessage(string text) { }
}
