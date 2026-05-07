using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal enum ConnectionResult { Success, Failed, Skipped }

internal static class ConnectionManager
{
	private static int _lowDeviceCountCycles;
	private static readonly ConcurrentDictionary<string, int> NolteCounters = new();
	private static readonly ConcurrentDictionary<string, int> ConnectionResetCounters = new();
	private static readonly ConcurrentDictionary<string, int> CooldownCounters = new();

	public static void DeviceCountCheck(int count, ILogger logger)
	{
		if (count == 8)
		{
			_lowDeviceCountCycles = 0;
			return;
		}
		if (count < 6)
		{
			if (_lowDeviceCountCycles == 0)
			{
				_lowDeviceCountCycles = 1;
				return;
			}
			if (_lowDeviceCountCycles > 2)
			{
				_lowDeviceCountCycles = 1;
				"echo \"1-1\" > /sys/bus/usb/drivers/usb/unbind && sleep 2 && echo \"1-1\" > /sys/bus/usb/drivers/usb/bind".Bash();
				"echo \"2-1\" > /sys/bus/usb/drivers/usb/unbind && sleep 2 && echo \"2-1\" > /sys/bus/usb/drivers/usb/bind".Bash();
				logger.LogError($"Critical error - reset usb hubs");
			}
			else
			{
				_lowDeviceCountCycles++;
			}
		}
	}

	public static void NolteReset(Device device, ILogger logger)
	{
		if (device.MobileMode == "LTE")
		{
			NolteCounters.TryRemove(device.Name, out _);
			return;
		}
		if (!NolteCounters.TryGetValue(device.Name, out var currentCount))
		{
			NolteCounters[device.Name] = 1;
			return;
		}

		if (currentCount > AppConfig.RestartCount)
		{
			NolteCounters[device.Name] = 1;
			$"qmicli -p -d /dev/{device.Name} --dms-set-operating-mode=reset".Bash();
			logger.LogError($"{device.Name} NoLTE reset");
		}
		else
		{
			NolteCounters[device.Name] = currentCount + 1;
		}
	}

	public static string ConnectionDown(string deviceName, ILogger logger)
	{
		MptcpManager.RemoveDeadEndpoint(deviceName, logger);
		return $"nmcli -w 10 connection down {deviceName}-conn".Bash();
	}

	public static ConnectionResult ConnectionUp(string deviceName, ILogger logger)
	{
		if (CooldownCounters.TryGetValue(deviceName, out var remaining) && remaining > 0)
		{
			CooldownCounters[deviceName] = remaining - 1;
			logger.LogInformation($"{deviceName} cooldown {remaining}");
			return ConnectionResult.Skipped;
		}

		var response = $"nmcli -w 10 connection up {deviceName}-conn".Bash();
		if (response.ToLower().Contains("failed") || response.ToLower().Contains("timeout"))
		{
			CooldownCounters[deviceName] = AppConfig.CooldownCycles;
			if (!ConnectionResetCounters.TryGetValue(deviceName, out var currentCount))
			{
				ConnectionResetCounters[deviceName] = 1;
			}
			else if (currentCount > AppConfig.RestartCount)
			{
				ConnectionResetCounters[deviceName] = 1;
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
				ConnectionResetCounters[deviceName] = currentCount + 1;
			}
		}
		else
		{
			ConnectionResetCounters[deviceName] = 1;
			CooldownCounters.TryRemove(deviceName, out _);
			return ConnectionResult.Success;
		}
		return ConnectionResult.Failed;
	}
}
