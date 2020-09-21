using System;
using System.Runtime.InteropServices;
using IPMIFanControl;

namespace JDMallen.IPMITempMonitor
{
	public class Settings
	{
		public HostSetting[] HostSettings { get; set; }

		public int ManualModeFanPercentage { get; set; } = 30;

		public int PollingIntervalInSeconds { get; set; } = 30;

		public int RollingAverageNumberOfTemps { get; set; } = 10;

		public int BackToManualThresholdInSeconds { get; set; } = 60;

		public Platform Platform
		{
			get
			{
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					return Platform.Windows;
				}

				if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				{
					return Platform.Linux;
				}

				throw new PlatformNotSupportedException(
					"Only works on Windows or Linux.");
			}
		}
	}

	public class HostSetting
	{
		public string Name { get; set; }
		#region IPMI
		public string Host { get; set; }
		public string User { get; set; }
        public string Password { get; set; }

		#endregion

		#region lm_sensors

		public string LMHost { get; set; } = string.Empty;
        public string LMUser { get; set; } = string.Empty;
        public string LMPassword { get; set; } = string.Empty;

		#endregion

		public string Type { get; set; }

        public string PathToIpmiToolIfNotDefault { get; set; }

		public string RegexToRetrieveTemp { get; set; }

		public int MaxTempInC { get; set; } = 50;

	}
}
