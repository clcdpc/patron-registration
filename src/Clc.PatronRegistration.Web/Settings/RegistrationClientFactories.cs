using Clc.Melissa;
using Clc.PatronRegistration;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Web.Settings;

public interface IEmailSenderFactory
{
    IEmailSender Create(string apiKey);
}

public interface IMelissaClientFactory
{
    IMelissaRestClient Create(string apiKey);
}

public sealed class EmailSenderFactory(IServiceProvider services) : IEmailSenderFactory
{
    public IEmailSender Create(string apiKey) => services.ResolveWith<PostmarkEmailSender>(apiKey);
}

public sealed class MelissaClientFactory(IServiceProvider services) : IMelissaClientFactory
{
    public IMelissaRestClient Create(string apiKey) => services.ResolveWith<MelissaRestClient>(apiKey);
}

public static class RegistrationClientProvider
{
    public static IEmailSender CreateEmail(ISettingProvider settings, IEmailSenderFactory factory) =>
        factory.Create(settings.PostmarkApiKey ?? string.Empty);

    public static IMelissaRestClient CreateMelissa(ISettingProvider settings, IMelissaClientFactory factory) =>
        factory.Create(settings.MelissaDataApiKey ?? string.Empty);
}
