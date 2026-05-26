using SphereNet.Game.Gumps;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Bot Manager Dialog - Visual interface for controlling stress test bots.
/// Opened via .BOTMENU command.
/// </summary>
public static class BotManagerDialog
{
    public const uint GumpId = 0xB07_0001;

    // Button IDs
    private const int BtnStart = 1;
    private const int BtnStop = 2;
    private const int BtnClean = 3;
    private const int BtnRefresh = 4;

    // Radio group IDs
    private const int RadioBehaviorBase = 100;  // 100-103: Idle, Walk, Combat, Full
    private const int RadioCityBase = 200;      // 200-208: All, Britain, Trinsic, etc.

    // Text entry IDs
    private const int EntryBotCount = 1;

    // Gump art IDs
    private const int BackgroundId = 9200;      // Gray stone background
    private const int ButtonOk = 4005;
    private const int ButtonOkPressed = 4007;
    private const int ButtonCancel = 4017;
    private const int ButtonCancelPressed = 4019;
    private const int RadioOff = 9720;
    private const int RadioOn = 9723;

    /// <summary>
    /// Build the Bot Manager dialog gump.
    /// </summary>
    public static GumpBuilder Build(uint charSerial, BotStats stats, BotSpawnCity currentCity, int lastCount)
    {
        var gump = new GumpBuilder(charSerial, GumpId, 450, 500);

        // Background
        gump.AddResizePic(0, 0, BackgroundId, 450, 500);

        // Title
        gump.AddText(150, 15, 1153, "Bot Stress Test Manager");

        // Divider line (using tiled gump)
        gump.AddGumpPicTiled(20, 45, 410, 2, 2620);

        // === Status Section ===
        gump.AddText(20, 55, 946, "Current Status:");
        gump.AddText(30, 80, 0, $"Active Bots: {stats.ActiveBots} / {stats.TotalBots}");
        gump.AddText(30, 100, 0, $"Connecting: {stats.ConnectingBots}");
        gump.AddText(230, 80, 0, $"Packets In: {stats.TotalPacketsReceived:N0}");
        gump.AddText(230, 100, 0, $"Packets Out: {stats.TotalPacketsSent:N0}");
        gump.AddText(30, 120, 0, $"Bytes In: {stats.TotalBytesReceived / 1024:N0} KB");
        gump.AddText(230, 120, 0, $"Bytes Out: {stats.TotalBytesSent / 1024:N0} KB");
        gump.AddText(30, 140, 0, $"PPS In: {stats.PacketsPerSecIn:F0}");
        gump.AddText(230, 140, 0, $"PPS Out: {stats.PacketsPerSecOut:F0}");

        // Divider
        gump.AddGumpPicTiled(20, 165, 410, 2, 2620);

        // === Bot Count Section ===
        gump.AddText(20, 175, 946, "Bot Count:");
        gump.AddResizePic(120, 172, 9350, 80, 26);
        gump.AddTextEntry(125, 175, 70, 20, 0, EntryBotCount, lastCount > 0 ? lastCount.ToString() : "100");

        // === Behavior Section ===
        gump.AddText(20, 195, 946, "Behavior:");

        gump.AddGroup(1);
        // Row 1 — legacy behaviors
        gump.AddRadio(20, 218, RadioOff, RadioOn, false, RadioBehaviorBase);
        gump.AddText(45, 220, 0, "Idle");

        gump.AddRadio(100, 218, RadioOff, RadioOn, false, RadioBehaviorBase + 1);
        gump.AddText(125, 220, 0, "Walk");

        gump.AddRadio(190, 218, RadioOff, RadioOn, false, RadioBehaviorBase + 2);
        gump.AddText(215, 220, 0, "Combat");

        gump.AddRadio(290, 218, RadioOff, RadioOn, false, RadioBehaviorBase + 3);
        gump.AddText(315, 220, 0, "Full");

        gump.AddRadio(370, 218, RadioOff, RadioOn, true, RadioBehaviorBase + 4);
        gump.AddText(395, 220, 68, "Smart");

        // Row 2 — role-based behaviors
        gump.AddRadio(20, 243, RadioOff, RadioOn, false, RadioBehaviorBase + 5);
        gump.AddText(45, 245, 67, "Walker");

        gump.AddRadio(120, 243, RadioOff, RadioOn, false, RadioBehaviorBase + 6);
        gump.AddText(145, 245, 67, "CombatR");

        gump.AddRadio(220, 243, RadioOff, RadioOn, false, RadioBehaviorBase + 7);
        gump.AddText(245, 245, 67, "Vendor");

        gump.AddRadio(320, 243, RadioOff, RadioOn, false, RadioBehaviorBase + 8);
        gump.AddText(345, 245, 67, "Loot");

        // Row 3 — more roles
        gump.AddRadio(20, 268, RadioOff, RadioOn, false, RadioBehaviorBase + 9);
        gump.AddText(45, 270, 67, "Skill");

        gump.AddRadio(120, 268, RadioOff, RadioOn, false, RadioBehaviorBase + 10);
        gump.AddText(145, 270, 67, "Social");

        gump.AddRadio(220, 268, RadioOff, RadioOn, false, RadioBehaviorBase + 11);
        gump.AddText(245, 270, 37, "Chaos");

        // Divider
        gump.AddGumpPicTiled(20, 300, 410, 2, 2620);

        // === Spawn City Section ===
        gump.AddText(20, 310, 946, "Spawn Location:");

        gump.AddGroup(2);
        // Row 1
        gump.AddRadio(20, 333, RadioOff, RadioOn, currentCity == BotSpawnCity.All, RadioCityBase);
        gump.AddText(45, 335, currentCity == BotSpawnCity.All ? 53 : 0, "All Cities");

        gump.AddRadio(130, 333, RadioOff, RadioOn, currentCity == BotSpawnCity.Britain, RadioCityBase + 1);
        gump.AddText(155, 335, currentCity == BotSpawnCity.Britain ? 53 : 0, "Britain");

        gump.AddRadio(240, 333, RadioOff, RadioOn, currentCity == BotSpawnCity.Trinsic, RadioCityBase + 2);
        gump.AddText(265, 335, currentCity == BotSpawnCity.Trinsic ? 53 : 0, "Trinsic");

        gump.AddRadio(340, 333, RadioOff, RadioOn, currentCity == BotSpawnCity.Moonglow, RadioCityBase + 3);
        gump.AddText(365, 335, currentCity == BotSpawnCity.Moonglow ? 53 : 0, "Moonglow");

        // Row 2
        gump.AddRadio(20, 358, RadioOff, RadioOn, currentCity == BotSpawnCity.Yew, RadioCityBase + 4);
        gump.AddText(45, 360, currentCity == BotSpawnCity.Yew ? 53 : 0, "Yew");

        gump.AddRadio(130, 358, RadioOff, RadioOn, currentCity == BotSpawnCity.Minoc, RadioCityBase + 5);
        gump.AddText(155, 360, currentCity == BotSpawnCity.Minoc ? 53 : 0, "Minoc");

        gump.AddRadio(240, 358, RadioOff, RadioOn, currentCity == BotSpawnCity.Vesper, RadioCityBase + 6);
        gump.AddText(265, 360, currentCity == BotSpawnCity.Vesper ? 53 : 0, "Vesper");

        gump.AddRadio(340, 358, RadioOff, RadioOn, currentCity == BotSpawnCity.Skara, RadioCityBase + 7);
        gump.AddText(365, 360, currentCity == BotSpawnCity.Skara ? 53 : 0, "Skara");

        // Row 3
        gump.AddRadio(20, 383, RadioOff, RadioOn, currentCity == BotSpawnCity.Jhelom, RadioCityBase + 8);
        gump.AddText(45, 385, currentCity == BotSpawnCity.Jhelom ? 53 : 0, "Jhelom");

        // Divider
        gump.AddGumpPicTiled(20, 415, 410, 2, 2620);

        // === Action Buttons ===
        gump.AddButton(30, 440, ButtonOk, ButtonOkPressed, BtnStart);
        gump.AddText(65, 442, 67, "Start Bots");

        gump.AddButton(150, 440, ButtonCancel, ButtonCancelPressed, BtnStop);
        gump.AddText(185, 442, 37, "Stop All");

        gump.AddButton(260, 440, 4020, 4022, BtnClean);
        gump.AddText(295, 442, 32, "Clean Chars");

        gump.AddButton(370, 440, 4008, 4010, BtnRefresh);
        gump.AddText(405, 442, 0, "Refresh");

        return gump;
    }

