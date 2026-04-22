using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

class Program
{
	static void Main(string[] args)
	{
		using var loggerFactory = LoggerFactory.Create(builder =>
		{
			builder
				.AddSystemdConsole();
		});
		ILogger logger = loggerFactory.CreateLogger("main");

		//### SRV detect ####
		if (File.Exists("/etc/openvpn/client/client.conf"))
			AppConfig.Srv = "cat /etc/openvpn/client/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash();

		//### Startup checks ####
		MptcpManager.CheckMultipleEndpoints(logger);
		MptcpManager.RecreateDeadSubflow(logger);

		if (args.Length == 2)
		{
			int.TryParse(args[0], out var maxLoss);
			int.TryParse(args[1], out var maxRtt);
			AppConfig.MaxLoss = maxLoss;
			AppConfig.MaxRtt = maxRtt;
		}

		bool isPingIputils = "ping -V".Bash().Contains("iputils");
		var devices = new List<Device>();
		var devices_raw = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n');

		foreach (var device_raw in devices_raw)
		{
			if (string.IsNullOrWhiteSpace(device_raw))
				continue;
			var device = new Device();
			device.Name = device_raw.Split(":")?[0];
			device.State = device_raw.Split(":")?[1];
			if (device.Name.Contains("cdc-wdm"))
			{
				device.Iface = $"qmicli --silent -d /dev/{device.Name} --get-wwan-iface".Bash().Replace("\n", "");
				var qmicliOutput = $"qmicli -p -d /dev/{device.Name} --nas-get-signal-info".Bash();
				if (ModemInfo.TryParseRssi(qmicliOutput, out var rssi))
					device.Rssi = rssi;
				device.MobileMode = ModemInfo.ParseMobileMode(qmicliOutput);
				var operatorOutput = $"qmicli -p -d /dev/{device.Name} --nas-get-operator-name".Bash();
				device.Operator = ModemInfo.ParseOperator(operatorOutput);
				devices.Add(device);
			}
		}

		logger.LogInformation($"Device count: {devices.Count}" + $" # server ip: {AppConfig.Srv}" + $" # Max loss {AppConfig.MaxLoss}, Max rtt {AppConfig.MaxRtt}" + " # Ping " + (isPingIputils ? "iputils" : "busybox"));
		ConnectionManager.DeviceCountCheck(devices.Count, logger);
		devices = devices.OrderBy(m => m.Name).ToList();

		foreach (var device in devices)
		{
			//calculate ip mptcp endpoint parameters
			var endpoint = $"ip mptcp endpoint | grep {device.Iface}".Bash();
			var endpointId = !string.IsNullOrWhiteSpace(endpoint) ? endpoint.Split()[2] : null;
			var endpointIsBackup = endpoint?.Contains("backup");

			switch (device.State)
			{
				case "connected":
					var ping = $"ping {AppConfig.Srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
					int PacketReceive = 0;
					double PacketLoss = 100;
					int AvgRtt = 10000;
					if (isPingIputils)
					{
						int.TryParse(new Regex(@"(\w+)\s" + "received").Match(ping)?.Groups[1]?.Value, out PacketReceive);
						double.TryParse(new Regex(@"([\d.,]+)%\s*packet\s+loss").Match(ping)?.Groups[1]?.Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping)?.Groups[1]?.Value, out AvgRtt);
					}
					else
					{
						int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(ping)?.Groups[1]?.Value, out PacketReceive);
						double.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(ping)?.Groups[1]?.Value, out PacketLoss);
						int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping)?.Groups[1]?.Value, out AvgRtt);
					}
					AvgRtt = AvgRtt == 0 ? 10000 : AvgRtt;

					//calculate route parameters
					var route = $"ip route show {AppConfig.Srv} dev {device.Iface}".Bash();
					var routeCount = route.Split('\n').Count();
					if (routeCount > 1)
					{
						logger.LogError($"Device {device.Name} has {routeCount} routes. Flushing.");
						for (int i = 0; i < (routeCount - 1); i++)
							$"ip route del {AppConfig.Srv} dev {device.Iface}".Bash();
					}

					var routeMetric = ChannelMetrics.GetRouteMetric(route);
					var num = Regex.Match(device.Iface, @"\d+").Value;
					var channelState = ChannelMetrics.ComputeState(PacketLoss, AvgRtt, PacketReceive);

					logger.LogInformation($"{device.Name} {device.State}. Receive: {PacketReceive} # Loss %: {PacketLoss} # RTT ms: {AvgRtt} # State: {channelState} # {device.Operator} # RSSI: {device.Rssi} # Mode: {device.MobileMode}");

					if (PacketLoss > AppConfig.MaxLoss || AvgRtt > AppConfig.MaxRtt || device.Rssi! < -80 || (device.MobileMode != "LTE" && device.MobileMode != "Unknown"))
					{
						if (!string.IsNullOrWhiteSpace(route) && routeMetric < 1100)
						{
							$"ip route del {AppConfig.Srv} dev {device.Iface}".Bash();
							$"ip route add {AppConfig.Srv} dev {device.Iface} metric {routeMetric + 1100}".Bash();
						}
						$"ip mptcp endpoint change id {endpointId} backup".Bash();
						logger.LogWarning($"{device.Name} {device.Iface} marked as BACKUP");
					}
					else
					{
						$"ip route del {AppConfig.Srv} dev {device.Iface}".Bash();
						$"ip route replace {AppConfig.Srv} dev {device.Iface} metric {channelState}{num}".Bash();

						var realModemIp = MptcpManager.GetRealModemIp(device.Iface);
						if ((bool)endpointIsBackup)
						{
							$"ip mptcp endpoint del id {endpointId}".Bash();
							Thread.Sleep(500);
							$"ip mptcp endpoint add {realModemIp} dev {device.Iface} subflow laminar".Bash();
							Thread.Sleep(500);
							logger.LogWarning($"{device.Name} {device.Iface} subflow recreated with RealIp {realModemIp} - no backup");
						}
						if (string.IsNullOrWhiteSpace(endpoint))
						{
							$"ip mptcp endpoint add {realModemIp} dev {device.Iface} subflow laminar".Bash();
							logger.LogWarning($"{device.Name} {device.Iface} subflow recreated with RealIp {realModemIp} - no endpoint");
						}
						if (string.IsNullOrWhiteSpace(route))
						{
							var refreshRouteIsUp = ConnectionManager.ConnectionUp(device.Name, logger);
							if (!refreshRouteIsUp)
							{
								logger.LogError($"Refresh route failed {device.Name} ({device.Iface})");
								ConnectionManager.ConnectionDown(device.Name, logger);
							}
							else
								logger.LogWarning($"Refresh route success {device.Name} ({device.Iface})");
						}
					}
					ConnectionManager.NolteReset(device, logger);
					break;

				case "connecting (prepare)":
					ConnectionManager.ConnectionDown(device.Name, logger);
					$"ip mptcp endpoint del id {endpointId}".Bash();
					Thread.Sleep(2000);
					var prepareIsUp = ConnectionManager.ConnectionUp(device.Name, logger);
					if (!prepareIsUp)
					{
						logger.LogWarning($"Failed activation of connecting (prepare) {device.Name}");
						ConnectionManager.ConnectionDown(device.Name, logger);
					}
					else
						logger.LogWarning($"Success activation of connecting (prepare) {device.Name}");
					break;

				case "disconnected":
					$"ip mptcp endpoint del id {endpointId}".Bash();
					var disconnectIsUp = ConnectionManager.ConnectionUp(device.Name, logger);
					if (!disconnectIsUp)
					{
						logger.LogWarning($"Activation failed of disconnected {device.Name}");
						ConnectionManager.ConnectionDown(device.Name, logger);
					}
					else
						logger.LogWarning($"Success activation of disconnected {device.Name}");
					break;

				case "unavailable":
					logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}");
					ConnectionManager.ConnectionUp(device.Name, logger);
					break;
			}
		}
	}
}
