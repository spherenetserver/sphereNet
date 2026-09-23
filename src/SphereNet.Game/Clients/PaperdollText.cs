using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Clients;

/// <summary>
/// The name line of a character's paperdoll (the 60-character text field of the
/// 0x88 OpenPaperdoll packet). One builder, so the game client, the admin panel
/// and the public paperdoll endpoint all show the same string.
/// </summary>
public static class PaperdollText
{
    public static string Build(Character ch) =>
        string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";
}