    /// <summary>
    /// Parse the gump response and return the action to perform.
    /// </summary>
    public static BotDialogAction ParseResponse(uint buttonId, uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        var action = new BotDialogAction { ActionType = BotActionType.None };

        // Parse text entry for bot count
        foreach (var (id, text) in textEntries)
        {
            if (id == EntryBotCount && int.TryParse(text.Trim(), out int count))
                action.BotCount = count;
        }

        // Parse radio selections
        foreach (uint sw in switches)
        {
            // Behavior radios (100-111)
            if (sw >= RadioBehaviorBase && sw < RadioBehaviorBase + 20)
            {
                action.Behavior = (int)(sw - RadioBehaviorBase) switch
                {
                    0 => BotBehavior.Idle,
                    1 => BotBehavior.RandomWalk,
                    2 => BotBehavior.Combat,
                    3 => BotBehavior.FullSimulation,
                    4 => BotBehavior.SmartAI,
                    5 => BotBehavior.Walker,
                    6 => BotBehavior.CombatRole,
                    7 => BotBehavior.Vendor,
                    8 => BotBehavior.Loot,
                    9 => BotBehavior.Skill,
                    10 => BotBehavior.Social,
                    11 => BotBehavior.Chaos,
                    _ => BotBehavior.SmartAI
                };
            }
            // City radios (200-208)
            else if (sw >= RadioCityBase && sw < RadioCityBase + 20)
            {
                action.City = (int)(sw - RadioCityBase) switch
                {
                    0 => BotSpawnCity.All,
                    1 => BotSpawnCity.Britain,
                    2 => BotSpawnCity.Trinsic,
                    3 => BotSpawnCity.Moonglow,
                    4 => BotSpawnCity.Yew,
                    5 => BotSpawnCity.Minoc,
                    6 => BotSpawnCity.Vesper,
                    7 => BotSpawnCity.Skara,
                    8 => BotSpawnCity.Jhelom,
                    _ => BotSpawnCity.All
                };
            }
        }

        // Determine action type
        action.ActionType = buttonId switch
        {
            BtnStart => BotActionType.Start,
            BtnStop => BotActionType.Stop,
            BtnClean => BotActionType.Clean,
            BtnRefresh => BotActionType.Refresh,
            _ => BotActionType.None
        };

        return action;
    }
}

public enum BotActionType
{
    None,
    Start,
    Stop,
    Clean,
    Refresh
}

public struct BotDialogAction
{
    public BotActionType ActionType;
    public int BotCount;
    public BotBehavior Behavior;
    public BotSpawnCity City;
}
