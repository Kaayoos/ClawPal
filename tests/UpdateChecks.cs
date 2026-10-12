using ClawPal.Companion;
using System.Security.Cryptography;
using System.Text.Json;

internal static class UpdateChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS: "+label);}
        void Reject(Action action,string label){try{action();}catch(Exception e)when(e is IOException or ArgumentException or NotSupportedException){Check(true,label);return;}throw new Exception(label);}
        byte[] bytes=new byte[288];bytes[0]=0xe9;bytes[12]=9;
        var file=new FirmwareFile(FirmwareRelease.AssetName,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)));
        var release=new FirmwareRelease(1,"0.3.0",FirmwareRelease.BoardId,FirmwareRelease.LayoutId,"0.3.0",file);
        release.CheckImage(bytes);Check(true,"matching board, layout, length, checksum and chip pass update preflight");
        Reject(()=>(release with{Board="other-board"}).Validate(),"wrong firmware board rejected");
        Reject(()=>(release with{Layout="future-layout"}).Validate(),"partition migration is never attempted over Wi-Fi");
        Reject(()=>(release with{Schema=2}).Validate(),"unknown release schema rejected");
        Reject(()=>(release with{MinCompanion="99.0.0"}).Validate(),"incompatible companion version rejected");
        Reject(()=>(release with{Version="0.3.0-preview"}).Validate(),"unstable firmware version rejected by stable channel");
        Reject(()=>(release with{Firmware=file with{File="../firmware.bin"}}).Validate(),"release file traversal rejected");
        Reject(()=>(release with{Firmware=file with{Bytes=FirmwareRelease.MaxBytes+1}}).Validate(),"oversized OTA image rejected");
        Reject(()=>(release with{Firmware=file with{Sha256="invalid"}}).Validate(),"invalid checksum encoding rejected");
        Reject(()=>release.CheckImage(bytes[..200]),"truncated firmware rejected before upload");
        var altered=(byte[])bytes.Clone();altered[100]=1;Reject(()=>release.CheckImage(altered),"changed firmware bytes rejected before upload");
        var wrongChip=(byte[])bytes.Clone();wrongChip[12]=0;
        Reject(()=>(release with{Firmware=file with{Sha256=Convert.ToHexString(SHA256.HashData(wrongChip))}}).CheckImage(wrongChip),"wrong ESP chip rejected despite matching checksum");
        Check(FirmwareRelease.ParseVersion("0.10.0")>FirmwareRelease.ParseVersion("0.9.0"),"release versions compare numerically");
        string json=JsonSerializer.Serialize(release,DeviceSettingsClient.Json);
        JsonSerializer.Deserialize<FirmwareRelease>(json,DeviceSettingsClient.Json)!.Validate();Check(json.Contains("min_companion"),"release manifest uses documented JSON field names");
        Check(GithubFirmware.Repository(" Kaayoos/clawpal ")=="Kaayoos/clawpal","repository field trims surrounding whitespace");
        Reject(()=>GithubFirmware.Repository("https://github.com/owner/repo"),"repository field rejects URLs instead of silently changing the source");
        Check(GithubFirmware.AssetUrl("https://github.com/Kaayoos/clawpal/releases/download/v0.3.0/clawpal-release.json","Kaayoos/clawpal").Host=="github.com","release assets stay in the official repository");
        Reject(()=>GithubFirmware.AssetUrl("http://github.com/Kaayoos/clawpal/releases/download/v0.3.0/a","Kaayoos/clawpal"),"unencrypted release assets rejected");
        Reject(()=>GithubFirmware.AssetUrl("https://github.com/attacker/repo/releases/download/v0.3.0/a","Kaayoos/clawpal"),"cross-repository asset rejected");
        Check(!GithubFirmware.AllowedRedirect(new Uri("https://release-assets.githubusercontent.com.attacker.test/a")),"lookalike asset hosts rejected");
        Check(GithubFirmware.AllowedRedirect(new Uri("https://release-assets.githubusercontent.com/a")),"official GitHub release CDN is allowed");
        Reject(()=>GithubFirmware.Repository("other/project"),"alternate update repositories cannot be selected");
        (release with{MinCompanion="99.0.0"}).Validate(false);Check(true,"newer PC requirement can be displayed before firmware installation");
        var requests=new List<Uri>();
        Task<byte[]> Fetch(Uri url,int maximum,CancellationToken cancellation){
            requests.Add(url);
            if(url.Host=="api.github.com")return Task.FromResult(Api("v0.3.0"));
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(json));
        }
        byte[] Api(string tag,bool draft=false,bool prerelease=false,bool setup=true,string owner="Kaayoos/clawpal"){
            var names=new List<string>{"clawpal-release.json",FirmwareRelease.AssetName};if(setup)names.Add("ClawPal-Setup.exe");
            return JsonSerializer.SerializeToUtf8Bytes(new {tag_name=tag,draft,prerelease,assets=names.Select(name=>new{name,browser_download_url=$"https://github.com/{owner}/releases/download/{tag}/{name}"})});
        }
        var offer=GithubFirmware.Check(CancellationToken.None,Fetch).GetAwaiter().GetResult();
        Check(requests.Count==2&&requests[0].AbsoluteUri=="https://api.github.com/repos/Kaayoos/clawpal/releases/latest","update check reads only the official latest release and manifest");
        Check(offer.Release.Version=="0.3.0"&&offer.SetupDownload.AbsolutePath.EndsWith("/ClawPal-Setup.exe"),"complete official release produces firmware and PC update offer");
        void RejectApi(byte[] api,string label)=>Reject(()=>GithubFirmware.Check(CancellationToken.None,(url,_,_)=>Task.FromResult(url.Host=="api.github.com"?api:System.Text.Encoding.UTF8.GetBytes(json))).GetAwaiter().GetResult(),label);
        RejectApi(Api("v0.3.0",draft:true),"draft releases are never offered");
        RejectApi(Api("v0.3.0",prerelease:true),"prereleases are never offered");
        RejectApi(Api("v0.4.0"),"release tag must match the manifest version");
        RejectApi(Api("v0.3.0",setup:false),"incomplete releases without the installer are rejected");
        RejectApi(Api("v0.3.0",owner:"other/project"),"release metadata cannot redirect to an unofficial repository");
        Check(CompanionUpgrade.IsNewer("0.5.2.0",new Version(0,6,0,0)),"a newer downloaded installer upgrades the existing companion");
        Check(!CompanionUpgrade.IsNewer("0.6.0.0",new Version(0,6,0,0)),"same-version installers do not replace the companion");
        Check(!CompanionUpgrade.IsNewer("0.7.0.0",new Version(0,6,0,0)),"downloaded older installers cannot silently downgrade");
        Check(!CompanionUpgrade.IsNewer("invalid",new Version(0,6,0,0)),"unknown installed metadata is not silently overwritten");
        Check(GithubFirmware.AssetUrl("https://github.com/kaayoos/ClawPal/releases/download/v0.3.0/a","Kaayoos/clawpal").Host=="github.com","GitHub repository case normalization does not block official assets");
        return count;
    }
}
