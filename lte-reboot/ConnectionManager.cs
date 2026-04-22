using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal enum ConnectionResult { Success, Failed, Skipped }

internal static class ConnectionManager
{
	public static void DeviceCountCheck(int count, ILogger logger)
	{
		string deviceCountLog = $"{AppConfig.LogFile}-devicecount";
		if (count == 8)
		{
			File.Delete(deviceCountLog);
			return;
		}
		if (count < 6)
		{
			if (!File.Exists(deviceCountLog))
			{
				File.WriteAllText(deviceCountLog, "1");
				return;
			}
			int currentCount = 1;
			int.TryParse(File.ReadAllText(deviceCountLog), out currentCount);
			if (currentCount > 2)
			{
				File.WriteAllText(deviceCountLog, "1");
				"echo \"1-1\" > /sys/bus/usb/drivers/usb/unbind && sleep 2 && echo \"1-1\" > /sys/bus/usb/drivers/usb/bind".Bash();
				"echo \"2-1\" > /sys/bus/usb/drivers/usb/unbind && sleep 2 && echo \"2-1\" > /sys/bus/usb/drivers/usb/bind".Bash();
				logger.LogError($"Critical error - reset usb hubs");
			}
			else
			{
				currentCount++;
				File.WriteAllText(deviceCountLog, currentCount.ToString());
			}
		}
	}

	public static void NolteReset(Device device, ILogger logger)
	{
		string nolteModemlog = $"{AppConfig.LogFile}-{device.Name}-nolte";
		if (device.MobileMode == "LTE")
		{
			File.Delete(nolteModemlog);
			return;
		}
		if (!File.Exists(nolteModemlog))
		{
			File.WriteAllText(nolteModemlog, "1");
			return;
		}
		int currentCount = 1;
		int.TryParse(File.ReadAllText(nolteModemlog), out currentCount);

		if (currentCount > AppConfig.RestartCount)
		{
			File.WriteAllText(nolteModemlog, "1");
			$"qmicli -p -d /dev/{device.Name} --dms-set-operating-mode=reset".Bash();
			logger.LogError($"{device.Name} NoLTE reset");
		}
		else
		{
			currentCount++;
			File.WriteAllText(nolteModemlog, currentCount.ToString());
		}
	}

	public static string ConnectionDown(string deviceName, ILogger logger)
	{
		MptcpManager.RemoveDeadEndpoint(deviceName, logger);
		return $"nmcli -w 10 connection down {deviceName}-conn".Bash();
	}

	public static ConnectionResult ConnectionUp(string deviceName, ILogger logger)
	{
		string cooldownLog = $"{AppConfig.LogFile}-{deviceName}-cooldown";
		if (File.Exists(cooldownLog) &&
			int.TryParse(File.ReadAllText(cooldownLog), out var remaining) &&
			remaining > 0)
		{
			File.WriteAllText(cooldownLog, (remaining - 1).ToString());
			logger.LogInformation($"{deviceName} cooldown {remaining}");
			return ConnectionResult.Skipped;
		}

		string resetModemlog = $"{AppConfig.LogFile}-{deviceName}-reset";
		var response = $"nmcli -w 10 connection up {deviceName}-conn".Bash();
		if (response.ToLower().Contains("failed") || response.ToLower().Contains("timeout"))
		{
			File.WriteAllText(cooldownLog, AppConfig.CooldownCycles.ToString());
			if (!File.Exists(resetModemlog))
				File.WriteAllText(resetModemlog, "1");
			else
			{
				int currentCount;
				try
				{
					currentCount = Convert.ToInt32(File.ReadAllText(resetModemlog));
				}
				catch
				{
					File.WriteAllText(resetModemlog, "1");
					currentCount = 1;
				}
				if (currentCount > AppConfig.RestartCount)
				{
					File.WriteAllText(resetModemlog, "1");
					$"qmicli -p -d /dev/{deviceName} --dms-set-operating-mode=reset".Bash();
					logger.LogError($"{deviceName} POWER REBOOT");
				}
				else
				{
					if (currentCount == 7)
					{
						$"qmicli -p -d /dev/{deviceName} --uim-sim-power-off=1".Bash();
						Thread.Sleep(1500);
						$"qmicli -p -d /dev/{deviceName} --uim-sim-power-on=1".Bash();
						logger.LogError($"{deviceName} SIM REBOOT");
					}
					currentCount++;
					File.WriteAllText(resetModemlog, currentCount.ToString());
				}
			}
		}
		else
		{
			File.WriteAllText(resetModemlog, "1");
			File.Delete(cooldownLog);
			return ConnectionResult.Success;
		}
		return ConnectionResult.Failed;
	}
}
