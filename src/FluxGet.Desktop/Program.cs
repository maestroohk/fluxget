using System.Net.Http.Json;
using FluxGet.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace FluxGet.Desktop;

internal static class Program
{
    public static int Port { get; private set; } = 17890;
    public static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public static long StartupMs { get; set; } = -1;
    public static string[]? SelfTest { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                Notifier.Uninstall();
                Protocol.Unregister();
            })
            .Run();
        var selfTestAt = Array.IndexOf(args, "--player-selftest");
        if (selfTestAt >= 0 && args.Length > selfTestAt + 2)
        {
            SelfTest = [args[selfTestAt + 1], args[selfTestAt + 2]];
            Port = 17899;
        }
        using var mutex = new Mutex(true, SelfTest is null ? @"Local\FluxGet.Desktop" : @"Local\FluxGet.SelfTest", out var first);
        var argument = SelfTest is null ? args.FirstOrDefault() : null;
        if (!first)
        {
            ForwardToRunningInstance(argument);
            return;
        }

        ApplicationConfiguration.Initialize();
        _ = Task.Run(Protocol.Register);

        WebApplication server;
        Task started;
        try
        {
            server = FluxGetServer.Build(args.Where(a => a.StartsWith("--")).ToArray(), o =>
            {
                o.Mode = HostMode.Desktop;
                o.Port = Port;
                if (SelfTest is not null) o.DataDirectory = Path.Combine(SelfTest[1], "data");
            });
            started = server.StartAsync();
        }
        catch (Exception e)
        {
            MessageBox.Show($"FluxGet could not start its download service on port {Port}.\n\n{e.Message}", "FluxGet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var form = new MainForm(server, argument, started);
        Application.Run(form);
        server.StopAsync().GetAwaiter().GetResult();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void ForwardToRunningInstance(string? argument)
    {
        try
        {
            var keyFile = Path.Combine(new FluxGetHostOptions().ResolveDataDirectory(), "session.key");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", File.ReadAllText(keyFile).Trim());
            http.PostAsJsonAsync("/api/app/activate", new ActivateBody(argument)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }
}

internal static class Protocol
{
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\fluxget");
            key.SetValue("", "URL:FluxGet");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception)
        {
        }
    }

    public static void Unregister()
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\fluxget", false); }
        catch (Exception) { }
    }
}
