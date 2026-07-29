using Clc.Auth.AzureAd.Security;
using Clc.Configuration;
using Clc.Melissa;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;
using Microsoft.Identity.Web;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.AddClcConfigFolder();

            IRegistrationConfiguration config = builder.Configuration.Get<RegistrationConfiguration>()!;
            builder.Services.AddSingleton(config);

            builder.Services.AddControllersWithViews();

            var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(name: MyAllowSpecificOrigins,
                    policy =>
                    {
                        policy.WithOrigins(config.CorsAllowedOrigins)
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                    });
            });

            builder.Services
                .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

            builder.Services
                .AddSingleton(config.Papi)
                .AddSingleton<IPapiClient, PapiClient>();

            builder.Services
                .AddSingleton<IDbHelperSettings>(config)
                .AddSingleton<IDbHelper, DbHelper>();

            builder.Services.AddSingleton<ICache, MemoryCache>();
            builder.Services.Configure<SettingsAdministrationOptions>(builder.Configuration.GetSection(SettingsAdministrationOptions.SectionName));
            builder.Services.AddSingleton<ISettingCatalog, SettingCatalog>();
            builder.Services.AddSingleton<IPreviewTokenService, PreviewTokenService>();
            builder.Services.AddSingleton<ISettingsAuthorizationService, SettingsAuthorizationService>();
            builder.Services.AddSingleton<ISettingsAdministrationRepository, SettingsAdministrationRepository>();
            builder.Services.AddSingleton<ISettingsCacheInvalidator, SettingsCacheInvalidator>();
            builder.Services.AddSingleton<IPreviewBranchEligibilityService, PreviewBranchEligibilityService>();
            builder.Services.AddSingleton<IFormCodeAvailabilityService, FormCodeAvailabilityService>();
            builder.Services.AddScoped<IPreviewRequestContextAccessor, PreviewRequestContextAccessor>();
            builder.Services.AddScoped<IRequestSettingProviderResolver, RequestSettingProviderResolver>();
            builder.Services.AddSingleton<IPreviewContextResolver, PreviewContextResolver>();
            builder.Services.AddScoped<IEmailSenderFactory, EmailSenderFactory>();
            builder.Services.AddScoped<IMelissaClientFactory, MelissaClientFactory>();
            builder.Services.AddHostedService<SettingsCacheGenerationWorker>();

            builder.Services
                .AddSingleton<IActionContextAccessor, ActionContextAccessor>()
                .AddSingleton<IHttpContextAccessor, HttpContextAccessor>()
                .AddScoped<ISettingProvider>(s => s.GetRequiredService<IRequestSettingProviderResolver>()
                    .Resolve(s.GetRequiredService<IHttpContextAccessor>().HttpContext!));

            builder.Services
                .AddScoped<IEmailSender>(s => RegistrationClientProvider.CreateEmail(s.GetRequiredService<ISettingProvider>(), s.GetRequiredService<IEmailSenderFactory>()))
                .AddScoped<IMelissaRestClient>(s => RegistrationClientProvider.CreateMelissa(s.GetRequiredService<ISettingProvider>(), s.GetRequiredService<IMelissaClientFactory>()));

            builder.Services.ConfigureApplicationCookie(o => { o.LogoutPath = "/"; });

            builder.Services.AddSingleton(x => builder.Configuration.GetSection("Clc").Get<AppSettings>()!);
            builder.Services.AddSingleton<IAuthorizationHandler, IsClcUserCheckHandler>();
            builder.Services.AddSingleton<IClaimsTransformation, ClcAzureAdClaimsTransformer>();


            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            HttpContextHelper.Configure(app.Services.GetRequiredService<IHttpContextAccessor>());
            CacheHelper.Configure(app.Services.GetRequiredService<ICache>());
            DbHelper.Global = app.Services.GetRequiredService<IDbHelper>();

            app.UseCors(MyAllowSpecificOrigins);
            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseMiddleware<PreviewRequestContextMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            app.MapControllerRoute(
                name: "history",
                pattern: "history/{action}/{id?}",
                defaults: new { controller = "History", action = "Index" });

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller}/{action}/{orgId?}/{formCode?}",
                defaults: new { controller = "Registration", action = "Create" });

            app.MapControllerRoute(
                name: "create",
                pattern: "create/{orgId?}/{formCode?}",
                defaults: new { controller = "Registration", action = "Create" });


            app.Run();
        }
    }
}
