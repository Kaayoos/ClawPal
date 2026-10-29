using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ClawPal.Companion;

public sealed record FirmwareFile(string File,long Bytes,string Sha256);
public sealed record FirmwareRelease(int Schema,string Version,string Board,string Layout,string MinCompanion,FirmwareFile Firmware)
{
    public const string BoardId="waveshare-esp32-s3-touch-lcd-4.3b", LayoutId="ota-v1", AssetName="clawpal-waveshare-4.3b.bin";
    public const int MaxBytes=0x3f0000;
    public static Version ParseVersion(string text)
    {
        if(!Regex.IsMatch(text??"","^[0-9]+\\.[0-9]+\\.[0-9]+$") || !System.Version.TryParse(text,out var value))
            throw new IOException(L.Text("the_release_has_an_unsupported_version"));
        return value;
    }
    public void Validate(bool requireCompatibleCompanion=true)
    {
        var model=BoardCatalog.Get(Board);
        if(Schema!=1 || Layout!=model.Layout || Firmware is null || Firmware.File!=model.AssetName ||
           Firmware.Bytes<288 || Firmware.Bytes>model.MaxBytes || !Regex.IsMatch(Firmware.Sha256??"","^[a-fA-F0-9]{64}$"))
            throw new IOException(L.Text("this_firmware_release_is_not_compatible_with_your_display"));
        ParseVersion(Version);var minimum=ParseVersion(MinCompanion);
        if(requireCompatibleCompanion && minimum>typeof(FirmwareRelease).Assembly.GetName().Version)throw new NotSupportedException(L.Text("install_the_newer_clawpal_pc_companion_before_this_firmware"));
    }
    public void CheckImage(byte[] bytes)
    {
        Validate();
        if(bytes.LongLength!=Firmware.Bytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(Firmware.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.Text("firmware_download_is_incomplete_or_its_checksum_does_not"));
        // Standard ESP image header: magic and little-endian chip ID (ESP32-S3 = 9).
        if(bytes[0]!=0xe9 || bytes[12]!=9 || bytes[13]!=0)throw new IOException(L.Text("this_is_not_an_esp32_s3_firmware_image"));
    }
}

public sealed record FirmwareInfo(string Board,string Layout,string Version,bool OtaReady,bool Confirmed,string Partition,int MaxBytes);
public sealed record ReleaseOffer(FirmwareRelease Release,Uri Download,Uri SetupDownload);

internal static class GithubFirmware
{
    public const string DefaultRepository="Kaayoos/clawpal";
    public static string Repository(string value)
    {
        if(!string.Equals(value?.Trim(),DefaultRepository,StringComparison.OrdinalIgnoreCase))throw new ArgumentException(L.Text("official_updates_only"));
        return DefaultRepository;
    }
    internal static Uri AssetUrl(string value,string repo)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="github.com" || !uri.IsDefaultPort ||
           uri.UserInfo.Length>0 || !uri.AbsolutePath.StartsWith("/"+Repository(repo)+"/releases/download/",StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.Text("the_release_asset_is_not_hosted_in_the_configured"));
        return uri;
    }
    internal static bool AllowedRedirect(Uri uri) => uri.Scheme=="https" && uri.IsDefaultPort && uri.UserInfo.Length==0 &&
        uri.Host is "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";

