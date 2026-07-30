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
    public class AuthDbHelper
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

        public List<string> GetRolesForUser(string username)
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                var sqlCommand = "select distinct uir.RoleName from clc_web_membership.dbo.UsersInRoles uir where (uir.ApplicationName = @application or @application = 'any') and uir.Username = @username";

                return sql.Query<string>(sqlCommand, new { username, application = config.ApplicationName }).ToList();
            }
        }

        public int GetOrgForUser(string username)
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                var emailDomain = username.Split('@')[1];
                var sqlCommand = "select top 1 ed.LibraryId from clc_web_membership.dbo.EmailDomains ed where (ed.ApplicationName = @application or ed.ApplicationName is null) and ed.EmailDomain = @emailDomain order by ApplicationName";

                return sql.QueryFirst<int>(sqlCommand, new { application = config.ApplicationName, emailDomain });
            }
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
