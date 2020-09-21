using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IPMIFanControl
{
    public static class CommandExecutor
    {
        public static async Task<string> RunProcess(
            string path,
            string args,
            ILogger _logger,
            CancellationToken cancellationToken)
        {
            string result = null;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            try
            {
                _logger.LogDebug("process starting");
                process.Start();
                _logger.LogDebug("process start");
                process.WaitForExit();
                _logger.LogDebug("process exit");
                var resultTask = process.StandardOutput.ReadToEndAsync();
                var runResult = Task.WaitAny(new Task[] { resultTask }, 3000);
                result = resultTask.Result;

            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Error attempting to call ipmitool!");
            }

            return result;
        }

        public static async Task<string> RunRemoteSSHCommand(string path,
            string args,
            ILogger _logger,
            CancellationToken cancellationToken)
        {
            string result = null;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (sender, args) =>
            {
                var innerProcess = sender as Process;
                if (args.Data.Contains("password"))
                {
                    innerProcess.StandardInput.Write("");
                }
            };

            try
            {
                _logger.LogDebug("process starting");
                process.Start();
                _logger.LogDebug("process start");
                process.WaitForExit();
                _logger.LogDebug("process exit");
                var resultTask = process.StandardOutput.ReadToEndAsync();
                var runResult = Task.WaitAny(new Task[] { resultTask }, 3000);
                result = resultTask.Result;

            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Error attempting to call ipmitool!");
            }

            return result;
        }
    }

    public class IPMIFanControlClient
    {
        public string PathToIpmiToolIfNotDefault;
        public Platform Platform;
        public string IpmiHost;
        public string IpmiUser;
        public string IpmiPassword;
        public OperatingMode? OperatingMode;

        public ILogger _logger;

        public async Task<string> ExecuteIpmiToolCommand(
            string command,
            CancellationToken cancellationToken)
        {
            // Uses default path for either Linux or Windows,
            // unless a path is explicitly provided in appsettings.json.
            var ipmiPath =
                string.IsNullOrWhiteSpace(this.PathToIpmiToolIfNotDefault)
                    ? this.Platform switch
                    {
                        Platform.Linux => "/usr/bin/ipmitool",
                        Platform.Windows =>
                        @"C:\Program Files (x86)\Dell\SysMgt\bmc\ipmitool.exe",
                        _ => throw new ArgumentOutOfRangeException()
                    }
                    : this.PathToIpmiToolIfNotDefault;

            var args =
                $"-I lanplus -H {this.IpmiHost} -U {this.IpmiUser} -P {this.IpmiPassword} {command}";

            this._logger.LogDebug(
                $"Executing:\r\n{ipmiPath} {args.Replace(this.IpmiPassword, "{password}")}");

            string result;
            // if (_environment.IsDevelopment())
            if (false)
            {
                // Your IPMI results may differ from my sample.
                result = await File.ReadAllTextAsync(
                    Path.Combine(Environment.CurrentDirectory, "testdata.txt"),
                    cancellationToken);
            }
            else
            {
                _logger.LogDebug("running process");
                result = await CommandExecutor.RunProcess(ipmiPath, args, this._logger, cancellationToken);
            }

            return result;
        }
    }

    public enum Platform
    {
        Linux,
        Windows
    }

    public enum OperatingMode
    {
        Automatic,
        Manual
    }
}
