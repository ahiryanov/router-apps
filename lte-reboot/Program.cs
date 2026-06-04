using System;
using System.Collections.Generic;
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
		AppConfig.DetectSrv();
		using var cts = new CancellationTokenSource();
		void OnShutdown(PosixSignalContext ctx) { ctx.Cancel = true; cts.Cancel(); }
		using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnShutdown);
		using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnShutdown);

		var pingImpl = AppConfig.IsPingIputils ? "iputils" : "busybox";
		logger.LogInformation($"lte-reboot daemon started # interval: {DecisionInterval.TotalSeconds:F0}s # srv: {AppConfig.Srv} # ping: {pingImpl} # flags: {MptcpManager.SubflowFlags} # MaxLoss: {AppConfig.MaxLoss}, MaxRtt: {AppConfig.MaxRtt}");

		while (!cts.IsCancellationRequested)
		{
			var started = Stopwatch.StartNew();
			try
			{
				RunCycle(logger);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "lte-reboot cycle failed");
			}

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

	private static void RunCycle(ILogger logger)
	{
		MptcpManager.CheckMultipleEndpoints(logger);
		var subflows = MptcpManager.GetSubflowMetrics(AppConfig.Srv);
		MptcpManager.RecreateDeadSubflow(logger, subflows);

		var devices = ModemInfo.DiscoverDevices();
		devices = devices.OrderBy(m => m.Name).ToList();

		var subflowsByIface = new Dictionary<string, SubflowMetrics>(StringComparer.Ordinal);
		foreach (var d in devices)
		{
			var ip = MptcpManager.GetRealModemIp(d.Iface);
			if (!string.IsNullOrEmpty(ip) && subflows.TryGetValue(ip, out var m))
				subflowsByIface[d.Iface] = m;
		}

		var throughput = ThroughputTracker.Sample(devices, logger);
		var ctx = ChannelEvaluator.BuildContext(throughput);

		logger.LogInformation($"LTE count: {devices.Count} # idle:{ctx.IsIdle} # maxTx:{ctx.MaxTxKbps / 1000.0:F2}Mbps");
		ConnectionManager.DeviceCountCheck(devices.Count, logger);

		Parallel.ForEach(devices, device =>
		{
			var endpoint = $"ip mptcp endpoint | grep {device.Iface}".Bash();
			var endpointId = !string.IsNullOrWhiteSpace(endpoint) ? endpoint.Split()[2] : null;
			var endpointIsBackup = endpoint?.Contains("backup") == true;

			switch (device.State)
			{
				case "connected":
					subflowsByIface.TryGetValue(device.Iface, out var ssMetrics);
					ctx.PerModemKbps.TryGetValue(device.Iface, out double modemKbps);
					if (endpointIsBackup) // additional ping for warmup modem if backup
					{
						$"ping {AppConfig.Srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
						Thread.Sleep(500);
					}
					var ping = $"ping -c 7 -i 0.2 -W 1 -s 64 -I {device.Iface} {AppConfig.Srv}".Bash();
					var (PacketReceive, PacketLoss, MedianRtt) = ChannelMetrics.ParsePing(ping, AppConfig.IsPingIputils);

					var route = ChannelMetrics.GetRouteWithFlush(device.Iface, device.Name, logger);
					var routeMetric = ChannelMetrics.GetRouteMetric(route);
					var num = Regex.Match(device.Iface, @"\d+").Value;
					var channelState = ChannelMetrics.ComputeState(PacketLoss, MedianRtt, PacketReceive, ssMetrics);

					var history = ChannelHistory.Get(device.Iface);

					var decision = ChannelEvaluator.Decide(device, PacketLoss, MedianRtt, ctx, history);

					var ssLog = ssMetrics != null ? $" # ssRTT:{ssMetrics.RttMs:F1}/{ssMetrics.RttVar:F1}ms" : "";
					logger.LogInformation($"{device.Iface} {(decision.ShouldBackup ? "BACKUP" : "PRIME")} ({decision.Reason}) # Rcv:{PacketReceive} Loss%:{(int)PacketLoss} RTT:{MedianRtt}ms{ssLog} # tx:{modemKbps / 1000.0:F2}Mbps # state:{channelState} # {device.Operator} RSSI:{device.Rssi} Mode:{device.MobileMode}");

					if (decision.ShouldBackup)
					{
						if (!string.IsNullOrWhiteSpace(route) && routeMetric < 1100)
						{
							$"ip route del {AppConfig.Srv} dev {device.Iface}".Bash();
							$"ip route add {AppConfig.Srv} dev {device.Iface} metric {routeMetric + 1100}".Bash();
						}
						if (!string.IsNullOrWhiteSpace(endpointId))
						{
							$"ip mptcp endpoint change id {endpointId} backup".Bash();
							if (!endpointIsBackup)
								logger.LogWarning($"{device.Name} {device.Iface} marked as BACKUP ({decision.Reason})");
						}
					}
					else
					{
						$"ip route del {AppConfig.Srv} dev {device.Iface}".Bash();
						$"ip route replace {AppConfig.Srv} dev {device.Iface} metric {channelState}{num}".Bash();

						MptcpManager.EnsureActiveSubflow(device, endpoint, endpointId, endpointIsBackup, logger);

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
					ChannelHistory.Reset(device.Iface);
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
					ChannelHistory.Reset(device.Iface);
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
					ChannelHistory.Reset(device.Iface);
					logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}");
					break;
			}
		});
	}
}
