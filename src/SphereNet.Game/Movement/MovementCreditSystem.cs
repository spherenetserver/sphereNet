namespace SphereNet.Game.Movement;

public static class MovementCreditSystem
{
    public static bool TryConsumeCredit(
        ref int creditMs,
        ref long creditLastTick,
        int baseCreditMs,
        int maxCreditMs,
        int moveDelayMs,
        long nowMs)
    {
        RefillCredit(ref creditMs, ref creditLastTick, maxCreditMs, nowMs);

        if (creditMs >= moveDelayMs)
        {
            creditMs -= moveDelayMs;
            return true;
        }

        return false;
    }

    public static void RefillCredit(
        ref int creditMs,
        ref long creditLastTick,
        int maxCreditMs,
        long nowMs)
    {
        if (creditLastTick <= 0)
        {
            creditMs = maxCreditMs;
            creditLastTick = nowMs;
            return;
        }

        long elapsed = nowMs - creditLastTick;
        if (elapsed <= 0) return;

        creditMs = (int)Math.Min((long)creditMs + elapsed, maxCreditMs);
        creditLastTick = nowMs;
    }
}
