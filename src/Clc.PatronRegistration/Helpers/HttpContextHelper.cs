using Microsoft.AspNetCore.Http;

namespace Clc.PatronRegistration.Helpers
{
    public static class HttpContextHelper
    {
        private static IHttpContextAccessor m_httpContextAccessor = default!;

        public static void Configure(IHttpContextAccessor httpContextAccessor)
        {
            m_httpContextAccessor = httpContextAccessor;
        }

        public static string GetTrueClientIp() => Current.Request.GetTrueClientIP();

        public static HttpContext Current => m_httpContextAccessor.HttpContext ?? new DefaultHttpContext();

        public static bool IsInjectedForm => Current.Request.Method.Equals("post", StringComparison.OrdinalIgnoreCase);
    }
}
