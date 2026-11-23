using MCEPatcher.Core;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().MinimumLevel.Debug().CreateLogger();

// Same as the UI's iOS advanced mode with a custom login server in single-domain mode.
const string host = "earth.home.arpa:8080";
var options = new IpaProcessor.Options
{
    Autonomous = true,
    InIpa = args[0],
    OutIpa = args[1],
    DecodedDir = args[2],
    Protocol = 0,
    Hostname = host,
    LocatorProtocol = 0,
    LocatorHostname = host,
    AppName = "Solace",
    ChangeLocatorAddress = true,
    ChangeXalAuthAddress = true,
    ForceInAppWebView = true,
    ForceInteractiveSignIn = true,
    ChangePlayfabApiAddress = true,
    ChangeXboxABAddress = true,
    ChangeXboxLiveAddress = true,
    DisableSunsetTimeCheck = true,
    DisableSpeedLimit = false,
    ChangeAppName = true,
    RemoveDrm = true,
};

bool ok = await IpaProcessor.Run(options);
Log.CloseAndFlush();
return ok ? 0 : 1;
