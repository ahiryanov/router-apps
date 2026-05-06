using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

class Program
{
	private static readonly TimeSpan DecisionInterval = TimeSpan.FromSeconds(20);

	static async Task Main(string[] args)
	{
		using var loggerFactory = LoggerFactory.Create(builder =>
		{
			builder
				.AddSystemdConsole();
		});
		ILogger logger = loggerFactory.CreateLogger("main");

		AppConfig.ApplyArgs(args);
		using var cts = new CancellationTokenSource();
		using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
		{
			context.Cancel = true;
			cts.Cancel();
		});
		using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
		{
			context.Cancel = true;
			cts.Cancel();
		});

		bool isPingIputils = "ping -V".Bash().Contains("iputils");
		logger.LogInformation($"lte-reboot daemon started. Decision interval: {DecisionInterval.TotalSeconds:F0}s # Ping " + (isPingIputils ? "iputils" : "busybox") + $" # flags: {MptcpManager.SubflowFlags}");

		while (!cts.IsCancellationRequested)
		{
			var started = Stopwatch.StartNew();
			try
			{
				RunCycle(logger, isPingIputils);
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "lte-reboot cycle failed");
			}
			started.Stop();

			var delay = DecisionInterval - started.Elapsed;
			if (delay > TimeSpan.Zero)
			{
				try
				{
					await Task.Delay(delay, cts.Token);
				}
				catch (OperationCanceledException) when (cts.IsCancellationRequested)
				{
					break;
				}
			}
			else
			{
				logger.LogWarning($"lte-reboot cycle took {started.Elapsed.TotalSeconds:F1}s, longer than {DecisionInterval.TotalSeconds:F0}s interval");
			}
		}

		logger.LogInformation("lte-reboot daemon stopped");
	}

	private static void RunCycle(ILogger logger, bool isPingIputils)
	{
		AppConfig.DetectSrv();
		MptcpManager.CheckMultipleEndpoints(logger);
		var subflows = MptcpManager.GetSubflowMetrics(AppConfig.Srv);
		MptcpManager.RecreateDeadSubflow(logger, subflows);

		var devices = ModemInfo.DiscoverDevices();

		logger.LogInformation($"LTE count: {devices.Count}" + $" # srv: {AppConfig.Srv}" + $" # MaxLoss {AppConfig.MaxLoss}, MaxRtt {AppConfig.MaxRtt}" + " # Ping " + (isPingIputils ? "iputils" : "busybox") + $" # flags: {MptcpManager.SubflowFlags}");
		ConnectionManager.DeviceCountCheck(devices.Count, logger);
		devices = devices.OrderBy(m => m.Name).ToList();

		Parallel.ForEach(devices, device =>
		{
			var endpoint = $"ip mptcp endpoint | grep {device.Iface}".Bash();
			var endpointId = !string.IsNullOrWhiteSpace(endpoint) ? endpoint.Split()[2] : null;
			var endpointIsBackup = endpoint?.Contains("backup");

			switch (device.State)
			{
				case "connected":
					var deviceIp = MptcpManager.GetRealModemIp(device.Iface);
					subflows.TryGetValue(deviceIp ?? string.Empty, out var ssMetrics);

					var ping = $"ping {AppConfig.Srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
					var (PacketReceive, PacketLoss, AvgRtt) = ChannelMetrics.ParsePing(ping, isPingIputils);

					var route = ChannelMetrics.GetRouteWithFlush(device.Iface, device.Name, logger);
					var routeMetric = ChannelMetrics.GetRouteMetric(route);
					var num = Regex.Match(device.Iface, @"\d+").Value;
					var channelState = ChannelMetrics.ComputeState(PacketLoss, AvgRtt, PacketReceive, ssMetrics);

					var ssLog = ssMetrics != null
						? $" # ssRTT: {ssMetrics.RttMs:F1}/{ssMetrics.RttVar:F1}ms"
						: "";
					logger.LogInformation($"{device.Iface} {device.State}. Rcv: {PacketReceive} # Loss%: {(int)PacketLoss} # RTTms: {AvgRtt}{ssLog} # State: {channelState} # {device.Operator} # RSSI: {device.Rssi} # Mode: {device.MobileMode}");

					if (PacketLoss > AppConfig.MaxLoss || AvgRtt > AppConfig.MaxRtt || device.Rssi! < -85 || channelState > 55 || (device.MobileMode != "LTE" && device.MobileMode != "Unknown"))
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

						MptcpManager.EnsureActiveSubflow(device, endpoint, endpointId, (bool)endpointIsBackup, logger);

						if (string.IsNullOrWhiteSpace(route))
						{
							var refreshResult = ConnectionManager.ConnectionUp(device.Name, logger);
							if (refreshResult == ConnectionResult.Failed)
							{
								logger.LogError($"Refresh route failed {device.Name} ({device.Iface})");
								ConnectionManager.ConnectionDown(device.Name, logger);
							}
							else if (refreshResult == ConnectionResult.Success)
								logger.LogWarning($"Refresh route success {device.Name} ({device.Iface})");
						}
					}
					ConnectionManager.NolteReset(device, logger);
					break;

				case "connecting (prepare)":
					ConnectionManager.ConnectionDown(device.Name, logger);
					$"ip mptcp endpoint del id {endpointId}".Bash();
					Thread.Sleep(1000);
					var prepareResult = ConnectionManager.ConnectionUp(device.Name, logger);
					if (prepareResult == ConnectionResult.Failed)
					{
						logger.LogWarning($"Failed activation of connecting (prepare) {device.Name}");
						ConnectionManager.ConnectionDown(device.Name, logger);
					}
					else if (prepareResult == ConnectionResult.Success)
						logger.LogWarning($"Success activation of connecting (prepare) {device.Name}");
					break;

				case "disconnected":
					$"ip mptcp endpoint del id {endpointId}".Bash();
					var disconnectResult = ConnectionManager.ConnectionUp(device.Name, logger);
					if (disconnectResult == ConnectionResult.Failed)
					{
						logger.LogWarning($"Activation failed of disconnected {device.Name}");
						ConnectionManager.ConnectionDown(device.Name, logger);
					}
					else if (disconnectResult == ConnectionResult.Success)
						logger.LogWarning($"Success activation of disconnected {device.Name}");
					break;

				case "unavailable":
					logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}");
					break;
			}
		});
	}
}
