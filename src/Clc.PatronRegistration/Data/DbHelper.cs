using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Data
{

    public interface IDbHelper
    {
        IEnumerable<OrganizationsGetRow> GetSelfRegistrationBranches(int? libraryId = null);
        IEnumerable<OrganizationsGetRow> GetSelfRegistrationLibraries(int? libraryId = null);
        IEnumerable<OrganizationsGetRow> GetSelfRegistrationOrganizations(int? libraryId = null);
        IEnumerable<OrganizationsGetRow> GetPickupBranches(int? libraryId = null);
        IEnumerable<RegistrationFormSetting> GetAllSettings();
        IEnumerable<GetGendersToOrganizations_Result> GetGendersToOrganizations(int nOrganizationId);
        bool CheckPatronIsDuplicate(int libraryId, string nameFirst, string nameLast, DateTime birthDate);
        bool AddRegistrationHistoryEntry(RegistrationHistoryEntry entry);
        IEnumerable<RegistrationHistoryEntry> GetRegistrationHistory(int orgId, string term = "", int numRows = 50);
        RegistrationHistoryEntry GetRegistrationHistoryEntry(int id);

        bool UpdateSetting(int orgId, string mnemonic, string value, string formCode = "");
    }
    public class DbHelper : DbHelperBase, IDbHelper
    {
        public static IDbHelper Global;
        public DbHelper(IDbHelperSettings settings) : base(settings.db_hostname, settings.db_name)
        {

        }
        public bool UpdateSetting(int orgId, string mnemonic, string value, string formCode = "")
        {
            var sql = "exec clcdb.dbo.InsertOrUpdateRegistrationSetting @orgId, @mnemonic, @formCode, @value";
            return Execute(sql, new { orgId, mnemonic, value, formCode });
        }

        public IEnumerable<OrganizationsGetRow> GetSelfRegistrationBranches(int? libraryId = null) => GetSelfRegistrationOrganizations(libraryId).Where(o => o.OrganizationCodeID == 3);
        public IEnumerable<OrganizationsGetRow> GetSelfRegistrationLibraries(int? libraryId = null) => GetSelfRegistrationOrganizations(libraryId).Where(o => o.OrganizationCodeID == 2);
        public IEnumerable<OrganizationsGetRow> GetSelfRegistrationOrganizations(int? libraryId = null) => Query<OrganizationsGetRow>("clcdb.dbo.GetSelfRegistrationOrganizations", new { libraryId }, CommandType.StoredProcedure);
        public IEnumerable<OrganizationsGetRow> GetPickupBranches(int? libraryId = null) => Query<OrganizationsGetRow>("clcdb.dbo.GetPickupBranches", new { libraryId }, CommandType.StoredProcedure);
        public IEnumerable<RegistrationFormSetting> GetAllSettings() => Select<RegistrationFormSetting>("select * from clcdb.dbo.RegistrationFormSettings");
        public IEnumerable<GetGendersToOrganizations_Result> GetGendersToOrganizations(int nOrganizationId) => Query<GetGendersToOrganizations_Result>("polaris.Polaris.GetGendersToOrganizations", new { nOrganizationId }, CommandType.StoredProcedure).Where(g => g.Display).OrderBy(g => g.DisplayOrder);
        public bool CheckPatronIsDuplicate(int libraryId, string nameFirst, string nameLast, DateTime birthDate) { var patrons = Query<int>("clcdb.dbo.CheckPatronIsDuplicate", new { libraryId, nameFirst, nameLast, birthDate }, CommandType.StoredProcedure); return (patrons?.Any()).GetValueOrDefault(); }
        public bool AddRegistrationHistoryEntry(RegistrationHistoryEntry entry) => Execute(@"
            insert into dbo.RegistrationHistory(Timestamp,PatronBranchId,FirstName,LastName,Email,Phone,StreetOne,StreetTwo,City,State,ZIP,IPAddress,Result,RegistrationBody,PapiResponse,SettingsSnapshot,MelissaResponse)
            values (@Timestamp,@PatronBranchId,@FirstName,@LastName,@Email,@Phone,@StreetOne,@StreetTwo,@City,@State,@ZIP,@IPAddress,@Result,@RegistrationBody,@PapiResponse,@SettingsSnapshot,@MelissaResponse)"
            , entry);
        public IEnumerable<RegistrationHistoryEntry> GetRegistrationHistory(int orgId, string term = "", int numRows = 50)
        {
            term = $"%{term}%";

            var sql = $@"
            select top {numRows} 
                     h.Id
                    ,h.Timestamp
		            ,h.PatronBranchId
		            ,h.FirstName
		            ,h.LastName
		            ,h.Email
		            ,h.Phone
		            ,h.StreetOne
		            ,h.StreetTwo
		            ,h.City
		            ,h.State
		            ,h.ZIP
		            ,h.IPAddress
		            ,h.Result
            from clcdb.dbo.RegistrationHistory h
            where (h.PatronBranchId = @orgid or @orgid = -1)
            and (h.FirstName like @term
	            or h.LastName like @term
	            or h.Email like @term
	            or h.Phone like @term
	            or h.StreetOne like @term
	            or h.StreetTwo like @term
	            or h.City like @term
	            or h.State like @term
	            or h.ZIP like @term
	            or h.IPAddress like @term)
            order by h.Timestamp desc";

            return Select<RegistrationHistoryEntry>(sql, new { orgId, term });
        }

        public RegistrationHistoryEntry GetRegistrationHistoryEntry(int id)
        {
            var sql = @"select * from RegistrationHistory where id = @id";
            return SelectFirst<RegistrationHistoryEntry>(sql, new { id });
        }
    }

    public class RegistrationHistoryEntry
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int PatronBranchId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string StreetOne { get; set; } = string.Empty;
        public string StreetTwo { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string ZIP { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string RegistrationBody { get; set; } = string.Empty;
        public string PapiResponse { get; set; } = string.Empty;
        public string SettingsSnapshot { get; set; } = string.Empty;
        public string MelissaResponse { get; set; } = string.Empty;

        public string Address => $"{StreetOne} {StreetTwo} {City}, {State} {ZIP}";

        public RegistrationHistoryEntry()
        {

        }

        public RegistrationHistoryEntry(string ip, string result, Registration? p = null)
        {
            IPAddress = ip;
            Result = result;

            if (p != null)
            {
                RegistrationBody = JsonConvert.SerializeObject(p);
                PatronBranchId = p.PatronBranchID;
                FirstName = p.NameFirst;
                LastName = p.NameLast;
                Email = p.EmailAddress ?? "";
                Phone = p.PhoneVoice1 ?? "";
                StreetOne = p.StreetOne;
                StreetTwo = p.StreetTwo ?? "";
                City = p.City;
                State = p.State;
                ZIP = p.PostalCode;
                if (p.MelissaResponse != null) { MelissaResponse = JsonConvert.SerializeObject(p.MelissaResponse); }
            }
        }
    }

    public abstract class DbHelperBase
    {
        public string Server { get; set; }
        public string DbName { get; set; }

        public string ConnectionString => $"Server={Server};Database={DbName};Trusted_Connection=True;Encrypt=False;";

        public DbHelperBase(string server, string dbName)
        {
            Server = server;
            DbName = dbName;
        }

        public T SelectFirst<T>(string sqlCommand, object parameters)
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                return sql.QueryFirstOrDefault<T>(sqlCommand, parameters);
            }
        }

        public IEnumerable<T> Select<T>(string sqlCommand, object? parameters = null)
        {
            using (var sql = new SqlConnection(ConnectionString))
            {
                return sql.Query<T>(sqlCommand, parameters);
            }
        }

        public bool Execute(string sqlCommand, object parameters)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var result = connection.Execute(sqlCommand, parameters);
                return result > 0;
            }
        }

        public IEnumerable<T> Query<T>(string sqlCommand, object parameters, CommandType commandType = CommandType.Text)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var result = connection.Query<T>(sqlCommand, param: parameters, commandType: commandType);
                return result;
            }
        }
    }
}
