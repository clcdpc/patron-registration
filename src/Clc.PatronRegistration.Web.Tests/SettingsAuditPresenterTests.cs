using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAuditPresenterTests
{
    [TestMethod]
    public void ActivityLabels_KnownAndUnknownNamesAreReadable()
    {
        Assert.AreEqual("Saved settings", SettingsAuditPresenter.PresentActivity("DirectSave"));
        Assert.AreEqual("Unexpected historical event", SettingsAuditPresenter.PresentActivity("UnexpectedHistoricalEvent"));
        Assert.AreEqual("Unknown activity", SettingsAuditPresenter.PresentActivity(null));
    }

    [TestMethod]
    public void TargetPresentation_UsesSystemOrganizationDefaultFormAndSafeFallbacks()
    {
        var system = Present(Row(organization: 1));
        var missing = Present(Row(organization: 123, form: "retired"));
        Assert.AreEqual("System defaults — Default form", system.Target);
        Assert.AreEqual("Organization 123 — retired", missing.Target);
    }

    [TestMethod]
    public void TargetPresentation_UsesOrganizationAndFormDisplayNames()
    {
        var model = SettingsAuditPresenter.Present(Row(organization: 3, form: "kids"), false, 1,
            id => id == 3 ? "North Branch" : null,
            (_, code) => code.Equals("kids", StringComparison.OrdinalIgnoreCase) ? "Kids registration" : null, Catalog());
        Assert.AreEqual("North Branch — Kids registration", model.Target);
    }

    [TestMethod]
    public void SettingPresentation_UsesCatalogNameAndPreservesUnknownKey()
    {
        Assert.AreEqual("Registration text", Present(Row(setting: "registration_text")).Setting);
        Assert.AreEqual("removed_key", Present(Row(setting: "removed_key")).Setting);
    }

    [TestMethod]
    public void FailedEvent_HasReasonOrUsefulFallback()
    {
        Assert.AreEqual("Bad input", Present(Row(succeeded: false, failure: "Bad input")).FailureReason);
        Assert.AreEqual("The operation did not complete.", Present(Row(succeeded: false)).FailureReason);
    }

    [TestMethod]
    public void LibraryAdministrator_ReceivesSearchableIdentifiersButNotTroubleshootingValues()
    {
        var model = Present(Row(form: "kids", setting: "registration_text", correlation: "request-1", ip: "127.0.0.1"));
        var labels = model.TechnicalDetails.Select(detail => detail.Label).ToList();

        CollectionAssert.Contains(labels, "Raw event type");
        CollectionAssert.Contains(labels, "Raw setting key");
        CollectionAssert.Contains(labels, "Raw form code");
        CollectionAssert.DoesNotContain(labels, "Audit event ID");
        CollectionAssert.DoesNotContain(labels, "Target organization ID");
        CollectionAssert.DoesNotContain(labels, "Target library ID");
        CollectionAssert.DoesNotContain(labels, "Correlation ID");
        CollectionAssert.DoesNotContain(labels, "IP address");
        Assert.AreEqual("kids", model.TechnicalDetails.Single(detail => detail.Label == "Raw form code").Value);
    }

    [TestMethod]
    public void DuplicateFriendlyFormNamesRemainDistinguishableByRawFormCode()
    {
        var first = SettingsAuditPresenter.Present(Row(form: "kids"), false, 1, _ => null,
            (_, _) => "Youth registration", Catalog());
        var second = SettingsAuditPresenter.Present(Row(form: "teens"), false, 1, _ => null,
            (_, _) => "Youth registration", Catalog());

        Assert.AreEqual(first.Form, second.Form);
        Assert.AreEqual("kids", first.TechnicalDetails.Single(detail => detail.Label == "Raw form code").Value);
        Assert.AreEqual("teens", second.TechnicalDetails.Single(detail => detail.Label == "Raw form code").Value);
    }

    [TestMethod]
    public void GlobalAdministrator_ReceivesOnlyAvailableTechnicalValues()
    {
        var model = Present(Row(correlation: "request-1", ip: "127.0.0.1"), true);
        var labels = model.TechnicalDetails.Select(detail => detail.Label).ToList();
        CollectionAssert.Contains(labels, "Target organization ID");
        CollectionAssert.Contains(labels, "Target library ID");
        CollectionAssert.Contains(labels, "Raw form code");
        CollectionAssert.Contains(labels, "Correlation ID");
        CollectionAssert.Contains(labels, "IP address");
        Assert.AreEqual("(empty — default form)", model.TechnicalDetails.Single(detail => detail.Label == "Raw form code").Value);
    }

    [TestMethod]
    public void TimestampAndUnknownStaff_ArePresentedForPeopleAndMachines()
    {
        var model = Present(Row());
        Assert.AreEqual("Jul 30, 2026, 9:14 PM UTC", model.TimestampDisplay);
        Assert.AreEqual("Unknown staff member", model.StaffMember);
        StringAssert.Contains(model.TimestampDateTime, "2026-07-30T21:14:00");
    }

    private static SettingsAuditEventViewModel Present(SettingsAuditRow row, bool global = false) =>
        SettingsAuditPresenter.Present(row, global, 1, _ => null, (_, _) => null, Catalog());

    private static IReadOnlyDictionary<string, SettingDefinition> Catalog() =>
        new Dictionary<string, SettingDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["registration_text"] = new("registration_text", "Registration text", "", SettingValueType.LongString)
        };

    private static SettingsAuditRow Row(int organization = 2, string form = "", string? setting = null,
        bool succeeded = true, string? failure = null, string? correlation = null, string? ip = null) =>
        new(42, new DateTime(2026, 7, 30, 21, 14, 0, DateTimeKind.Utc), "DirectSave", organization, 2,
            form, setting, null, null, false, succeeded, null, failure, correlation, ip);
}
