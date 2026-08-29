using Imm.Core.Domain;

namespace Imm.Core.Launch;

/// <summary>Per-engine pre-launch checks and launch plan building (IMM.md MVP #3: ILaunchStrategy).</summary>
public interface ILaunchStrategy
{
    /// <summary>Whether this strategy handles the given launch engine.</summary>
    bool Handles(LaunchEngine engine);

    /// <summary>
    /// Validates the pack and builds the launch plan.
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message when launch is impossible.
    /// </summary>
    Task<LaunchPlan> BuildPlanAsync(ModpackManifest manifest, string packRoot, CancellationToken cancellationToken = default);
}
