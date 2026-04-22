using System.Text.RegularExpressions;

namespace lte_reboot;

internal static class ModemInfo
{
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
