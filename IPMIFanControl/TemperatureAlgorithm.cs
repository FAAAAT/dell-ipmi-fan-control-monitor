using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SshNet;

namespace IPMIFanControl
{
    public abstract class TemperatureClient
    {
        public abstract Task<int> CheckLatestTemperature(
            CancellationToken cancellationToken);
    }

    public class TemperatureIpmiClient : TemperatureClient
    {
        private IPMIFanControlClient IpmifanClient;
        public string RegexToRetrieveTemp;
        public string CheckTemperatureControlCommand;

        public TemperatureIpmiClient(IPMIFanControlClient fanClient,string regexToRetrieveTemp,string checkCommand)
        {
            this.IpmifanClient = fanClient;
            this.RegexToRetrieveTemp = regexToRetrieveTemp;
            this.CheckTemperatureControlCommand = checkCommand;
        }

        /// <summary>
        /// Calls iDRAC for latest temperature. Ensure that the Regex setting
        /// to retrieve the temp has been updated for your particular system.
        /// Mine is set for an R620 system.
        /// </summary>
        /// <returns></returns>

        public override async Task<int> CheckLatestTemperature(CancellationToken cancellationToken)
        {
            var result =
                await this.IpmifanClient.ExecuteIpmiToolCommand(
                    CheckTemperatureControlCommand,
                    cancellationToken);
            var matches = Regex.Matches(
                result,
                this.RegexToRetrieveTemp,
                RegexOptions.Multiline);
            var intTemp = matches.Select(x => x.Groups.LastOrDefault()?.Value)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x =>
                {
                    if (int.TryParse(x, out var temp)) return temp;
                    else return 0;
                }).Max();

            return intTemp;
        }
    }

    public class TemperatureLMSensorsClient : TemperatureClient,IDisposable
    {
        public string Host;
        public string User;
        public string Password;
        public Regex retrieveTempRegex;
        public ILogger logger;
        private SshClient client;
        private bool Available;

        public TemperatureLMSensorsClient(string host,string user,string password,string tempRegex,ILogger logger)
        {
            this.Host = host;
            this.User = user;
            this.Password = password;
            this.retrieveTempRegex = new Regex(tempRegex);
            this.logger = logger;
            this.client = new SshClient(host,user,password);
            this.client.Connect();
            this.Available = true;
        }

        public override async Task<int> CheckLatestTemperature(CancellationToken cancellationToken)
        {
            if (!Available)
            {
                this.client = new SshClient(this.Host, this.User, this.Password);
                client.Connect();
                Available = true;
            }

            try
            {
                using (var command = client.RunCommand("sensors"))
                {
                    //(?<=Core.\d+:\W*\+)\d*\.\d+
                    if (command.ExitStatus == 0)
                    {
                        var temps = this.retrieveTempRegex.Matches(command.Result).Select(x => float.Parse(x.Value));
                        return (int) temps.Max();
                    }
                    else
                    {
                        throw new Exception(command.Error);
                    }
                }
            }
            catch (Exception)
            {
                Available = false;
                throw;
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        public void Dispose()
        {
            this.client?.Dispose();
        }
    }
}
