using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Speech;

/// <summary>
/// Ghost (dead player) speech garbling. Source-X / classic UO: a dead character's
/// speech is scrambled into random o/O characters — spaces preserved, length kept —
/// for living listeners who cannot hear the dead. Other ghosts and staff hear it
/// clearly. Mirrors ServUO Mobile.MutateSpeech / CheckHearsMutatedSpeech, where a
/// recipient hears the garbled form exactly when (recipient.Alive &amp;&amp; !CanHearGhosts).
/// </summary>
public static class GhostSpeech
{
    private static readonly char[] GhostChars = ['o', 'O'];

    /// <summary>Scramble <paramref name="text"/> into random ghost characters using
    /// the shared RNG. Spaces are preserved and the length is unchanged.</summary>
    public static string Garble(string text) => Garble(text, Random.Shared);

    /// <summary>Deterministic overload (test seam): scramble with a supplied RNG.</summary>
    public static string Garble(string text, Random rng)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c == ' ' ? ' ' : GhostChars[rng.Next(GhostChars.Length)]);
        return sb.ToString();
    }

    /// <summary>A recipient hears a ghost's speech as clear text when they are
    /// themselves dead (a ghost) or are staff (AllShow / Counsel+). Every other
    /// living listener hears the garbled form. Mirrors ServUO CanHearGhosts
    /// (manual flag || staff) combined with the dead-listener exemption.</summary>
    public static bool HearsGhostClearly(Character recipient) =>
        recipient.IsDead ||
        recipient.AllShow ||
        recipient.PrivLevel >= PrivLevel.Counsel;
}
