using System.ComponentModel.DataAnnotations;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Validators;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class LegalNameValidatorTests
{
    [DataTestMethod]
    [DataRow(false, "", "", true)]
    [DataRow(true, "Jane", "Doe", true)]
    [DataRow(true, "", "Doe", false)]
    [DataRow(true, "Jane", "", false)]
    [DataRow(true, "   ", "Doe", false)]
    [DataRow(true, "Jane", "\t", false)]
    public void LegalNames_AreRequiredOnlyWhenSelected(bool useLegalName, string first, string last, bool expectedValid)
    {
        var settings = new Mock<ISettingProvider>();
        var registration = new Registration(settings.Object) { UseLegalName = useLegalName, LegalNameFirst = first, LegalNameLast = last };
        var errors = new List<ValidationResult>();

        Validate(registration, nameof(Registration.LegalNameFirst), first, errors);
        Validate(registration, nameof(Registration.LegalNameLast), last, errors);

        Assert.AreEqual(expectedValid, errors.Count == 0);
        if (useLegalName && string.IsNullOrWhiteSpace(first))
            Assert.IsTrue(errors.Any(error => error.MemberNames.Contains(nameof(Registration.LegalNameFirst))));
        if (useLegalName && string.IsNullOrWhiteSpace(last))
            Assert.IsTrue(errors.Any(error => error.MemberNames.Contains(nameof(Registration.LegalNameLast))));
    }

    private static void Validate(Registration registration, string memberName, string value, ICollection<ValidationResult> errors)
    {
        var settings = new Mock<ISettingProvider>();
        settings.Setup(service => service.GetFieldLabel(It.IsAny<string>())).Returns((string name) => name);
        var context = new ValidationContext(registration, new Services(settings.Object), null) { MemberName = memberName };
        var result = new LegalNameValidator().GetValidationResult(value, context);
        if (result != ValidationResult.Success)
            errors.Add(new ValidationResult(result!.ErrorMessage, [memberName]));
    }

    private sealed class Services(ISettingProvider settings) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ISettingProvider) ? settings : null;
    }
}
