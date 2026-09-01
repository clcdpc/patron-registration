using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Configuration
{
    public class AppSettings
    {
        public string ApplicationName { get; set; }
        public DatabaseSettings Database { get; set; }
        public string BaseUrl { get; set; }
        public RabbitMqSettings RabbitMQ { get; set; }

        public static void Require(string value) { if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("setting is required"); } }
    }
    public class DatabaseSettings
    {
        public string Hostname { get; set; }
        public string Database { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool Encrypt { get; set; } = false;
    }

    public class RabbitMqSettings
    {
        public string Hostname { get; set; }
        public string Virtualhost { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class PostmarkSettings
    {
        public string ApiKey { get; set; }
    }
}
