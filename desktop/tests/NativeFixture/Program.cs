using System.Diagnostics;
using System.Text;

// 仅供显式受控原生验收；没有窗口、子进程或任何外部动作。
if (args.Length != 2) return 2;
var allocation = new byte[16 * 1024 * 1024];
for (var index = 0; index < allocation.Length; index += 4096) allocation[index] = 1;
using var self = Process.GetCurrentProcess();
self.PriorityClass = ProcessPriorityClass.Normal;
File.WriteAllText(args[0] + ".tmp", Environment.ProcessId + ":" + args[1], Encoding.UTF8);
File.Move(args[0] + ".tmp", args[0]);
using var lifetime = new ManualResetEventSlim(false);
lifetime.Wait(TimeSpan.FromMinutes(2));
GC.KeepAlive(allocation);
return 0;
