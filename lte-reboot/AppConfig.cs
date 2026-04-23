using System.IO;

namespace lte_reboot;

internal static class AppConfig
{
	internal static string Srv;
	internal static int MaxRtt = 300;
	internal static int MaxLoss = 25;
	internal const int RestartCount = 30;
	internal const int CooldownCycles = 5;
	internal const string LogFile = "/tmp/lte";

	internal static void DetectSrv()
	{
		if (File.Exists("/etc/openvpn/client/client.conf"))
			Srv = "cat /etc/openvpn/client/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash();
	}

	internal static void ApplyArgs(string[] args)
	{
		if (args.Length == 2)
		{
			int.TryParse(args[0], out var maxLoss);
			int.TryParse(args[1], out var maxRtt);
			MaxLoss = maxLoss;
			MaxRtt = maxRtt;
		}
	}
}
