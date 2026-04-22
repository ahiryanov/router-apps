using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;

internal static class MptcpManager
{
	private record Endpoint(int Id, string Ip, string Dev, bool IsBackup, IReadOnlyList<string> Flags, string RawTail);

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

	public static void RecreateDeadSubflow(ILogger logger)
	{
		var endpointsRaw = "ip mptcp endpoint".Bash();
		var endpoints = ParseEndpoints(endpointsRaw);
		var activeEndpoints = endpoints.Where(e => !e.IsBackup).ToList();
		var localIpInUse = GetSsIp(AppConfig.Srv);

		foreach (var ep in activeEndpoints)
		{
			bool inUse = localIpInUse.Contains(ep.Ip);
			if (inUse) continue;
			var realModemIp = GetRealModemIp(ep.Dev);
			logger.LogError($"Endpoint id={ep.Id} ip={ep.Ip} realIP={realModemIp} dev={ep.Dev} flags=[{string.Join(' ', ep.Flags)}] -> {(inUse ? "in use" : " NOT in use")}");
			$"ip mptcp endpoint delete id {ep.Id}".Bash();
			Thread.Sleep(500);
			$"ip mptcp endpoint add {realModemIp} dev {ep.Dev} subflow laminar".Bash();
		}
	}

	public static object GetRealModemIp(string iface)
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

	private static HashSet<string> GetSsIp(string srv)
	{
		var ssOut = $"ss -tHn state established dst {srv}".Bash();
		var set = new HashSet<string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(ssOut)) return set;
		var rx = new Regex(@"^\s*\d+\s+\d+\s+(?<local>\S+)\s+(?<peer>\S+)", RegexOptions.Multiline);

		foreach (Match m in rx.Matches(ssOut))
		{
			string local = m.Groups["local"].Value;
			string ip = TrimToIp(local);
			if (!string.IsNullOrEmpty(ip))
				set.Add(ip);
		}
		return set;
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
