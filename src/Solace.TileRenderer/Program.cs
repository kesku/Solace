using CommandLine;
using Npgsql;
using Serilog;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solace.EventBus.Client;
using Solace.StaticData;
using System.Globalization;

namespace Solace.TileRenderer;

internal static class Program
{
    // exit code for failures that restarting the renderer can't fix (e.g. an invalid maptiler api key), the launcher doesn't restart it after this
    // keep in sync with Solace.LauncherUI.Programs.TileRenderer.ConfigurationErrorExitCode
    private const int ConfigurationErrorExitCode = 2;

    private const int DefaultMaxZoom = 15;

    // delays between the attempts to reach maptiler at startup, after the last one the renderer starts anyway
    private static readonly TimeSpan[] MaptilerVerifyRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private sealed class Options
    {
        [Option("maptiler_key", HelpText = "Api key for maptiler.com", SetName = "Data source")]
        public string? MapTilerApiKey { get; set; }

        [Option("tileDB", HelpText = "Connection string to a postgresql database with tile data, for example 'Host=myserver;Username=mylogin;Password=mypass;Database=mydatabase'", SetName = "Data source")]
        public string? TileDatabaseConnectionString { get; set; }

        [Option("eventbus", Default = "localhost:5532", Required = false, HelpText = "Event bus address")]
        public string? EventBusConnectionString { get; set; }

        [Option("dir", Default = "./staticdata", Required = false, HelpText = "Static data path")]
        public string? StaticDataPath { get; set; }

        [Option("logger-url", Default = null, Required = false, HelpText = "Url to send logs to")]
        public string? LoggerUrl { get; set; }
    }

    private static async Task<int> Main(string[] args)
    {
        if (!Debugger.IsAttached)
        {
            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Log.Fatal($"Unhandled exception: {e.ExceptionObject}");
                Log.CloseAndFlush();
                Environment.Exit(1);
            };
        }

        ParserResult<Options> res = Parser.Default.ParseArguments<Options>(args);

        Options options;
        if (res is Parsed<Options> parsed)
        {
            options = parsed.Value;
        }
        else
        {
            return res is NotParsed<Options> notParsed
                ? res.Errors.Any(error => error is HelpRequestedError)
                    ? 0
                    : res.Errors.Any(error => error is VersionRequestedError)
                    ? 0
                    : 1
                : 1;
        }

        var loggerConfig = new LoggerConfiguration()
                 .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                 .WriteTo.File("logs/tile_renderer/log.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}", formatProvider: CultureInfo.InvariantCulture)
                 .Enrich.WithProperty("ComponentName", "TileRenderer");

        if (!string.IsNullOrWhiteSpace(options.LoggerUrl))
        {
            loggerConfig.WriteTo.Http(options.LoggerUrl, 10 * 1024 * 1024);
        }

        loggerConfig.MinimumLevel.Debug();
        var log = loggerConfig.CreateLogger();

        Log.Logger = log;

        if (string.IsNullOrEmpty(options.MapTilerApiKey) && string.IsNullOrEmpty(options.TileDatabaseConnectionString))
        {
            Log.Fatal("No data source provided, either maptiler_key or tileDB must be specified.");
            Log.CloseAndFlush();
            return ConfigurationErrorExitCode;
        }

        ITileDataSource tileDataSource;
        if (options.MapTilerApiKey is not null)
        {
            Log.Information("Verifying maptiler api key");

            var httpClient = new HttpClient() { Timeout = MaptilerTileDataSource.RequestTimeout };
            int? maxZoom = await VerifyMaptilerKeyAsync(httpClient, options.MapTilerApiKey);
            if (maxZoom is null)
            {
                httpClient.Dispose();

                Log.CloseAndFlush();
                return ConfigurationErrorExitCode;
            }

            tileDataSource = new MaptilerTileDataSource(options.MapTilerApiKey, maxZoom.Value, httpClient);
        }
        else
        {
            Debug.Assert(options.TileDatabaseConnectionString is not null);

            Log.Information("Connecting to tile database");
            try
            {
                tileDataSource = new DatabaseTileDataSource(NpgsqlDataSource.Create(options.TileDatabaseConnectionString));
            }
            catch (Exception ex)
            {
                Log.Fatal($"Could not connect to tile database: {ex}");
                if (ex is ArgumentException)
                {
                    Log.Information($"The provided connection string is: '{options.TileDatabaseConnectionString}', make sure that it is in the correct format");
                }

                Log.CloseAndFlush();
                return ConfigurationErrorExitCode;
            }

            Log.Information("Connected to tile database");
        }

        Log.Information("Connecting to event bus");
        EventBusClient eventBusClient;
        try
        {
            eventBusClient = await EventBusClient.ConnectAsync(options.EventBusConnectionString ?? "");
        }
        catch (EventBusClientException ex)
        {
            tileDataSource.Dispose();

            Log.Fatal($"Could not connect to event bus: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Connected to event bus");

        Log.Information("Loading static data");
        StaticData.StaticData staticData;
        try
        {
            staticData = new StaticData.StaticData(options.StaticDataPath ?? "");
        }
        catch (StaticDataException staticDataException)
        {
            tileDataSource.Dispose();

            Log.Fatal($"Failed to load static data: {staticDataException}");
            Log.CloseAndFlush();
            return ConfigurationErrorExitCode;
        }

        Log.Information("Loaded static data");

        await using (var renderer = new EventBusTileRenderer(tileDataSource, eventBusClient, staticData))
        {
            await renderer.RunAsync();
        }

        return 0;
    }

    // returns the max zoom of the maptiler tiles, or null if maptiler rejected the api key
    // a temporary failure (network error, timeout, 5xx, 408/429) is retried with backoff and after that the renderer starts anyway,
    // so a maptiler outage doesn't take it down, the tile requests are retried in the same way once it is running
    private static async Task<int?> VerifyMaptilerKeyAsync(HttpClient httpClient, string apiKey)
    {
        for (int attempt = 0; ; attempt++)
        {
            string failure;
            try
            {
                using var response = await httpClient.GetAsync($"https://api.maptiler.com/tiles/v3/tiles.json?key={apiKey}");

                if (response.IsSuccessStatusCode)
                {
                    Log.Information("Verified maptiler api key");
                    return await ReadMaxZoomAsync(response);
                }

                if (!MaptilerTileDataSource.IsTransientFailure(response.StatusCode))
                {
                    Log.Fatal($"Maptiler api key not valid, response status code: {response.StatusCode}");
                    return null;
                }

                failure = $"response status code: {response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failure = ex.Message;
            }

            if (attempt >= MaptilerVerifyRetryDelays.Length)
            {
                Log.Warning($"Could not reach maptiler api ({failure}), starting anyway, tiles will render once it is reachable again");
                return DefaultMaxZoom;
            }

            var delay = MaptilerVerifyRetryDelays[attempt];
            Log.Warning($"Could not reach maptiler api ({failure}), retrying in {delay.TotalSeconds} seconds");
            await Task.Delay(delay);
        }
    }

    private static async Task<int> ReadMaxZoomAsync(HttpResponseMessage response)
    {
        JsonObject? json;
        try
        {
            json = await JsonSerializer.DeserializeAsync<JsonObject>(await response.Content.ReadAsStreamAsync());
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null || !json.TryGetPropertyValue("maxzoom", out JsonNode? maxZoomNode) || maxZoomNode is not JsonValue maxZoomValue || maxZoomValue.GetValueKind() != JsonValueKind.Number)
        {
            Log.Warning("Invalid maptiler response");
            return DefaultMaxZoom;
        }

        return maxZoomValue.GetValue<int>();
    }
}