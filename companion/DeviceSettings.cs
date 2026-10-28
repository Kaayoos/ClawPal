using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClawPal.Companion;

public sealed record DisplayApp(int Id, string Name, string Description, string Mascot);
public static class DisplayApps
{
    public static DisplayApp[] All => [
        new(0,L.Text("pc_monitor"),L.Text("live_pc_readings_and_optional_game_fps"),"working"),
        new(1,L.Text("clock"),L.Text("local_time_with_an_optional_little_forecast"),"sleeping"),
        new(2,L.Text("spotify"),L.Text("playback_track_names_and_music_controls"),"music"),
        new(3,L.Text("timer"),L.Text("an_adjustable_countdown_for_anything"),"thinking"),
        new(4,L.Text("codex"),L.Text("account_limits_and_recorded_task_activity"),"typing"),
        new(7,L.Text("claude_code"),L.Text("claude_description"),"typing"),
        new(6,L.Text("weather"),L.Text("current_conditions_and_a_crab_dressed_for_the_weather"),"sunny") ];
    public static DisplayApp Get(int id) => All.Single(a => a.Id == id);
}

public sealed record DeviceSettings
{
    public const int CurrentSchema=6;
    public int Schema { get; init; } = CurrentSchema;
    public string Language { get; init; } = "en";
    public int EnabledApps { get; init; } = 255;
    public int[] HomeOrder { get; init; } = [0,4,7,2,1];
    [JsonPropertyName("use_24h")] public bool Use24h { get; init; } = true;
    public bool WeatherEnabled { get; init; } = true;
    public bool WeatherFahrenheit { get; init; }
    public string City { get; init; } = "Weather";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Timezone { get; init; } = "UTC0";
    public int DimBehavior { get; init; } = 1; // 0 never, 1 PC offline, 2 always
    public int DimAfterSeconds { get; init; } = 60;
    public int DimBrightness { get; init; } = 40;
    public bool NightEnabled { get; init; } = true;
    public int NightStart { get; init; } = 1;
    public int NightEnd { get; init; } = 10;
    public int OfflineApp { get; init; } = 1;
    public DeviceSettings Upgrade() {
        var value=Schema==2 ? this with {EnabledApps=EnabledApps|64,Language="en",WeatherFahrenheit=false} : Schema==3 ? this with {Language="en",WeatherFahrenheit=false} : Schema==4 ? this with {WeatherFahrenheit=false} : this;
        return Schema is >=2 and <6 ? value with {Schema=CurrentSchema,EnabledApps=value.EnabledApps|128} : value;
    }
    public bool Enabled(int app) => app is >= 0 and <= 7 && (EnabledApps & (1 << app)) != 0;
    public DeviceSettings SetEnabled(int app, bool enabled)
    {
        if (app is < 0 or > 7 or 5) throw new ArgumentOutOfRangeException(nameof(app));
        int mask = enabled ? EnabledApps | (1 << app) : EnabledApps & ~(1 << app);
        return this with { EnabledApps = mask, OfflineApp = (mask & (1 << OfflineApp))!=0 ? OfflineApp : (mask & 2)!=0 ? 1 : 5, HomeOrder = HomeOrder.Where(id => (mask & (1 << id)) != 0).ToArray() };
    }
    public DeviceSettings Move(int from, int to)
    {
        if (from < 0 || from >= HomeOrder.Length || to < 0 || to >= HomeOrder.Length) return this;
        var order=HomeOrder.ToList(); int item=order[from];order.RemoveAt(from);order.Insert(to,item);
        return this with { HomeOrder=order.ToArray() };
    }
    public void Validate()
    {
        if(!L.Supports(Language))throw new ArgumentException(L.Text("unsupported_language"));
        if (Schema != CurrentSchema || (EnabledApps & 32)==0 || (EnabledApps & ~255)!=0) throw new ArgumentException(L.Text("home_must_stay_enabled"));
        if (HomeOrder is null || HomeOrder.Length > 5 || HomeOrder.Distinct().Count() != HomeOrder.Length || HomeOrder.Any(id => id is < 0 or > 7 or 5 || !Enabled(id)))
            throw new ArgumentException(L.Text("home_shortcuts_must_be_unique_enabled_apps"));
        if (DimBehavior is < 0 or > 2 || DimAfterSeconds is < 15 or > 3600 || DimBrightness is < 5 or > 90)
            throw new ArgumentException(L.Text("choose_a_dimming_delay_from_15_seconds_to_60"));
        if (NightStart is < 0 or > 23 || NightEnd is < 0 or > 23 || NightStart==NightEnd)
            throw new ArgumentException(L.Text("night_hours_must_have_different_start_and_end_times"));
        if (OfflineApp is < 0 or > 7) throw new ArgumentException(L.Text("choose_an_available_offline_app"));
        if (string.IsNullOrWhiteSpace(City) || Encoding.UTF8.GetByteCount(City) > 63 || City.Contains('\0')) throw new ArgumentException(L.Text("choose_a_location_with_a_name_shorter_than_64"));
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude) || Math.Abs(Latitude)>90 || Math.Abs(Longitude)>180) throw new ArgumentException(L.Text("choose_a_valid_weather_location"));
        if (!Regex.IsMatch(Timezone ?? "", "^[A-Za-z0-9+,.:/<>-]{3,127}$")) throw new ArgumentException(L.Text("choose_a_supported_time_zone"));
    }
    internal static DeviceSettings FromConfiguration(Configuration config) => new() {
        City=string.IsNullOrWhiteSpace(config.City)?"Weather":config.City,
        Latitude=config.Latitude, Longitude=config.Longitude, Timezone=config.Timezone
    };
}

