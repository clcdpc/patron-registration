using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAdministrationTests
{
    [TestMethod] public void Resolver_Uses_All_Six_Explicit_Levels()
    {
        var precedence=SettingsResolver.BuildPrecedence(30,20,1,"kids");
        CollectionAssert.AreEqual(new[]{(30,"kids"),(30,""),(20,"kids"),(20,""),(1,"kids"),(1,"")},precedence.Select(x=>(x.OrganizationId,x.FormCode)).ToArray());
    }
    [TestMethod] public void Resolver_Preserves_Explicit_Empty_Override()
    {
        var rows=new[]{new RegistrationFormSetting{OrganizationID=1,Setting="x",Value="system"},new RegistrationFormSetting{OrganizationID=20,Setting="x",Value=""}};
        var result=new SettingsResolver().Resolve(rows,"x",20,20,"");
        Assert.IsTrue(result.OwnsOverride); Assert.AreEqual("",result.EffectiveValue); Assert.IsFalse(result.IsInherited);
    }
    [TestMethod] public void Resolver_Remove_Exposes_Inherited_Value()
    {
        var rows=new[]{new RegistrationFormSetting{OrganizationID=1,Setting="x",Value="system"},new RegistrationFormSetting{OrganizationID=20,Setting="x",Value="local"}};
        var removed=new HashSet<(int,string,string)>{(20,"","x")};
        Assert.AreEqual("system",new SettingsResolver().Resolve(rows,"x",20,20,"",removed:removed).EffectiveValue);
    }
    [TestMethod] public void Catalog_Keys_Are_Unique_And_Reject_Arbitrary_Suffixes()
    {
        var catalog=new SettingCatalog(); Assert.AreEqual(catalog.All.Count,catalog.All.Select(x=>x.Key.ToLowerInvariant()).Distinct().Count());
        Assert.IsTrue(catalog.TryGet("require.NameFirst",out _)); Assert.IsFalse(catalog.TryGet("require.DropTable",out _));
    }
    [TestMethod] public void Catalog_Performs_Invariant_Typed_Validation()
    {
        var catalog=new SettingCatalog(); catalog.TryGet("reset_seconds",out var number); catalog.TryGet("welcome_email_from_address",out var email);
        Assert.IsNull(number.Validate("42")); Assert.IsNotNull(number.Validate("4.2")); Assert.IsNotNull(email.Validate("not-email"));
    }
    [DataTestMethod][DataRow("a")][DataRow("secret")][DataRow("abcd1234wxyz5678")]
    public void Sensitive_Masking_Never_Retains_The_Secret(string secret)
    { var masked=SensitiveValueMasker.Mask(secret); Assert.AreNotEqual(secret,masked); Assert.IsTrue(masked.Contains('…')); Assert.IsTrue(masked.Replace("…","").Length<=secret.Length/2); }
    [TestMethod] public void Preview_Tokens_Have_256_Bits_And_Verify_In_Constant_Time_Helper()
    { var service=new PreviewTokenService();var token=service.Create();Assert.AreEqual(32,token.Hash.Length);Assert.IsTrue(service.Matches(token.Plaintext,token.Hash));Assert.IsFalse(service.Matches(token.Plaintext+"x",token.Hash));Assert.IsFalse(token.Plaintext.Contains('+'));Assert.IsFalse(token.Plaintext.Contains('/')); }
}
