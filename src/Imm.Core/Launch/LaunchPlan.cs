namespace Imm.Core.Launch;

/// <summary>Fully resolved launch request: executable, working directory, CLI arguments.</summary>
public sealed record LaunchPlan(string ExecutablePath, string WorkingDirectory, IReadOnlyList<string> Arguments);
