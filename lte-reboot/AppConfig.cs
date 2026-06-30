using System.IO;

namespace lte_reboot;

internal static class AppConfig
{
	internal static string Srv;
	internal const int MaxRtt = 200;
	internal const int MaxLoss = 25;
	internal const int RestartCount = 30;
	internal const int CooldownCycles = 5;
	internal const int SimRebootThreshold = 7;
	internal static int LowDeviceThreshold = 6;
	internal const int LowDeviceResetCycles = 2;
	internal static readonly bool IsPingIputils = "ping -V".Bash().Contains("iputils");

	internal static void DetectSrv()
	{
		if (File.Exists("/etc/openvpn/client/client.conf"))
			Srv = "cat /etc/openvpn/client/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash();
	}

	internal static void ApplyArgs(string[] args)
	{
		if (args.Length > 0 && int.TryParse(args[0], out var lowDeviceThreshold))
			LowDeviceThreshold = lowDeviceThreshold;
	}
}
