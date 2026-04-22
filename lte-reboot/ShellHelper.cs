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
		string result = process.StandardOutput.ReadToEnd();
		process.WaitForExit();

		return result.Trim();
	}
}