    internal static async Task<byte[]> Download(Uri address,int maximum,CancellationToken token)
    {
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(2)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClawPal/"+(typeof(GithubFirmware).Assembly.GetName().Version?.ToString(3)??"0.0.0"));
        for(int redirects=0;redirects<6;redirects++) {
            using var response=await http.GetAsync(address,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308) {
                var next=response.Headers.Location is { } location?new Uri(address,location):throw new IOException(L.Text("missing_release_download_location"));
                if(!AllowedRedirect(next))throw new IOException(L.Text("unexpected_release_download_host"));address=next;continue;
            }
            if(response.StatusCode==HttpStatusCode.NotFound)throw new IOException(L.Text("no_published_release_was_found_in_this_github_repository"));
            if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)throw new IOException(L.Text("github_limited_update_checks_try_again_later"));
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength>maximum)throw new IOException(L.Text("release_asset_is_too_large"));
            await using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[16384];
            int read;while((read=await input.ReadAsync(buffer,token))>0){if(output.Length+read>maximum)throw new IOException(L.Text("release_asset_is_too_large"));await output.WriteAsync(buffer.AsMemory(0,read),token);}
            return output.ToArray();
        }
        throw new IOException(L.Text("too_many_release_download_redirects"));
    }
    internal static FirmwareRelease SelectRelease(string manifest,string boardId)
    {
        BoardCatalog.Get(boardId);
        using var doc=JsonDocument.Parse(manifest);var root=doc.RootElement;
        FirmwareRelease? selected;
        if(root.GetProperty("schema").GetInt32()==2){
            string version=root.GetProperty("version").GetString()??"";
            var releases=root.GetProperty("boards").Deserialize<FirmwareRelease[]>(DeviceSettingsClient.Json)??[];
            if(releases.Length==0||releases.Select(r=>r.Board).Distinct().Count()!=releases.Length)throw new IOException(L.Text("invalid_board_release"));
            FirmwareRelease.ParseVersion(version);
            foreach(var release in releases){
                if(release.Schema!=1||string.IsNullOrWhiteSpace(release.Board)||release.Version!=version)throw new IOException(L.Text("invalid_board_release"));
                // Later releases may add hardware unknown to this installed companion.
                if(BoardCatalog.All.Any(b=>b.Id==release.Board))release.Validate(false);
            }
            selected=releases.SingleOrDefault(r=>r.Board==boardId);
        }else selected=root.Deserialize<FirmwareRelease>(DeviceSettingsClient.Json);
        if(selected is null||selected.Board!=boardId)throw new IOException(L.Text("release_board_missing"));
        selected.Validate(false);return selected;
    }
    public static async Task<ReleaseOffer> Check(CancellationToken token,Func<Uri,int,CancellationToken,Task<byte[]>>? fetch=null,string boardId=BoardCatalog.DefaultId)
    {
        const string repo=DefaultRepository;fetch??=Download;
        var bytes=await fetch(new Uri("https://api.github.com/repos/"+repo+"/releases/latest"),262144,token);
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
        if(root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())throw new IOException(L.Text("only_published_stable_releases_are_supported"));
        var tag=root.GetProperty("tag_name").GetString()??"";
        var assets=root.GetProperty("assets").EnumerateArray().ToArray();
        Uri Find(string name){var matches=assets.Where(a=>a.GetProperty("name").GetString()==name).ToArray();
            if(matches.Length!=1)throw new IOException(L.Text("the_release_is_missing_its_clawpal_firmware_assets"));
            return AssetUrl(matches[0].GetProperty("browser_download_url").GetString()??"",repo);}
        var manifest=await fetch(Find("clawpal-release.json"),65536,token);
        var release=SelectRelease(Encoding.UTF8.GetString(manifest).TrimStart('\uFEFF'),boardId);
        release.Validate(false);if(tag!="v"+release.Version)throw new IOException(L.Text("the_release_tag_does_not_match_its_firmware_version"));
        return new(release,Find(release.Firmware.File),Find("ClawPal-Setup.exe"));
    }
}

