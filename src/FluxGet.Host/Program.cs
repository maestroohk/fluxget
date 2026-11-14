using FluxGet.Host;

var app = FluxGetServer.Build(args);
var options = app.Services.GetRequiredService<FluxGetHostOptions>();
if (options.Mode == HostMode.Local && Environment.UserInteractive)
{
    var key = app.Services.GetRequiredService<AccessKeys>().SessionKey;
    Console.WriteLine($"FluxGet is running at http://127.0.0.1:{options.Port}/");
    Console.WriteLine($"Access key (keep private): {key}");
}
app.Run();

public partial class Program;
