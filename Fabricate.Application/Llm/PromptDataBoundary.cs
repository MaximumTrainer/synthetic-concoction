using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// What class of content a tool's result carries, ordered by how much of the customer's actual data it exposes.
/// </summary>
public enum PromptContentClass
{
    /// <summary>
    /// Names and shapes only — tables, columns, types, constraints, run summaries. Always allowed to leave the
    /// instance: it is what makes the agent useful, and it describes the data rather than containing it.
    /// </summary>
    Metadata = 0,

    /// <summary>
    /// Derived statistics over real rows — histograms, distinct counts, min/max. No single row is disclosed, but
    /// a min/max is still a real value and a histogram over a small table can identify individuals.
    /// </summary>
    AggregateStatistics,

    /// <summary>Values copied from real rows: samples, examples, few-shot rows.</summary>
    SampledValues,
}

/// <summary>
/// Enforces the boundary #60 §7 defined for what may be sent to a model provider (#83).
///
/// <para>
/// The rule is not "warn" and not "redact": a tool whose output the boundary forbids is never offered to the
/// model, so the model cannot ask for it and be refused halfway through a turn. Refusing mid-turn would already
/// have told the model the data exists, and would leave the user with a broken conversation instead of a tool
/// that simply is not there.
/// </para>
/// </summary>
public interface IPromptDataBoundary
{
    /// <summary>Whether content of this class may enter a prompt for this workspace.</summary>
    bool Allows(PromptContentClass contentClass, Workspace workspace, WorkspaceLlmPolicy? policy);

    /// <summary>
    /// Whether <paramref name="profile"/> permits the sampled-data opt-in to be set at all. Healthcare and
    /// Finance do not: for those regimes the answer is fixed by the regime, not by a workspace administrator.
    /// </summary>
    bool CanOptIn(ComplianceProfile profile);

    /// <summary>Why the opt-in is unavailable, for the error the API returns.</summary>
    string OptInRefusalReason(ComplianceProfile profile);
}

public sealed class PromptDataBoundary : IPromptDataBoundary
{
    public bool Allows(PromptContentClass contentClass, Workspace workspace, WorkspaceLlmPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (contentClass == PromptContentClass.Metadata) return true;

        // A regulated workspace cannot have opted in, but check the profile rather than trusting the stored flag:
        // a profile can change after a policy was written, and the regime must win over the stale record.
        if (!CanOptIn(workspace.ComplianceProfile)) return false;

        return policy?.AllowSampledDataInPrompts == true;
    }

    public bool CanOptIn(ComplianceProfile profile)
        => profile is not (ComplianceProfile.Healthcare or ComplianceProfile.Finance);

    public string OptInRefusalReason(ComplianceProfile profile)
        => CanOptIn(profile)
            ? string.Empty
            : $"Sampled data cannot be sent to a model provider from a workspace on the {profile} compliance " +
              "profile. This is fixed by the profile and cannot be enabled per workspace; move the work to a " +
              "Default-profile workspace if the data genuinely does not fall under that regime.";
}
