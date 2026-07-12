namespace SphereNet.Core.Enums;

/// <summary>COMBATPARRYINGERA bits from Source-X PARRYFLAGS_TYPE.</summary>
[Flags]
public enum ParryEraFlags : int
{
    PreSeFormula = 0x01,
    SeFormula = 0x02,
    ShieldBlock = 0x10,
    OneHandBlock = 0x20,
    TwoHandBlock = 0x40,
    ArmorScaling = 0x80,
}
