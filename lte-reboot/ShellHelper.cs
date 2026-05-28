using System.Diagnostics;

namespace lte_reboot;

public static class ShellHelper
{
	public static string Bash(this string cmd)
	{
		string escapedArgs = cmd.Replace("\"", "\\\"");

		Process process = new Process()
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "/bin/bash",
				Arguments = $"-c \"{escapedArgs} 2>&1\"",
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			}
		};

		process.Start();
		// Read async so a hung qmicli/mbimcli on a broken modem can't block us
		// in ReadToEnd; the timeout below then actually fires.
		var readTask = process.StandardOutput.ReadToEndAsync();
		if (!process.WaitForExit(15000))
		{
			try { process.Kill(true); } catch { /* already gone */ }
			return string.Empty;
		}

		return readTask.GetAwaiter().GetResult().Trim();
	}
}
