using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    // ServUO-style fastwalk prevention via time-based throttle + walk buffer.
    public static int WalkBufferMax { get; set; } = 75;
    public static int WalkRegenPerSecond { get; set; } = 25;
    public static int MoveToleranceMs { get; set; } = 80;
    public static int MoveRejectResyncMs { get; set; } = 0;

    public static int MoveViolationKickThreshold { get; set; }
    public static Func<long> MoveClock { get; set; } = () => Environment.TickCount64;

    // Credit-based movement system (opt-in via MovementCreditEnabled)
    public static bool MovementCreditEnabled { get; set; }
    public static int MovementCreditBaseMs { get; set; } = 200;
    public static int MovementCreditMaxMs { get; set; } = 1400;
    public static int MovementQueueCapacity { get; set; } = 10;

    // Speed hack detection (opt-in)
    public static bool SpeedHackDetectionEnabled { get; set; }
    public static double SpeedHackRateThreshold { get; set; } = 1.5;
    public static int SpeedHackBurstWindow { get; set; } = 3;
    public static int SpeedHackHistorySize { get; set; } = 20;
    public static int SpeedHackCooldownMs { get; set; } = 60_000;

    public static event Action<Objects.Characters.Character, Movement.SpeedVerdict>? OnSpeedHackDetected;

    /// <summary>Raise bridge so the extracted combat handler can fire the
    /// event (C# events are only invocable from the declaring class).</summary>
    internal static void RaiseSpeedHackDetected(Character ch, Movement.SpeedVerdict verdict) =>
        OnSpeedHackDetected?.Invoke(ch, verdict);

    /// <summary>Combat handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientCombatHandler"/>.</summary>
    internal ClientCombatHandler Combat => _combat ??= new ClientCombatHandler(this);
    private ClientCombatHandler? _combat;

    public void HandleMovementBatch(IReadOnlyList<SphereNet.Network.State.MovementStep> steps) =>
        Combat.HandleMovementBatch(steps);

    public void QueueMoveRequest(byte dir, byte seq, uint fastWalkKey) =>
        Combat.QueueMoveRequest(dir, seq, fastWalkKey);

    public bool HandleMove(byte dir, byte seq, uint fastWalkKey) => Combat.HandleMove(dir, seq, fastWalkKey);

    public void TickMovementQueue(long nowMs) => Combat.TickMovementQueue(nowMs);

    internal void ResetWalkValidator() => Combat.ResetWalkValidator();

    public void HandleSpeech(byte type, ushort hue, ushort font, string text) =>
        Combat.HandleSpeech(type, hue, font, text);

    public void HandleAttack(uint targetUid) => Combat.HandleAttack(targetUid);

    public void TickCombat() => Combat.TickCombat();

    public void OnCharacterDeath() => Combat.OnCharacterDeath();

    public void OnResurrect() => Combat.OnResurrect();

    public void HandleWarMode(bool warMode) => Combat.HandleWarMode(warMode);

    public void HandleCastSpell(SpellType spell, uint targetUid) => Combat.HandleCastSpell(spell, targetUid);

    public void TickSpellCast() => Combat.TickSpellCast();

    /// <summary>
    /// Consolidated client tick: runs combat, spell casting, and stat updates.
    /// </summary>
    public void TickClientState() => Combat.TickClientState();

    public void TickStatUpdate() => Combat.TickStatUpdate();
}
