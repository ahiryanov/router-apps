using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace lte_reboot;

internal static class ModemInfo
{
	public static List<Device> DiscoverDevices()
	{
		var devicesRaw = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n');
		var devices = new ConcurrentBag<Device>();

		Parallel.ForEach(devicesRaw, deviceRaw =>
		{
			if (string.IsNullOrWhiteSpace(deviceRaw))
				return;
			var parts = deviceRaw.Split(':');
			var name = parts?[0];
			var state = parts?[1];
			if (name?.Contains("cdc-wdm") != true)
				return;
			var iface = $"qmicli --silent -d /dev/{name} --get-wwan-iface".Bash().Replace("\n", "").Trim();
			// qmicli fails on MBIM (or dead) modems: no usable iface -> skip the
			// device so it doesn't poison the rest of the cycle.
			if (string.IsNullOrWhiteSpace(iface) || !Regex.IsMatch(iface, @"^[A-Za-z0-9._-]+$"))
				return;
			var device = new Device
			{
				Name = name,
				State = state,
				Iface = iface
			};
			var qmicliOutput = $"qmicli -p -d /dev/{name} --nas-get-signal-info".Bash();
			if (TryParseRssi(qmicliOutput, out var rssi))
				device.Rssi = rssi;
			device.MobileMode = ParseMobileMode(qmicliOutput);
			var operatorOutput = $"qmicli -p -d /dev/{name} --nas-get-operator-name".Bash();
			device.Operator = ParseOperator(operatorOutput);
			devices.Add(device);
		});

		return devices.ToList();
	}


	public static bool TryParseRssi(string qmicliOutput, out int rssi)
	{
		rssi = 0;
		if (string.IsNullOrWhiteSpace(qmicliOutput))
			return false;

		var m = Regex.Match(qmicliOutput,
			@"\bRSSI:\s*'?\s*(?<v>[-+]?\d+)\s*dBm",
			RegexOptions.IgnoreCase);

		if (!m.Success) return false;
		return int.TryParse(m.Groups["v"].Value, out rssi);
	}

	public static string ParseMobileMode(string qmicliOutput)
	{
		if (string.IsNullOrWhiteSpace(qmicliOutput) || !qmicliOutput.Contains("Successfully"))
			return "Unknown";

		return qmicliOutput.Split('\r', '\n')[1].Replace(":", "");
	}

	public static string ParseOperator(string operatorOutput)
	{
		var m = Regex.Match(operatorOutput,
			@"Service Provider Name[\s\S]*?Name\s*:\s*'([^']+)'",
			RegexOptions.IgnoreCase);
		if (!m.Success) return "N/A";
		return m.Groups[1].Value.Trim();
	}
}
