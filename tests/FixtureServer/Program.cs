// Disposable test process: never installs or runs a game.
using var stopped = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shutdown.requested"), "graceful"); stopped.Set(); };
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "server.ready"), "ready");
stopped.Wait(TimeSpan.FromMinutes(2));
