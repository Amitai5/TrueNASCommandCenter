using TrueNasUpdateManager.Domain;
using TrueNasUpdateManager.Services;

namespace TrueNasUpdateManager.Tests;

[TestClass]
public sealed class VersionAndPolicyTests
{
    private readonly VersionClassifier versions = new();
    private readonly UpdatePolicyEvaluator evaluator;

    public VersionAndPolicyTests()
    {
        evaluator = new UpdatePolicyEvaluator(versions);
    }

    [TestMethod]
    public void TryParse_NormalizesClearVPrefix()
    {
        var parsed = versions.TryParse("v12.3.4", out var result);

        Assert.IsTrue(parsed);
        Assert.AreEqual(new VersionParts(12, 3, 4), result);
    }

    [TestMethod]
    [DataRow("1.2")]
    [DataRow("1.2.3.4")]
    [DataRow("release-1.2.3")]
    [DataRow("")]
    public void TryParse_RejectsAmbiguousVersions(string value)
    {
        Assert.IsFalse(versions.TryParse(value, out _));
    }

    [TestMethod]
    public void IsAllowed_AppliesScopeConservatively()
    {
        Assert.IsTrue(versions.IsAllowed("1.2.3", "1.9.0", VersionScope.MinorAndPatch));
        Assert.IsFalse(versions.IsAllowed("1.2.3", "2.0.0", VersionScope.MinorAndPatch));
        Assert.IsTrue(versions.IsAllowed("1.2.3", "1.2.9", VersionScope.PatchOnly));
        Assert.IsFalse(versions.IsAllowed("1.2.3", "1.3.0", VersionScope.PatchOnly));
        Assert.IsFalse(versions.IsAllowed("chart_1", "chart_2", VersionScope.PatchOnly));
        Assert.IsTrue(versions.IsAllowed("chart_1", "chart_2", VersionScope.AnyVersion));
    }

    [TestMethod]
    public void Evaluate_UnconfiguredAppNeverUpdatesOrNotifies()
    {
        var result = evaluator.Evaluate(App(policy: null), "1.2.4", manual: false);

        Assert.AreEqual(UpdateDecisionKind.Unconfigured, result.Kind);
    }

    [TestMethod]
    public void Evaluate_ActionRequiredAlwaysBlocks()
    {
        var app = App(AppPolicy.AutoUpdate);
        app.ActionRequired = true;

        var automatic = evaluator.Evaluate(app, "1.2.4", manual: false);
        var manual = evaluator.Evaluate(app, "1.2.4", manual: true, riskyStateConfirmed: true);

        Assert.AreEqual(UpdateDecisionKind.Blocked, automatic.Kind);
        Assert.AreEqual(UpdateDecisionKind.Blocked, manual.Kind);
        Assert.AreEqual("ACTION_REQUIRED", automatic.ReasonCode);
    }

    [TestMethod]
    public void Evaluate_StoppedAppRequiresManualConfirmation()
    {
        var app = App(AppPolicy.AutoUpdate);
        app.State = "STOPPED";

        Assert.AreEqual(UpdateDecisionKind.Blocked, evaluator.Evaluate(app, "1.2.4", manual: false).Kind);
        Assert.AreEqual(UpdateDecisionKind.ManualApproval, evaluator.Evaluate(app, "1.2.4", manual: true).Kind);
        Assert.AreEqual(
            UpdateDecisionKind.Eligible,
            evaluator.Evaluate(app, "1.2.4", manual: true, riskyStateConfirmed: true).Kind);
    }

    [TestMethod]
    public void Evaluate_ImageUpdateIgnoresVersionScope()
    {
        var app = App(AppPolicy.AutoUpdate);
        app.CatalogUpdateAvailable = false;
        app.ImageUpdateAvailable = true;
        app.VersionScope = VersionScope.PatchOnly;
        app.InstalledVersion = "not-semver";

        var result = evaluator.Evaluate(app, null, manual: false);

        Assert.AreEqual(UpdateDecisionKind.Eligible, result.Kind);
        Assert.AreEqual("IMAGE_ELIGIBLE", result.ReasonCode);
    }

    [TestMethod]
    public void Evaluate_UnknownCatalogVersionRequiresApprovalWithinScopedPolicy()
    {
        var app = App(AppPolicy.AutoUpdate);
        app.InstalledVersion = "chart_1";
        app.VersionScope = VersionScope.MinorAndPatch;

        var result = evaluator.Evaluate(app, "chart_2", manual: false);

        Assert.AreEqual(UpdateDecisionKind.ManualApproval, result.Kind);
        Assert.AreEqual("VERSION_SCOPE", result.ReasonCode);
    }

    [TestMethod]
    public void Evaluate_ManagerNeverUpdatesItself()
    {
        var app = App(AppPolicy.AutoUpdate);

        var result = evaluator.Evaluate(app, "1.2.4", manual: false, managerAppId: app.Id);

        Assert.AreEqual(UpdateDecisionKind.Blocked, result.Kind);
        Assert.AreEqual("SELF_UPDATE", result.ReasonCode);
    }

    private static AppRecord App(AppPolicy? policy) => new()
    {
        Id = "sample",
        Name = "sample",
        Policy = policy,
        State = "RUNNING",
        CatalogUpdateAvailable = true,
        InstalledVersion = "1.2.3",
        VersionScope = VersionScope.AnyVersion
    };
}
