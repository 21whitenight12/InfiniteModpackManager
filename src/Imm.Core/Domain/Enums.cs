namespace Imm.Core.Domain;

/// <summary>Detection by game executable in the pack root (IMM.md Detection Markers).</summary>
public enum GameType
{
    Unknown,
    SkyrimLE,
    SkyrimSE,
    Fallout4,
    FalloutNV
}

/// <summary>Launch engine: Direct, standard MO2, or MO2 with Root Builder (IMM.md ILaunchStrategy).</summary>
public enum LaunchEngine
{
    Direct,
    Mo2Standard,
    Mo2RootBuilder
}

/// <summary>Where game files come from: in-pack Stock Game / Game Root, or system install (Steam/GOG).</summary>
public enum PackSource
{
    Portable,
    System
}
