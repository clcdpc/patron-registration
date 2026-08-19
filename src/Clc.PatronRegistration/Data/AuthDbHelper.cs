using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Data
{
    public interface IAuthDbHelper
    {
        List<string> GetRolesForUser(string? username);
        int? GetOrgForUser(string? username);
        List<string> GetDomains();
    }

    public class AuthDbHelper : IAuthDbHelper
    {
        AppSettings config;
        public string ConnectionString => $"Server={config.Database.Hostname};Database=clc_web_membership;Trusted_Connection=True;TrustServerCertificate=true";

        public AuthDbHelper(string dbHostname, string appName) : this(new AppSettings { Database = new DatabaseSettings { Hostname = dbHostname }, ApplicationName = appName })
        {
            config = new AppSettings { Database = new DatabaseSettings { Hostname = dbHostname }, ApplicationName = appName };
        }

        public AuthDbHelper(AppSettings _config) //: this(_config.Database.Hostname, _config.ApplicationName)
        {
            config = _config;
        }

        public List<string> GetRolesForUser(string? username)
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                var sqlCommand = "select distinct uir.RoleName from clc_web_membership.dbo.UsersInRoles uir where (uir.ApplicationName = @application or @application = 'any') and uir.Username = @username";

                return sql.Query<string>(sqlCommand, new { username, application = config.ApplicationName }).ToList();
            }
        }

        public int? GetOrgForUser(string? username)
        {
            if (!TryGetEmailDomain(username, out var emailDomain))
            {
                return null;
            }

            using (var sql = new SqlConnection(ConnectionString))
            {
                var sqlCommand = "select top 1 ed.LibraryId from clc_web_membership.dbo.EmailDomains ed where (ed.ApplicationName = @application or ed.ApplicationName is null) and ed.EmailDomain = @emailDomain order by ApplicationName desc";

                return sql.QueryFirstOrDefault<int?>(sqlCommand, new { application = config.ApplicationName, emailDomain });
            }
        }

        public static bool TryGetEmailDomain(string? username, out string emailDomain)
        {
            emailDomain = string.Empty;
            if (string.IsNullOrWhiteSpace(username))
            {
                return false;
            }

            var identifier = username.Trim();
            var atIndex = identifier.IndexOf('@');
            if (atIndex <= 0 || atIndex == identifier.Length - 1 || atIndex != identifier.LastIndexOf('@'))
            {
                return false;
            }

            var localPart = identifier[..atIndex];
            var domain = identifier[(atIndex + 1)..];
            if (localPart.Any(char.IsWhiteSpace) || domain.Any(char.IsWhiteSpace) ||
                domain.StartsWith(".", StringComparison.Ordinal) ||
                domain.EndsWith(".", StringComparison.Ordinal) ||
                domain.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            emailDomain = domain;
            return true;
        }

        public List<string> GetDomains()
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                var command = "select distinct EmailDomain from EmailDomains where ApplicationName = @applicationName or ApplicationName is null";

                return sql.Query<string>(command, new { applicationName = config.ApplicationName }).ToList();
            }
        }
    }
}
