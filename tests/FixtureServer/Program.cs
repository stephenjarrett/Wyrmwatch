// Disposable test process: never installs or runs a game.
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using var stopped = new ManualResetEventSlim();
void Stop() { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shutdown.requested"), "graceful"); stopped.Set(); }
if (OperatingSystem.IsWindows()) Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop(); };
using var signal = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; Stop(); }) : null;
var port = int.Parse(args.Single(arg => arg.StartsWith("-Port=", StringComparison.Ordinal))[6..]);
using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "server.ready"), "ready");
stopped.Wait(TimeSpan.FromMinutes(2));
