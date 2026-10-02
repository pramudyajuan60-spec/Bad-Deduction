namespace BadDeduction.Investigation;

/// <summary>
/// Phase 10 seam: lets a hidden genius occasionally lie in interviews. Implemented by the
/// agenda service; consulted by <see cref="InvestigationService"/> when answering
/// whereabouts topics. Kept as an interface so the investigation subsystem never
/// depends on the agenda subsystem.
/// </summary>
public interface IDeceptionHook
{
    /// <summary>
    /// Returns a replacement claim for the speaker's answer, or null to keep the truthful one.
    /// </summary>
    string? MaybeDeceive(string speakerId, string topic, string truthfulClaim);
}
