using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal record SubflowMetrics(double RttMs, double RttVar)
{
    public double StabilityRatio => RttMs > 0 ? RttVar / RttMs : 1.0;
}

internal static class MptcpManager
{
	private record Endpoint(int Id, string Ip, string Dev, bool IsBackup, IReadOnlyList<string> Flags, string RawTail);

	public static void EnsureActiveSubflow(Device device, string endpoint, string endpointId, bool isBackup, ILogger logger)
	{
		var realModemIp = GetRealModemIp(device.Iface);
		if (isBackup)
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
	}

	public static void CheckMultipleEndpoints(ILogger logger)
	{
		var endpoints = "ip mptcp endpoint".Bash();
		var lines = endpoints
			.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(s => s.Trim())
			.ToList();
		var rx = new Regex(@"\bid\s+(\d+).*?\bdev\s+(\S+)", RegexOptions.IgnoreCase);
		var parsed = new List<(int Id, string Dev, string Line)>();
		foreach (var line in lines)
		{
			var m = rx.Match(line);
			if (!m.Success) continue;
			int id = int.Parse(m.Groups[1].Value);
			string dev = m.Groups[2].Value;
			parsed.Add((id, dev, line));
		}
		var groups = parsed.GroupBy(p => p.Dev, StringComparer.Ordinal);
		foreach (var g in groups)
		{
			if (g.Key.StartsWith("if"))
			{
				logger.LogError($"Remove orphaned endpoints dev: {g.Key}");
				foreach (var e in g)
					$"ip mptcp endpoint delete id {e.Id}".Bash();
			}

			if (g.Count() <= 1) continue;

			int maxId = g.Max(x => x.Id);
			logger.LogError($"{g.Key}: has {g.Count()} endpoints. Leave id={maxId}, delete rest.");

			foreach (var e in g.Where(x => x.Id != maxId).OrderBy(x => x.Id))
				$"ip mptcp endpoint delete id {e.Id}".Bash();
		}
	}

	public static void RemoveDeadEndpoint(string iface, ILogger logger)
	{
		var endpoint = $"ip mptcp endpoint | grep {iface}".Bash();
		var rx = new Regex(@"\bid\s+(\d+).*?\bdev\s+(\S+)", RegexOptions.IgnoreCase);
		var m = rx.Match(endpoint);
		if (m.Success)
		{
			var id = m.Groups[1].Value;
			var dev = m.Groups[2].Value;
			$"ip mptcp endpoint delete id {id}".Bash();
			logger.LogWarning($"Remove dead endpoint {dev} id {id}");
		}
	}

	public static Dictionary<string, SubflowMetrics> GetSubflowMetrics(string srv)
	{
		var ssOut = $"ss -MntiH state established dst {srv}".Bash();
		var result = new Dictionary<string, SubflowMetrics>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(ssOut)) return result;

		var lines = ssOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < lines.Length; i++)
		{
			var line = lines[i];
			if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0])) continue;
			if (line.TrimStart().StartsWith("mptcp", StringComparison.OrdinalIgnoreCase)) continue;

			var localMatch = Regex.Match(line, @"\d+\s+\d+\s+(?<local>\S+:\d+)\s+");
			if (!localMatch.Success) continue;

			string localIp = TrimToIp(localMatch.Groups["local"].Value);
			if (string.IsNullOrEmpty(localIp)) continue;

			if (i + 1 >= lines.Length) continue;
			var infoLine = lines[i + 1];
			if (string.IsNullOrEmpty(infoLine) || !char.IsWhiteSpace(infoLine[0])) continue;

			var rttMatch = Regex.Match(infoLine, @"\brtt:(?<rtt>\d+(?:\.\d+)?)/(?<var>\d+(?:\.\d+)?)");
			if (!rttMatch.Success) continue;

			if (double.TryParse(rttMatch.Groups["rtt"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rtt) &&
				double.TryParse(rttMatch.Groups["var"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rttVar))
			{
				result[localIp] = new SubflowMetrics(rtt, rttVar);
			}
		}
		return result;
	}

	public static void RecreateDeadSubflow(ILogger logger, Dictionary<string, SubflowMetrics> subflows)
	{
		var endpointsRaw = "ip mptcp endpoint".Bash();
		var endpoints = ParseEndpoints(endpointsRaw);
		var activeEndpoints = endpoints.Where(e => !e.IsBackup).ToList();

		foreach (var ep in activeEndpoints)
		{
			bool inUse = subflows.ContainsKey(ep.Ip);
			if (inUse) continue;
			var realModemIp = GetRealModemIp(ep.Dev);
			logger.LogError($"Endpoint id={ep.Id} ip={ep.Ip} realIP={realModemIp} dev={ep.Dev} flags=[{string.Join(' ', ep.Flags)}] -> {(inUse ? "in use" : " NOT in use")}");
			$"ip mptcp endpoint delete id {ep.Id}".Bash();
			Thread.Sleep(500);
			$"ip mptcp endpoint add {realModemIp} dev {ep.Dev} subflow laminar".Bash();
		}
	}

	public static string GetRealModemIp(string iface)
	{
		var json = $"ip -j address show dev {iface}".Bash();
		using var doc = JsonDocument.Parse(json);

		if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
			return null;

		var ifaceN = doc.RootElement[0];

		if (!ifaceN.TryGetProperty("addr_info", out var addrInfo) || addrInfo.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var item in addrInfo.EnumerateArray())
		{
			if (item.TryGetProperty("family", out var fam) &&
				fam.ValueKind == JsonValueKind.String &&
				fam.GetString() == "inet" &&
				item.TryGetProperty("local", out var localProp) &&
				localProp.ValueKind == JsonValueKind.String)
			{
				return localProp.GetString();
			}
		}
		return null;
	}

	private static List<Endpoint> ParseEndpoints(string text)
	{
		var list = new List<Endpoint>();
		if (string.IsNullOrWhiteSpace(text)) return list;

		var lineRx = new Regex(@"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+id\s+(?<id>\d+)\s*(?<tail>.*)$",
							   RegexOptions.IgnoreCase | RegexOptions.Multiline);

		foreach (Match m in lineRx.Matches(text))
		{
			string ip = m.Groups["ip"].Value;
			int id = int.Parse(m.Groups["id"].Value);
			string tail = (m.Groups["tail"].Value ?? "").Trim();

			var devRx = new Regex(@"\bdev\s+(?<dev>\S+)", RegexOptions.IgnoreCase);
			var devMatch = devRx.Match(tail);
			if (!devMatch.Success) continue;
			string dev = devMatch.Groups["dev"].Value;

			string flagsPart = tail;
			int devIdx = flagsPart.IndexOf(devMatch.Value, StringComparison.OrdinalIgnoreCase);
			if (devIdx >= 0) flagsPart = flagsPart.Substring(0, devIdx).Trim();
			var flags = flagsPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
								 .Select(f => f.ToLowerInvariant())
								 .ToList();

			bool isBackup = flags.Contains("backup");
			list.Add(new Endpoint(id, ip, dev, isBackup, flags, tail));
		}
		return list;
	}

private static string TrimToIp(string endpoint)
	{
		int pct = endpoint.IndexOf('%');
		int colon = endpoint.IndexOf(':');
		int cut = -1;
		if (pct >= 0 && colon >= 0) cut = Math.Min(pct, colon);
		else if (pct >= 0) cut = pct;
		else if (colon >= 0) cut = colon;
		return (cut >= 0 ? endpoint.Substring(0, cut) : endpoint).Trim();
	}
}
