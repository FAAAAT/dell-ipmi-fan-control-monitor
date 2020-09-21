using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IPMIFanControl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JDMallen.IPMITempMonitor
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly Settings _settings;
        private static DateTime _timeFellBelowTemp = DateTime.MinValue;
        private readonly IHostEnvironment _environment;
        private bool _belowTemp;
        private readonly IDictionary<string, List<int>> _lastTenTempsCache;

        private const string CheckTemperatureControlCommand =
            "sdr type temperature";

        private const string EnableAutomaticTempControlCommand =
            "raw 0x30 0x30 0x01 0x01";

        private const string DisableAutomaticTempControlCommand =
            "raw 0x30 0x30 0x01 0x00";

        private const string StaticFanSpeedFormatString =
            "raw 0x30 0x30 0x02 0xff 0x{0}";


        public IDictionary<string, IPMIFanControlClient> ipmiFanControlClients;
        public IDictionary<string, TemperatureClient> tempClients;

        public Worker(
            ILogger<Worker> logger,
            IOptions<Settings> settings,
            IHostEnvironment environment)
        {
            _logger = logger;
            _environment = environment;
            _settings = settings.Value;
            this.ipmiFanControlClients = new Dictionary<string, IPMIFanControlClient>();
            this.tempClients = new Dictionary<string, TemperatureClient>();
            this._lastTenTempsCache = new Dictionary<string, List<int>>();

            if (_settings.HostSettings == null)
            {
                throw new Exception("no config found!");
            }

            foreach (var hostSetting in _settings.HostSettings)
            {
                if (this._lastTenTempsCache.ContainsKey(hostSetting.Name))
                {
                    throw new Exception("Duplicate client name detected.");
                }

                this._lastTenTempsCache.Add(hostSetting.Name, new List<int>(_settings.RollingAverageNumberOfTemps));

                var fanControlClient = new IPMIFanControlClient()
                {
                    _logger = logger,
                    PathToIpmiToolIfNotDefault = hostSetting.PathToIpmiToolIfNotDefault,
                    Platform = this._settings.Platform,
                    IpmiHost = hostSetting.Host,
                    IpmiUser = hostSetting.User,
                    IpmiPassword = hostSetting.Password,
                };
                this.ipmiFanControlClients.Add(hostSetting.Name, fanControlClient);

                TemperatureClient client = null;
                if (string.Compare(hostSetting.Type, "ipmi", true) == 0)
                {
                    client = new TemperatureIpmiClient(fanControlClient, hostSetting.RegexToRetrieveTemp,
                        CheckTemperatureControlCommand);
                }
                else if (string.Compare(hostSetting.Type, "ssh_lm_sensors", true) == 0)
                    client = new TemperatureLMSensorsClient(hostSetting.LMHost, hostSetting.LMUser, hostSetting.LMPassword,
                        hostSetting.RegexToRetrieveTemp, this._logger);
                else
                    throw new Exception("no match host type");

                this.tempClients.Add(hostSetting.Name,
                   client);
            }
        }

        protected override async Task ExecuteAsync(
            CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Notify_Socket = {Environment.GetEnvironmentVariable("NOTIFY_SOCKET")}");
            _logger.LogInformation($"Detected OS: {_settings.Platform:G}.");
            _logger.LogInformation($"Detected OS: what the fuck.");

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var hostSetting in this._settings.HostSettings)
                {
                    _logger.LogInformation($"Starting process {hostSetting.Name}");
                    try
                    {
                        if (!this._lastTenTempsCache.ContainsKey(hostSetting.Name) ||
                                        !this.tempClients.ContainsKey(hostSetting.Name) ||
                                        !this.ipmiFanControlClients.ContainsKey(hostSetting.Name))
                        {
                            this._logger.LogError($"some necessary component init failed. Name:{hostSetting.Name},Host:{hostSetting.Host}");
                            continue;
                        }

                        var cachedTemps = this._lastTenTempsCache[hostSetting.Name];
                        var tempClient = this.tempClients[hostSetting.Name];
                        var fanControlClient = this.ipmiFanControlClients[hostSetting.Name];

                        var temp = await tempClient.CheckLatestTemperature(cancellationToken);
                        PushTemperature(temp, cachedTemps);
                        var rollingAverageTemp = GetRollingAverageTemperature(cachedTemps);

                        _logger.LogInformation(
                            "Server {server} fan control is {operatingMode}, temp is {temp} C, rolling average temp is {rollingAverageTemp} at {time}. Max temp:{maxtemp}",
                            hostSetting.Name,
                            fanControlClient.OperatingMode,
                            temp,
                            rollingAverageTemp,
                            DateTimeOffset.Now,
                            hostSetting.MaxTempInC);

                        // If the temp goes above the max threshold,
                        // immediately switch to Automatic fan mode.
                        if (temp > hostSetting.MaxTempInC
                            || rollingAverageTemp > hostSetting.MaxTempInC)
                        {
                            _belowTemp = false;
                            if (fanControlClient.OperatingMode == OperatingMode.Automatic)
                            {
                                await Delay(cancellationToken);
                                continue;
                            }

                            await SwitchToAutomaticTempControl(fanControlClient, cancellationToken);
                        }
                        // Only switch back to manual if both the current temp
                        // AND the rolling average are back below the set max.
                        else
                        {
                            if (!_belowTemp)
                            {
                                // Record the first record of when the temp dipped
                                // below the max temp threshold. This is an extra
                                // safety measure to ensure that Automatic mode isn't
                                // turned off too soon. You can see its usage in
                                // SwitchToManualTempControl().
                                _timeFellBelowTemp = DateTime.UtcNow;
                            }

                            _belowTemp = true;

                            if (fanControlClient.OperatingMode == OperatingMode.Manual)
                            {
                                await Delay(cancellationToken);
                                await SwitchToManualTempControl(fanControlClient, cancellationToken);
                                continue;
                            }

                            await SwitchToManualTempControl(fanControlClient, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        this._logger.LogError(ex+"");
                    }
                }

                await Delay(cancellationToken);

            }

            _logger.LogInformation($"Exiting.");
        }

        private void PushTemperature(int temp, List<int> temps)
        {
            if (temps == null)
                throw new Exception("push temp to a null reference");
            if (temps.Count == _settings.RollingAverageNumberOfTemps)
            {
                temps.RemoveAt(0);
            }

            temps.Add(temp);

        }

        private double GetRollingAverageTemperature(List<int> temps)
        {
            if (temps == null)
                throw new Exception("get average temp from a null reference");
            return temps.Average();
        }

        private async Task Delay(
            CancellationToken cancellationToken)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.PollingIntervalInSeconds),
                cancellationToken);
        }

        private async Task SwitchToAutomaticTempControl(IPMIFanControlClient fanControlClient,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Attempting switch to automatic mode.");
            await fanControlClient.ExecuteIpmiToolCommand(
                EnableAutomaticTempControlCommand,
                cancellationToken);
            fanControlClient.OperatingMode = OperatingMode.Automatic;
        }

        private async Task SwitchToManualTempControl(IPMIFanControlClient fanControlClient,
            CancellationToken cancellationToken)
        {
            try
            {
                var timeSinceLastActivation =
                        DateTime.UtcNow - _timeFellBelowTemp;

                var threshold =
                    TimeSpan.FromSeconds(_settings.BackToManualThresholdInSeconds);

                if (timeSinceLastActivation < threshold)
                {
                    _logger.LogWarning(
                        "Manual threshold not crossed yet. Staying in Automatic mode for at least another "
                        + $"{(int)(threshold - timeSinceLastActivation).TotalSeconds} seconds.");
                    return;
                }

                _logger.LogInformation("Attempting switch to manual mode.");

                await fanControlClient.ExecuteIpmiToolCommand(
                    DisableAutomaticTempControlCommand,
                    cancellationToken);

                var fanSpeedCommand = string.Format(
                    StaticFanSpeedFormatString,
                    _settings.ManualModeFanPercentage.ToString("X"));

                await fanControlClient.ExecuteIpmiToolCommand(fanSpeedCommand, cancellationToken);
                fanControlClient.OperatingMode = OperatingMode.Manual;
            }
            catch (Exception)
            {
                await StopAsync(cancellationToken);
            }
        }




        /// <summary>
        /// Triggered when the application host is ready to start the service.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Monitor starting. Setting fan control to automatic to start.");

            foreach (var fanControlClient in ipmiFanControlClients.Values)
            {
                fanControlClient.OperatingMode = OperatingMode.Automatic;
#pragma warning disable 4014
                SwitchToAutomaticTempControl(fanControlClient, cancellationToken);
#pragma warning restore 4014
            }

            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("Monitor stopping");
            return base.StopAsync(cancellationToken);
        }
    }
}