public sealed class FirmwareUpdates(Configuration config,DeviceSettingsClient settings,Action<bool> maintenance)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private ReleaseOffer? offer;
    public bool Busy { get; private set; }
    public bool Checking { get; private set; }
    public FirmwareInfo? Device { get; private set; }
    private string statusText = L.Text("check_your_display_for_wireless_update_support");
    public string Status { get=>L.Retranslate(statusText);private set=>statusText=value; }
    private string releaseStatusText = L.Text("official_update_source");
    public string ReleaseStatus { get=>L.Retranslate(releaseStatusText);private set=>releaseStatusText=value; }
    public void ReportProblem(Exception error){Status=error is OperationCanceledException?L.Text("the_request_timed_out_reconnect_the_display_and_try"):error.Message;}
    public string Repository=>GithubFirmware.DefaultRepository;
    public string? AvailableCompanionVersion=>offer is { } found && FirmwareRelease.ParseVersion(found.Release.Version)>typeof(FirmwareUpdates).Assembly.GetName().Version?found.Release.Version:null;
    public string? NotificationVersion=>AvailableCompanionVersion??AvailableVersion;
    public bool CompanionRequired=>offer is { } found && FirmwareRelease.ParseVersion(found.Release.MinCompanion)>typeof(FirmwareUpdates).Assembly.GetName().Version;
    public Uri ReleasePage=>new("https://github.com/"+GithubFirmware.DefaultRepository+"/releases"+(offer is { } found?"/tag/v"+found.Release.Version:"/latest"));
    public string? AvailableVersion => offer is { } found && Device is { } device && FirmwareRelease.ParseVersion(found.Release.Version)>FirmwareRelease.ParseVersion(device.Version)?found.Release.Version:null;
    public bool BundledNewer => Device is { } device && FirmwareRelease.ParseVersion(Installer.BundledVersion)>FirmwareRelease.ParseVersion(device.Version);
    private async Task<(HttpClient,Uri)> Open(CancellationToken token)
    {
        var endpoint=await Connection.DiscoverAsync(config,token)??throw new IOException(L.Text("display_offline_connect_it_to_the_same_local_network"));
        var http=DeviceAuth.For(endpoint,config.Token).CreateClient(TimeSpan.FromMinutes(3));
        return (http,new Uri($"http://{endpoint.Address}:{Protocol.MonitorPort}/firmware"));
    }
    public async Task<FirmwareInfo> ReadDevice(CancellationToken token)
    {
        var (http,url)=await Open(token);using(http){using var response=await http.GetAsync(url,token);
            if(response.StatusCode==HttpStatusCode.NotFound){
                using var health=await http.GetAsync(new Uri(url,"/health"),token);health.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await health.Content.ReadAsStringAsync(token));
                if(json.RootElement.GetProperty("id").GetString()!=config.DeviceId)throw new IOException(L.Text("unexpected_display_identity"));
                var version=json.RootElement.GetProperty("version").GetString()??throw new IOException(L.Text("display_version_missing"));
                Device=new(FirmwareRelease.BoardId,"legacy",version,false,true,"factory",0);
            }
            else{response.EnsureSuccessStatusCode();Device=JsonSerializer.Deserialize<FirmwareInfo>(await response.Content.ReadAsStringAsync(token),DeviceSettingsClient.Json)??throw new IOException(L.Text("empty_firmware_status"));}
        }
        var model=BoardCatalog.Get(Device.Board);
        if(Device.Board!=config.BoardId || (Device.OtaReady&&(Device.Layout!=model.Layout||Device.MaxBytes!=model.MaxBytes)))throw new IOException(L.Text("this_firmware_release_is_not_compatible_with_your_display"));
        Status=Device.OtaReady?L.Text("ready_for_wi_fi_updates_your_settings_stay_on"):L.Text("one_time_usb_preparation_is_needed_your_settings_will");
        return Device;
    }
    public void SetAutomaticChecks(bool automatic)
    {
        config.AutoCheckUpdates=automatic;config.Save();
    }
    public async Task Check(CancellationToken token)
    {
        if(!await gate.WaitAsync(0,token))return;
        Checking=true;
        try {
            ReleaseStatus=L.Text("checking_github_releases");offer=await GithubFirmware.Check(token,boardId:config.BoardId);
            try{await ReadDevice(token);}catch(Exception e) when(e is IOException or HttpRequestException or JsonException or KeyNotFoundException){}
            ReleaseStatus=AvailableCompanionVersion is { } pc?L.Text("companion_update_available",pc):AvailableVersion is { } version?L.Text("firmware_available", version):Device is null?L.Text("firmware_latest", offer.Release.Version):L.Text("your_display_is_up_to_date");
        }catch(OperationCanceledException){ReleaseStatus=L.Text("update_check_timed_out_or_was_cancelled_try_again");if(token.IsCancellationRequested)throw;}catch(Exception e){offer=null;ReleaseStatus=e.Message;}
        finally{config.LastUpdateCheckUtc=DateTimeOffset.UtcNow;config.Save();Checking=false;gate.Release();}
    }
    public async Task RunAsync(CancellationToken token)
    {
        bool initial=true;
        while(!token.IsCancellationRequested){
            if(config.AutoCheckUpdates && Repository.Length>0 && (initial || config.LastUpdateCheckUtc is null || DateTimeOffset.UtcNow-config.LastUpdateCheckUtc>TimeSpan.FromHours(6)))
                try{await Check(token);}catch(OperationCanceledException)when(token.IsCancellationRequested){return;}
            initial=false;
            try{await Task.Delay(TimeSpan.FromMinutes(1),token);}catch(OperationCanceledException){return;}
        }
    }
    public async Task Install(bool bundled,CancellationToken token)
    {
        if(!await gate.WaitAsync(0,token))throw new IOException(L.Text("an_update_check_is_already_running"));
        Busy=true;
        try {
            var before=await ReadDevice(token);
            var model=BoardCatalog.Get(before.Board);
            FirmwareRelease release;byte[] bytes;
            if(bundled){var board=await Task.Run(()=>Installer.PreparePayload(before.Board),token);var part=board.Parts.Single(p=>p.File.EndsWith("/clawpal.bin",StringComparison.Ordinal));
                bytes=await File.ReadAllBytesAsync(Path.Combine(Installer.PayloadPath,part.File),token);
                release=new(1,board.Version,board.Id,model.Layout,"0.9.0",new(model.AssetName,bytes.Length,part.Sha256));}
            else{var found=offer??throw new IOException(L.Text("check_github_for_an_update_first"));release=found.Release;Status=L.Text("downloading_firmware_from_github");bytes=await GithubFirmware.Download(found.Download,model.MaxBytes,token);}
            release.CheckImage(bytes);
            if(release.Board!=before.Board || bytes.Length>before.MaxBytes)throw new IOException(L.Text("this_firmware_release_is_not_compatible_with_your_display"));
            if(!before.OtaReady || before.Layout!=release.Layout)throw new IOException(L.Text("prepare_wi_fi_updates_once_using_usb_first"));
            if(!before.Confirmed)throw new IOException(L.Text("the_display_is_still_confirming_its_previous_update_try"));
            if(FirmwareRelease.ParseVersion(release.Version)<FirmwareRelease.ParseVersion(before.Version))throw new IOException(L.Text("clawpal_does_not_install_older_firmware_automatically"));
            if(config.SettingsPending && (await settings.ReadAsync(token)).ProtocolSchema>=DeviceSettings.CurrentSchema)await settings.SyncAsync(token);
            var preferences=(await settings.ReadAsync(token)).Settings;
            var (confirmHttp,confirmUrl)=await Open(token);
            using(confirmHttp)await DeviceSettingsClient.ConfirmOnDisplayAsync(confirmHttp,confirmUrl,"firmware",release.Version,()=>Status=L.Text("tap_allow_on_your_clawpal_display_to_continue"),token);
            settings.Suspended=true;maintenance(true);Status=L.Text("sending_firmware_over_wi_fi");
            var (http,url)=await Open(token);
            try { using(http){using var request=new HttpRequestMessage(HttpMethod.Post,url);
                request.Headers.Add("X-ClawPal-Board",release.Board);request.Headers.Add("X-ClawPal-Layout",release.Layout);request.Headers.Add("X-ClawPal-SHA256",release.Firmware.Sha256.ToLowerInvariant());
                request.Content=new UploadContent(bytes,percent=>Status=L.Text("sending_firmware_over_wi_fi_0", percent));
                using var response=await http.SendAsync(request,token);response.EnsureSuccessStatusCode();
                using var ack=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if(!ack.RootElement.TryGetProperty("accepted",out var accepted)||accepted.ValueKind!=JsonValueKind.True)throw new IOException(L.Text("the_display_did_not_accept_the_firmware"));} }
            catch(HttpRequestException error) when(error.StatusCode is null){Status=L.Text("upload_connection_closed_checking_whether_the_display_accepted_the");}
            catch(OperationCanceledException) when(!token.IsCancellationRequested){Status=L.Text("upload_response_timed_out_checking_the_display_s_boot");}
            Status=L.Text("firmware_sent_waiting_for_restart_and_boot_confirmation");
            var until=DateTimeOffset.UtcNow.AddSeconds(100);bool confirmed=false;
            string expectedSlot=before.Partition=="ota_0"?"ota_1":"ota_0";
            while(DateTimeOffset.UtcNow<until){
                await Task.Delay(2000,token);
                try{using var poll=CancellationTokenSource.CreateLinkedTokenSource(token);poll.CancelAfter(TimeSpan.FromSeconds(5));
                    var current=await ReadDevice(poll.Token);
                    if(current.Version==release.Version && current.Partition==expectedSlot && current.Confirmed){
                        var after=await settings.ReadAsync(poll.Token);
                        if(!DeviceSettingsClient.Equivalent(preferences,after.Settings))throw new InvalidDataException(L.Text("firmware_started_but_display_settings_differ_review_your_saved"));
                        confirmed=true;break;}}
                catch(Exception e) when(e is HttpRequestException or OperationCanceledException or IOException){if(token.IsCancellationRequested)throw;}
                Status=L.Text("waiting_for_the_display_to_restart_and_confirm_its");
            }
            if(!confirmed)throw new IOException(L.Text("update_was_not_confirmed_the_display_may_have_rolled"));
            Status=L.Text("firmware_installed_preserved", release.Version);
            if(AvailableVersion is null)ReleaseStatus=L.Text("your_display_is_up_to_date");
        }catch(Exception e){Status=e is OperationCanceledException?L.Text("update_interrupted_reconnect_and_check_the_display_version_before"):e.Message;throw;}
        finally{settings.Suspended=false;maintenance(false);Busy=false;gate.Release();}
    }
    public async Task PrepareUsb(string port,IProgress<string> progress,CancellationToken token)
    {
        if(!await gate.WaitAsync(0,token))throw new IOException(L.Text("an_update_check_is_already_running"));Busy=true;
        try{settings.Suspended=true;maintenance(true);var forwarding=new Progress<string>(message=>{Status=message;progress.Report(message);});await new Installer(forwarding).EnableWirelessUpdates(port,config);Status=L.Text("usb_preparation_complete_the_display_is_restarting");
            Device=null;
            for(int attempt=0;attempt<12;attempt++) {
                await Task.Delay(2000,token);
                try{using var poll=CancellationTokenSource.CreateLinkedTokenSource(token);poll.CancelAfter(TimeSpan.FromSeconds(4));
                    var current=await ReadDevice(poll.Token);
                    if(current.OtaReady&&current.Confirmed){await settings.ReadAsync(poll.Token);Status=L.Text("wi_fi_updates_are_ready_settings_and_pairing_were");return;}}
                catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException){if(token.IsCancellationRequested)throw;}
            }
            Status=L.Text("usb_preparation_finished_reconnect_the_display_to_wi_fi");
        }
        catch(Exception e){Status=e.Message;throw;}
        finally{settings.Suspended=false;maintenance(false);Busy=false;gate.Release();}
    }
    private sealed class UploadContent(byte[] bytes,Action<int> progress):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=bytes.Length;return true;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>Write(stream,CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken token)=>Write(stream,token);
        private async Task Write(Stream stream,CancellationToken token){for(int offset=0;offset<bytes.Length;offset+=16384){int n=Math.Min(16384,bytes.Length-offset);await stream.WriteAsync(bytes.AsMemory(offset,n),token);progress((offset+n)*100/bytes.Length);}}
    }
}



