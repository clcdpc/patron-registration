using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class PreviewRepositoryClockContractTests
{
    [DataTestMethod]
    [DataRow(nameof(ISettingsAdministrationRepository.CreatePreviewLink))]
    [DataRow(nameof(ISettingsAdministrationRepository.ResolvePreviewContext))]
    [DataRow(nameof(ISettingsAdministrationRepository.TryAdmitLivePreviewSubmission))]
    public void PreviewContracts_DoNotAcceptCallerControlledTime(string methodName)
    {
        var methods = typeof(ISettingsAdministrationRepository).GetMethods().Where(method => method.Name == methodName).ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.IsFalse(methods[0].GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(DateTime) || parameter.ParameterType == typeof(DateTimeOffset)));
    }
}
