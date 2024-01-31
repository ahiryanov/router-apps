using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

using static System.Net.WebRequestMethods;
using File = System.IO.File;

namespace persist_config;

internal class Program
{
    private static List<Config> _configList = new();
    private static string _configListPath = "/etc/config_list";

    static void Main(string[] args)
    {
        if (ReadConfigList(_configListPath))
        {
            //just print all find files
            foreach (var config in _configList)
            {
                Console.WriteLine($"Found file {config.FullPath} Exist: {File.Exists(config.FullPath)}");
                if (!File.Exists(config.FullPath))
                    Console.WriteLine($"Error!!! File {config.FullPath} not found!");
            }

            //processing find files
             foreach (var config in _configList)
             {
                 if (config.FileName == "hostname")
                 {
                     if (File.Exists($"/configs{config.FullPath}"))
                     {
                         string hostname = File.ReadAllText($"/configs{config.FullPath}");
                         File.WriteAllText(config.FullPath, hostname);
                         File.WriteAllText("/proc/sys/kernel/hostname", hostname);
                     }
                     Directory.CreateDirectory($"/configs{config.Directory}");
                     if (File.Exists($"/configs{config.FullPath}"))
                         File.Delete($"/configs{config.FullPath}");
                     File.Copy(config.FullPath, $"/configs{config.FullPath}");
                     continue;
 
                 }

                 if (!IsSymlink(config.FullPath))
                 {
                     Directory.CreateDirectory($"/configs{config.Directory}");
                     File.Copy(config.FullPath, $"/configs{config.FullPath}");
                 }

                 if (File.Exists($"/configs{config.FullPath}"))
                 {
                     if (File.Exists(config.FullPath))
                         File.Delete(config.FullPath);
                     File.CreateSymbolicLink(config.FullPath, $"/configs{config.FullPath}");
                 }
            }
        }
        else
        {
            Console.WriteLine("Error reading config_list file!");
        }

    }

    private static bool IsSymlink(string path)
    {
        FileInfo pathInfo = new FileInfo(path);
        return pathInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool ReadConfigList(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                List<Config> configs = new();
                foreach (var line in File.ReadLines(path))
                {
                    if (Directory.Exists(Path.GetDirectoryName(line)))
                        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(line)!, Path.GetFileName(line)))
                        {
                            configs.Add(new()
                            {
                                FileName = Path.GetFileName(file),
                                FullPath = file,
                                Directory = Path.GetDirectoryName(file)
                            });
                        }

                    if (Directory.Exists($"/configs{Path.GetDirectoryName(line)}"))
                        foreach (var file in Directory.GetFiles($"/configs{Path.GetDirectoryName(line)}"!,
                                     Path.GetFileName(line)))
                        {
                            configs.Add(new()
                            {
                                FileName = Path.GetFileName(file),
                                FullPath = file.Remove(0, 8), 
                                Directory = Path.GetDirectoryName(file)!.Remove(0, 8)
                            });
                        }
                }

                _configList = configs.GroupBy(c => c.FullPath).Select(g => g.First()).ToList();

            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception {e.Message}");
            }


            if (_configList.Any())
                return true;
        }
        else
        {
            Console.WriteLine("Missing path to config list");
        }

        return false;
    }
}

class Config
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
    public string Directory { get; set; }
}