public sealed record DeviceSettingsSnapshot(DeviceSettings Settings, string Version, string WifiSsid, string Model, int ProtocolSchema = DeviceSettings.CurrentSchema);

public sealed class DeviceSettingsClient(Configuration config)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive=true };
    internal static ByteArrayContent JsonBody<T>(T value)
    {
        // ESP-IDF's HTTP server does not decode chunked request bodies.
        var body=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value,Json));
        body.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        return body;
    }
    // Newer firmware asks for a tap on the display before firmware or Wi-Fi changes. Firmware without /confirm returns 404.
    internal static async Task ConfirmOnDisplayAsync(HttpClient client, Uri address, string action, Action waiting, CancellationToken token)
    {
        var deadline=DateTimeOffset.UtcNow.AddSeconds(55);bool notified=false;
        while(true){
            using(var body=JsonBody(new { action }))
            using(var response=await client.PostAsync(new Uri(address,"/confirm"),body,token)){
                if(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)return;
                response.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var state=json.RootElement.TryGetProperty("state",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
                if(state=="approved")return;
                if(state=="denied")throw new IOException(L.Text("the_change_was_declined_on_the_display"));
                if(state!="pending")throw new IOException(L.Text("the_change_was_not_confirmed_on_the_display_in_time"));
            }
            if(!notified){waiting();notified=true;}
            if(DateTimeOffset.UtcNow>=deadline)throw new IOException(L.Text("the_change_was_not_confirmed_on_the_display_in_time"));
            await Task.Delay(1500,token);
        }
    }
    internal static bool Equivalent(DeviceSettings a,DeviceSettings b) =>
        a.Schema==b.Schema && a.Language==b.Language && a.EnabledApps==b.EnabledApps && a.HomeOrder.SequenceEqual(b.HomeOrder) &&
        a.DimBehavior==b.DimBehavior && a.DimAfterSeconds==b.DimAfterSeconds && a.DimBrightness==b.DimBrightness &&
        a.NightEnabled==b.NightEnabled && a.NightStart==b.NightStart && a.NightEnd==b.NightEnd && a.OfflineApp==b.OfflineApp &&
        a.Use24h==b.Use24h && a.WeatherEnabled==b.WeatherEnabled && a.WeatherFahrenheit==b.WeatherFahrenheit && a.City==b.City && a.Timezone==b.Timezone &&
        Math.Abs(a.Latitude-b.Latitude)<0.0000001 && Math.Abs(a.Longitude-b.Longitude)<0.0000001;
    public static string FailureMessage(Exception error) => error switch {
        NotSupportedException => error.Message,
        HttpRequestException e when e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.LengthRequired => L.Text("the_display_rejected_the_settings_request_update_the_companion"),
        HttpRequestException e when e.StatusCode==HttpStatusCode.Unauthorized => L.Text("pairing_was_rejected_reopen_clawpal_with_the_device_s"),
        HttpRequestException e when e.StatusCode is not null => L.Text("display_http_error", (int)e.StatusCode.Value),
        ArgumentException => error.Message,
        _ => L.Text("saved_on_this_pc_waiting_for_the_display_to")
    };
    private readonly SemaphoreSlim gate = new(1,1);
    public bool Suspended { get; set; }
    private string statusText = L.Text("settings_stay_on_your_display");
    public string Status { get=>L.Retranslate(statusText);private set=>statusText=value; }
    public string FirmwareVersion { get; private set; } = "";
    public string WifiSsid { get; private set; } = "";
    public bool NeedsFirmwareUpdate { get; private set; }
    private async Task<(HttpClient Client, Uri Address)> Open(CancellationToken token)
    {
        var endpoint=await Connection.DiscoverAsync(config,token) ?? throw new IOException(L.Text("display_offline_changes_will_be_sent_when_it_reconnects"));
        var client=DeviceAuth.For(endpoint,config.Token).CreateClient(TimeSpan.FromSeconds(8));
        return (client,new Uri($"http://{endpoint.Address}:{Protocol.MonitorPort}/"));
    }
    internal static DeviceSettingsSnapshot Parse(string response)
    {
        using var json=JsonDocument.Parse(response);
        var settings=JsonSerializer.Deserialize<DeviceSettings>(response,Json) ?? throw new IOException(L.Text("the_display_returned_empty_settings"));
        if (!json.RootElement.TryGetProperty("schema",out var schema) || schema.GetInt32() is not (2 or 3 or 4 or 5 or 6)) throw new NotSupportedException(L.Text("update_the_display_firmware_to_use_wireless_configuration"));
        foreach(string key in new[]{"enabled_apps","home_order","use_24h","weather_enabled","city","latitude","longitude","timezone"})
            if(!json.RootElement.TryGetProperty(key,out _))throw new IOException(L.Text("the_display_returned_incomplete_settings"));
        if(schema.GetInt32()>=3)
            foreach(string key in new[]{"dim_behavior","dim_after_seconds","dim_brightness","night_enabled","night_start","night_end","offline_app"})
                if(!json.RootElement.TryGetProperty(key,out _))throw new IOException(L.Text("the_display_returned_incomplete_screen_settings"));
        if(schema.GetInt32()>=4 && !json.RootElement.TryGetProperty("language",out _))throw new IOException(L.Text("the_display_returned_incomplete_settings"));
        if(schema.GetInt32()>=5 && !json.RootElement.TryGetProperty("weather_fahrenheit",out _))throw new IOException(L.Text("the_display_returned_incomplete_settings"));
        settings=settings.Upgrade();settings.Validate();
        string Value(string key)=>json.RootElement.TryGetProperty(key,out var item) && item.ValueKind==JsonValueKind.String?item.GetString()??"":"";
        return new(settings,Value("version"),Value("wifi_ssid"),Value("model"),schema.GetInt32());
    }
    private async Task<DeviceSettingsSnapshot> Read(HttpClient client, Uri address, CancellationToken token)
    {
        using var response=await client.GetAsync(new Uri(address,"settings"),token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) throw new NotSupportedException(L.Text("a_one_time_display_firmware_update_is_needed_for"));
        response.EnsureSuccessStatusCode();
        var snapshot=Parse(await response.Content.ReadAsStringAsync(token));
        FirmwareVersion=snapshot.Version; WifiSsid=snapshot.WifiSsid; NeedsFirmwareUpdate=snapshot.ProtocolSchema<DeviceSettings.CurrentSchema;
        return snapshot;
    }
    public async Task<DeviceSettingsSnapshot> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { var (client,address)=await Open(token);using(client) return await Read(client,address,token); }
        catch(NotSupportedException) { NeedsFirmwareUpdate=true;throw; }
        finally { gate.Release(); }
    }
    public async Task SyncAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try {
            if (!config.SettingsPending || config.DisplaySettings is not { } settings) return;
            settings.Validate(); Status=L.Text("sending_settings_over_wi_fi");
            var (client,address)=await Open(token);
            using(client) {
                if((await Read(client,address,token)).ProtocolSchema<DeviceSettings.CurrentSchema) throw new NotSupportedException(L.Text("firmware_settings_update"));
                using var body=JsonBody(settings);
                using var response=await client.PostAsync(new Uri(address,"settings"),body,token);
                response.EnsureSuccessStatusCode();
                using var ack=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!ack.RootElement.TryGetProperty("saved",out var saved) || saved.ValueKind!=JsonValueKind.True) throw new IOException(L.Text("the_display_did_not_confirm_saving_settings"));
                var confirmed=await Read(client,address,token);
                if (!Equivalent(settings,confirmed.Settings)) throw new IOException(L.Text("the_display_settings_have_not_matched_yet_clawpal_will"));
            }
            config.ConfirmDisplaySettings(settings);
            Status=config.SettingsPending?L.Text("newer_changes_are_waiting_to_sync"):L.Text("saved_on_your_display_over_wi_fi");
        } catch(NotSupportedException e) { NeedsFirmwareUpdate=true;Status=e.Message;throw; }
        finally { gate.Release(); }
    }
    public async Task ChangeWifiAsync(string ssid, string password, Action<string> progress, CancellationToken token)
    {
        new SetupOptions("COM1",ssid,password,"Weather",0,0,"UTC0",false,false,false).Validate();
        await gate.WaitAsync(token);
        try {
            var (client,address)=await Open(token);
            using(client) {
                await Read(client,address,token);
                await ConfirmOnDisplayAsync(client,address,"wifi",()=>progress(L.Text("tap_allow_on_your_clawpal_display_to_continue")),token);
                using var body=JsonBody(new { ssid,password });
                using var response=await client.PostAsync(new Uri(address,"wifi"),body,token);
                response.EnsureSuccessStatusCode();
                using var ack=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!ack.RootElement.TryGetProperty("saved",out var saved) || saved.ValueKind!=JsonValueKind.True) throw new IOException(L.Text("wi_fi_change_was_not_confirmed"));
            }
            WifiSsid=ssid;Status=L.Text("wi_fi_saved_your_display_is_reconnecting");
        } finally { gate.Release(); }
    }
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            if(!Suspended && config.Token.Length>0 && FirmwareVersion.Length==0 && !config.SettingsPending) {
                try { using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(12));await ReadAsync(timeout.Token); }
                catch(OperationCanceledException) when(token.IsCancellationRequested){return;}
                catch(Exception e){Log.Write("Display capability discovery: "+e.GetType().Name);}
            }
            if (!Suspended && config.Token.Length>0 && config.SettingsPending) {
                try { using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(14));await SyncAsync(timeout.Token); }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { return; }
                catch(Exception e) { var message=FailureMessage(e);if(Status!=message)Log.Write("Settings sync: "+e.GetType().Name+"; "+message);Status=message; }
            }
            try { await Task.Delay(5000,token); } catch(OperationCanceledException) { return; }
        }
    }
}
