using ClawPal.Companion;
using System.Drawing;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class BoardChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS: "+label);}
        void Reject(Action action,string label){try{action();}catch(Exception e)when(e is IOException or ArgumentException or NotSupportedException){Check(true,label);return;}throw new Exception(label);}
        var models=BoardCatalog.All;
        Check(models.Count==7,"Seven exact hardware profiles are available");
        Check(models.Count(b=>!b.Experimental)==1&&!BoardCatalog.Get(BoardCatalog.DefaultId).Experimental,"Only the physically tested 4.3B is marked tested");
        Check(models.Select(b=>b.AssetName).Distinct().Count()==models.Count,"Each board has a unique update asset");
        Check(models.Single(b=>b.Driver=="cores3-spi").Psram=="quad","CoreS3 uses quad PSRAM");
        Check(new Configuration().BoardId==BoardCatalog.DefaultId,"Existing settings retain the original hardware model");
        byte[] bytes=new byte[288];bytes[0]=0xe9;bytes[12]=9;
        FirmwareRelease Release(BoardDefinition b)=>new(1,"0.8.0",b.Id,b.Layout,"0.8.0",new(b.AssetName,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes))));
        var releases=models.Select(Release).ToArray();
        string Manifest(IEnumerable<FirmwareRelease> entries,string version="0.8.0")=>JsonSerializer.Serialize(new{schema=2,version,boards=entries},DeviceSettingsClient.Json);
        string manifest=Manifest(releases);
        foreach(var board in models){
            var release=GithubFirmware.SelectRelease(manifest,board.Id);release.CheckImage(bytes);
            Check(release.Board==board.Id&&release.Firmware.File==board.AssetName,"Select exact update image: "+board.Name);
            Reject(()=>(release with{Firmware=release.Firmware with{Bytes=board.MaxBytes+1}}).Validate(),"Enforce OTA capacity: "+board.Id);
            var other=releases.First(r=>r.Board!=board.Id);
            Reject(()=>(release with{Firmware=other.Firmware}).Validate(),"Reject another model's image asset: "+board.Id);
            Reject(()=>GithubFirmware.SelectRelease(Manifest(releases.Where(r=>r.Board!=board.Id)),board.Id),"Missing board never falls back to another image: "+board.Id);
            var profile=new BoardProfile(board.Id,board.Name,board.Chip,board.Width,board.Height,board.FlashBytes,"0.8.0","",[]);
            string Usb(int mb)=>$"Chip is ESP32-S3\nDetected flash size: {mb}MB\nMAC: 00:11:22:33:44:55";
            Check(Installer.ParseIdentity(profile,Usb(board.FlashBytes/1048576))=="001122334455","Accept selected model's flash capacity: "+board.Id);
            Reject(()=>Installer.ParseIdentity(profile,Usb(board.FlashBytes==8388608?16:8)),"Reject wrong physical flash capacity: "+board.Id);
        }
        Reject(()=>GithubFirmware.SelectRelease(Manifest(releases.Append(releases[0])),BoardCatalog.DefaultId),"Reject duplicate board records");
        Reject(()=>GithubFirmware.SelectRelease(Manifest(releases,"0.9.0"),BoardCatalog.DefaultId),"Reject mixed release versions");
        Check(GithubFirmware.SelectRelease(JsonSerializer.Serialize(releases[0],DeviceSettingsClient.Json),BoardCatalog.DefaultId).Board==BoardCatalog.DefaultId,"Original single-board release format remains readable");
        Reject(()=>GithubFirmware.SelectRelease(JsonSerializer.Serialize(releases[0],DeviceSettingsClient.Json),models[1].Id),"Legacy release never targets an experimental board");
        var future=releases[0] with{Board="future-hardware",Firmware=releases[0].Firmware with{File="clawpal-future.bin"}};
        Check(GithubFirmware.SelectRelease(Manifest(releases.Append(future)),BoardCatalog.DefaultId).Board==BoardCatalog.DefaultId,"Future board additions do not break existing users' update checks");
        using(var stream=typeof(Installer).Assembly.GetManifestResourceStream("ClawPal.Payload")!)
        using(var zip=new ZipArchive(stream)){
            foreach(var board in models){
                using var entry=zip.GetEntry("boards/"+board.Id+".json")!.Open();
                var profile=JsonSerializer.Deserialize<BoardProfile>(entry)!;
                Check(profile.Id==board.Id&&profile.Version==typeof(Installer).Assembly.GetName().Version!.ToString(3)&&profile.FlashBytes==board.FlashBytes,"Bundled profile matches selected hardware: "+board.Id);
                var offsets=profile.Parts.Select(p=>Convert.ToInt32(p.Offset,16)).Order().ToArray();
                int[] expected=board.FlashBytes==8388608?[0,0x8000,0xf000,0x20000]:[0,0x8000,0x10000,0x800000];
                Check(offsets.SequenceEqual(expected),"Bundled USB offsets preserve the NVS settings partition: "+board.Id);
                foreach(var part in profile.Parts){using var data=zip.GetEntry(part.File)!.Open();Check(Convert.ToHexString(SHA256.HashData(data)).Equals(part.Sha256,StringComparison.OrdinalIgnoreCase),"Bundled firmware hash: "+part.File);}
            }
        }
        Exception? failure=null;
        var thread=new Thread(()=>{try{
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException,true);
            foreach(var board in models){
                using var preview=new HomePreview(new DeviceSettings(),board.Id);
                foreach(var size in new[]{new Size(380,300),new Size(560,420),new Size(700,460)}){
                preview.Size=size;
                Check(Math.Abs(preview.ScreenBounds.Width*(double)board.Height/board.Width-preview.ScreenBounds.Height)<1.01,"Preview keeps model aspect ratio: "+board.Id);
                Check(preview.ScreenBounds.Contains(preview.CrabBounds),"Crab stays inside selected display preview: "+board.Id);
                }
                using var bitmap=new Bitmap(preview.Width,preview.Height);preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,preview.Size));
            }
        }catch(Exception e){failure=e;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(60)))throw new Exception("Board preview checks timed out");
        if(failure is not null)throw new Exception("Board preview checks failed",failure);
        return count;
    }
}
