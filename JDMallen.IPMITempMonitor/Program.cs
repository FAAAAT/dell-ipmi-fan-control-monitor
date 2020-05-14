using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JDMallen.IPMITempMonitor
{
	public class Program
	{
		public static void Main(string[] args)
		{
			var builder = CreateHostBuilder(args);

			var host = builder.Build(); // Separated for ease of inspection

			host.Run();
		}

		public static IHostBuilder CreateHostBuilder(string[] args) =>
			Host.CreateDefaultBuilder(args)
				.UseWindowsService()
				.UseSystemd()
				.ConfigureServices(
					(hostContext, services) =>
					{
						services.AddHostedService<Worker>();
                        var configuration = hostContext.Configuration.GetSection("Settings");
#if DEBUG
						Console.WriteLine(hostContext.Configuration.AsEnumerable().Aggregate("",(o,n)=>o+"\r\n"+n.Key+":"+n.Value));
#endif
						services.Configure<Settings>(configuration);
					});
	}
}
