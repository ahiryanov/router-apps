using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace qrename
{
    public class Program
    {
        static void Main(string[] args)
        {
            string path = $"/usr/share/{args[1]}";
            int num = Convert.ToInt32(Regex.Match(args[0], @"\d+").Value);
            Thread.Sleep(num * 1000);
            File.WriteAllText($"/tmp/exist-{args[1]}-{File.Exists(path)}","");
            if (File.Exists(path) && File.ReadAllText(path).Contains("qmi") && !File.Exists($"/dev/{File.ReadLines(path)}"))
            {
                File.WriteAllText($"/tmp/udev-fromfile-{num}", $"{args[0]} {args[1]}");
                Console.Out.WriteLine(File.ReadAllText(path));
                return;
            }
            else
            {
                if (!File.Exists($"/dev/qmi{num}"))
                {
                    File.WriteAllText($"/tmp/udev-defaultnum-{num}", $"{args[0]} {args[1]}");
                    File.WriteAllText(path, $"qmi{num}");
                    Console.Out.WriteLine($"qmi{num}");
                    return;
                }
                else
                {
                    File.WriteAllText($"/tmp/udev-count-{num}", $"{args[0]} {args[1]}");
                    var qmis = Directory.GetFileSystemEntries("/dev").Where(t => t.Contains("qmi")).Select(t => t.Substring(t.LastIndexOf("/") + 1)).OrderBy(s => s);
                    for (int i = 0; i < qmis.Count() + 1; i++)
                        if (!File.Exists($"/dev/qmi{i}"))
                        {
                            File.WriteAllText(path, $"qmi{i}");
                            Console.Out.WriteLine($"qmi{i}");
                            return;
                        }
                }
            }
        }
    }
}
