namespace lte_reboot;

internal static class AppConfig
{
	internal static string Srv;
	internal static int MaxRtt = 300;
	internal static int MaxLoss = 25;
	internal const int RestartCount = 20;
	internal const string LogFile = "/tmp/lte";
}
