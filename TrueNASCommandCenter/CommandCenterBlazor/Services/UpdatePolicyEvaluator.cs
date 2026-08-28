using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Services;

public interface IUpdatePolicyEvaluator
{
    UpdateDecision Evaluate(
        AppRecord app,
        string? targetVersion,
        bool manual,
        bool riskyStateConfirmed = false,
        string? managerAppId = null);
}

public sealed class UpdatePolicyEvaluator(IVersionClassifier versionClassifier) : IUpdatePolicyEvaluator
{
    public UpdateDecision Evaluate(
        AppRecord app,
        string? targetVersion,
        bool manual,
        bool riskyStateConfirmed = false,
        string? managerAppId = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.CatalogUpdateAvailable && !app.ImageUpdateAvailable)
        {
            return new UpdateDecision(UpdateDecisionKind.NoUpdate, "NO_UPDATE", "The app is up to date.");
        }

        if (app.ActionRequired)
        {
            return new UpdateDecision(
                UpdateDecisionKind.Blocked,
                "ACTION_REQUIRED",
                "TrueNAS reports that administrator action is required.",
                targetVersion);
        }

        if (!string.IsNullOrWhiteSpace(managerAppId) &&
            string.Equals(app.Id, managerAppId, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateDecision(
                UpdateDecisionKind.Blocked,
                "SELF_UPDATE",
                "This manager never updates itself.",
                targetVersion);
        }

        if (app.State is "DEPLOYING" or "STOPPING")
        {
            return new UpdateDecision(
                UpdateDecisionKind.Blocked,
                "TRANSIENT_STATE",
                $"The app is currently {app.State.ToLowerInvariant()}.",
                targetVersion);
        }

        if (manual)
        {
            if (app.State is "STOPPED" or "CRASHED" && !riskyStateConfirmed)
            {
                return new UpdateDecision(
                    UpdateDecisionKind.ManualApproval,
                    "STATE_CONFIRMATION_REQUIRED",
                    $"Confirm updating an app in the {app.State} state.",
                    targetVersion);
            }

            return TargetDecision(app, targetVersion, allowRegardlessOfScope: true);
        }

        if (app.Policy is null)
        {
            return new UpdateDecision(
                UpdateDecisionKind.Unconfigured,
                "POLICY_UNCONFIGURED",
                "Choose a policy before this app can update or notify.",
                targetVersion);
        }

        if (app.Policy == AppPolicy.Ignore)
        {
            return new UpdateDecision(UpdateDecisionKind.Ignored, "POLICY_IGNORE", "The app is ignored.", targetVersion);
        }

        if (app.Policy == AppPolicy.NotifyOnly)
        {
            return new UpdateDecision(
                UpdateDecisionKind.Notify,
                "POLICY_NOTIFY_ONLY",
                "Manual approval is required by policy.",
                targetVersion);
        }

        if (app.State != "RUNNING")
        {
            return new UpdateDecision(
                UpdateDecisionKind.Blocked,
                "STATE_NOT_RUNNING",
                $"Automatic updates require RUNNING state; current state is {app.State}.",
                targetVersion);
        }

        return TargetDecision(app, targetVersion, allowRegardlessOfScope: false);
    }

    private UpdateDecision TargetDecision(AppRecord app, string? targetVersion, bool allowRegardlessOfScope)
    {
        if (app.CatalogUpdateAvailable)
        {
            if (string.IsNullOrWhiteSpace(targetVersion))
            {
                return new UpdateDecision(
                    UpdateDecisionKind.ManualApproval,
                    "AMBIGUOUS_TARGET",
                    "TrueNAS did not provide an unambiguous target version.");
            }

            if (!allowRegardlessOfScope &&
                !versionClassifier.IsAllowed(app.InstalledVersion, targetVersion, app.VersionScope))
            {
                return new UpdateDecision(
                    UpdateDecisionKind.ManualApproval,
                    "VERSION_SCOPE",
                    "The target cannot be confidently classified within the selected version scope.",
                    targetVersion);
            }

            return new UpdateDecision(
                UpdateDecisionKind.Eligible,
                "CATALOG_ELIGIBLE",
                "The catalog upgrade is eligible.",
                targetVersion);
        }

        return new UpdateDecision(
            UpdateDecisionKind.Eligible,
            "IMAGE_ELIGIBLE",
            "The image refresh is eligible; version scope does not apply.");
    }
}
