using Clc.PatronRegistration.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class AccountControllerTests
{
    [TestMethod]
    public void AccessDenied_ReturnsViewWithForbiddenStatusAndLocalReturnUrl()
    {
        var controller = ControllerWithLocalUrl("/settings");

        var result = controller.AccessDenied("/settings");

        Assert.AreEqual(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        var view = (ViewResult)result;
        Assert.AreEqual("/settings", view.Model);
    }

    [TestMethod]
    public void AccessDenied_DoesNotExposeNonLocalReturnUrl()
    {
        var controller = ControllerWithLocalUrl(null);

        var result = controller.AccessDenied("https://example.invalid/phishing");

        Assert.AreEqual(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        Assert.IsNull(((ViewResult)result).Model);
        Assert.IsFalse(result is RedirectResult);
    }

    [TestMethod]
    public void AccessDenied_IsAnonymousGetEndpoint()
    {
        var method = typeof(AccountController).GetMethod(nameof(AccountController.AccessDenied))!;

        Assert.IsNotNull(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).SingleOrDefault());
        var route = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Cast<HttpGetAttribute>().Single();
        Assert.AreEqual("/Account/AccessDenied", route.Template);
    }

    private static AccountController ControllerWithLocalUrl(string? localUrl)
    {
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.IsLocalUrl(It.IsAny<string>()))
            .Returns((string value) => value == localUrl);
        return new AccountController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = url.Object
        };
    }
}
