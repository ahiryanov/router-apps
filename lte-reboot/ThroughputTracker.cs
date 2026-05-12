using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal record RouterThroughput(
	string TunnelIface,
	double? TunnelKbps,
	Dictionary<string, double> PerModemKbps
);

internal static class ThroughputTracker
{
	private record Snapshot(long RxBytes, long TxBytes, DateTime Ts);

	private static readonly ConcurrentDictionary<string, Snapshot> Last = new();

	public static RouterThroughput Sample(IReadOnlyList<Device> devices, ILogger logger)
	{
		var now = DateTime.UtcNow;

		string tunnelIface = null;
		if (Directory.Exists("/sys/class/net/tun1")) tunnelIface = "tun1";
		else if (Directory.Exists("/sys/class/net/ifb0")) tunnelIface = "ifb0";

		var ifaces = new List<string>();
		if (tunnelIface != null) ifaces.Add(tunnelIface);
		foreach (var d in devices)
			if (!string.IsNullOrWhiteSpace(d.Iface))
				ifaces.Add(d.Iface);

		double? tunnelKbps = null;
		var perModem = new Dictionary<string, double>(StringComparer.Ordinal);

		foreach (var iface in ifaces)
		{
			var snap = TryReadSnapshot(iface, now);
			if (snap == null)
			{
				Last.TryRemove(iface, out _);
				continue;
			}

			if (Last.TryGetValue(iface, out var prev))
			{
				double dt = (snap.Ts - prev.Ts).TotalSeconds;
				if (dt > 0)
				{
					long deltaBytes = (snap.RxBytes - prev.RxBytes) + (snap.TxBytes - prev.TxBytes);
					if (deltaBytes < 0) deltaBytes = 0;
					double kbps = deltaBytes * 8.0 / 1000.0 / dt;
					if (iface == tunnelIface) tunnelKbps = kbps;
					else perModem[iface] = kbps;
				}
			}
			Last[iface] = snap;
		}

		logger.LogInformation($"throughput tunnel={tunnelIface ?? "none"}={FormatMbps(tunnelKbps)} modems=[{string.Join(", ", FormatPerModem(perModem))}]");
		return new RouterThroughput(tunnelIface, tunnelKbps, perModem);
	}

	private static string FormatMbps(double? kbps) => kbps.HasValue ? $"{kbps.Value / 1000.0:F2}Mbps" : "n/a";

	private static Snapshot TryReadSnapshot(string iface, DateTime now)
	{
		try
		{
			string rxPath = $"/sys/class/net/{iface}/statistics/rx_bytes";
			string txPath = $"/sys/class/net/{iface}/statistics/tx_bytes";
			if (!File.Exists(rxPath) || !File.Exists(txPath)) return null;
			if (long.TryParse(File.ReadAllText(rxPath).Trim(), out long rx) &&
				long.TryParse(File.ReadAllText(txPath).Trim(), out long tx))
				return new Snapshot(rx, tx, now);
		}
		catch { }
		return null;
	}

	private static IEnumerable<string> FormatPerModem(Dictionary<string, double> perModem)
	{
		foreach (var kv in perModem)
			yield return $"{kv.Key}:{kv.Value / 1000.0:F2}Mbps";
	}
}
