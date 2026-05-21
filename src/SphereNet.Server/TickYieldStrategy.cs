namespace SphereNet.Server;

public enum TickYieldAction
{
    Spin,
    SleepOne,
    Hybrid,
}

public static class TickYieldStrategy
{
    public static TickYieldAction Resolve(int mode) => mode switch
    {
        0 => TickYieldAction.Spin,
        1 => TickYieldAction.SleepOne,
        _ => TickYieldAction.Hybrid,
    };

    public static void Yield(int mode)
    {
        switch (Resolve(mode))
        {
            case TickYieldAction.Spin:
                Thread.SpinWait(100);
                break;
            case TickYieldAction.SleepOne:
                Thread.Sleep(1);
                break;
            default:
                Thread.SpinWait(100);
                Thread.Sleep(0);
                break;
        }
    }
}
