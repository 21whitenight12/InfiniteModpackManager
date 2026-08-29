namespace Imm.Core.Launch;

/// <summary>
/// Fully resolved launch request: executable, working directory, CLI arguments.
/// <see cref="Elevate"/> requests a UAC prompt (runas verb) — game loaders like f4se launch
/// the game exe from the MO2 process token, and 740 (ERROR_ELEVATION_REQUIRED) means MO2 itself
/// must run elevated (FEHLauncher scheme).
/// </summary>
public sealed record LaunchPlan(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool Elevate = false);
