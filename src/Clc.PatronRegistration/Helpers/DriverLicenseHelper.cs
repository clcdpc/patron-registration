using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Helpers
{
    public static class DriverLicenseHelper
    {
        public static DriversLicenseInfo ProcessDlMagstripe(string data)
        {
            var output = new DriversLicenseInfo();
            if (!data.Contains("$")) { return output; }

            data = data.Remove(0, 1);

            output.State = new string(data.Take(2).ToArray());

            data = data.Remove(0, 2);
            if (data.Take(13).Contains('^'))
            {
                output.City = data.Split('^')[0];
                data = data.Remove(0, output.City.Length + 1);
            }
            else
            {
                output.City = new string(data.Take(13).ToArray());
                data = data.Remove(0, 13);
            }

            output.LastName = data.Split('$')[0];
            output.FirstName = data.Split('$')[1];

            var birthdateData = Regex.Match(data, @"(?<=[=])(.*)(?=\?)").Value.Substring(4, 8);
            if (DateTime.TryParseExact(birthdateData, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime bdParse))
            {
                output.Birthdate = bdParse;
            }

            output.Address = data.Split('^')[1];
            if (data.Contains('#'))
            {
                output.ZIP = new string(data.Split('#')[1].Skip(2).Take(5).ToArray());
                var gender = new string(data.Split('#')[1].Skip(29).Take(1).ToArray());

                output.Gender = gender == "1" ? "M" : gender == "2" ? "F" : "";

            }
            else if (data.Contains('+'))
            {
                output.ZIP = new string(data.Split('+')[1].Skip(2).Take(5).ToArray());
            }
            return output;
        }

        public static DriversLicenseInfo ProcessDlBarcode(string data)
        {
            var output = new DriversLicenseInfo
            {
                LastName = Regex.Match(data, "DCS(.*?)DAC").Groups[1].Value,
                FirstName = Regex.Match(data, "DAC(.*?)DAD").Groups[1].Value,
                Address = Regex.Match(data, "DAG(.*?)DAI").Groups[1].Value,
                City = Regex.Match(data, "DAI(.*?)DAJ").Groups[1].Value,
                State = Regex.Match(data, "DAJ(.*?)DAK").Groups[1].Value
            };

            var zipMatch = Regex.Match(data, @"DAK(.*?)\s").Groups[1].Value;
            output.ZIP = zipMatch.Substring(0, Math.Min(5, zipMatch.Length));

            var birthdateData = Regex.Match(data, "DBB(.*?)DBC").Groups[1].Value;
            if (DateTime.TryParseExact(birthdateData, "MMddyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime bdParse))
            {
                output.Birthdate = bdParse;
            }

            var gender = Regex.Match(data, @"DBC(.*?)DAY").Groups[1].Value;
            output.Gender = gender == "1" ? "M" : gender == "2" ? "F" : "";

            return output;
        }
    }
}
