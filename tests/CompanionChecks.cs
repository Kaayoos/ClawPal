using ClawPal.Companion;
using System.Text.Json;

// Config recovery, the installed-apps entry, CPU sensor choice and how long a Claude
// permission prompt stays "waiting".
internal static class CompanionChecks
{
    internal static int Run(string directory)
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS: "+label);}

        var folder=Path.Combine(directory,"config-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"config.json");
        File.WriteAllText(path,"{\"DeviceId\":\"001122334455\",\"Paused\":tru");
        try{Configuration.LoadFrom(path,recover:false);Check(false,"hook processes leave a damaged config alone");}
        catch(JsonException){Check(File.Exists(path),"hook processes leave a damaged config alone");}
        var recovered=Configuration.LoadFrom(path);
        var aside=Directory.GetFiles(folder,"config.json.damaged-*");
        Check(!File.Exists(path)&&aside.Length==1&&File.ReadAllText(aside[0]).EndsWith("tru"),"a damaged config is moved aside, not deleted");
        Check(recovered.Token.Length==0&&!recovered.SetupComplete&&recovered.DeviceId.Length==0&&Configuration.Recovered,"a damaged config returns to setup");
        File.WriteAllText(path,"{\"DeviceId\":\"001122334455\",\"SetupComplete\":false}");
        Check(Configuration.LoadFrom(path).DeviceId=="001122334455"&&File.Exists(path),"a valid config still loads");
        Directory.Delete(folder,true);

        var exe=@"D:\Profiles\Example User\.clawpal\app\ClawPal.Companion.exe";
        var entry=Uninstaller.Entry(exe,"1.2.3",5_000_000);
        Check((string)entry["UninstallString"]=="\""+exe+"\" --uninstall"&&(string)entry["QuietUninstallString"]=="\""+exe+"\" --uninstall --quiet","uninstall commands quote the per-user path");
        Check((string)entry["DisplayVersion"]=="1.2.3"&&(int)entry["EstimatedSize"]==4882&&(int)entry["NoModify"]==1&&(string)entry["InstallLocation"]==Path.GetDirectoryName(exe),"installed-apps entry describes this version and folder");
        Check(StartupContract.Origin(["--uninstall"])=="uninstall","uninstall launches are logged as such");

        Check(SensorContract.PickCpu([("GPU Temperature",70),("CPU Core #1",55),("CPU Package",61.26)])==61.3,"CPU package temperature is preferred and rounded");
        Check(SensorContract.PickCpu([("Core Max",58),("Core (Tctl/Tdie)",63)])==63,"AMD control temperature wins over per-core maxima");
        Check(SensorContract.PickCpu([("CPU Package",0),("CPU Package",double.NaN),("Core Max",130)]) is null,"implausible readings are ignored");
        Check(SensorContract.PickCpu([("Motherboard",40),("GPU Hot Spot",80)]) is null,"other sensors are never reported as the CPU");

        var now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        ClaudeSession Session(string state,DateTimeOffset at)=>new("s1","Task","Project",state,at,at,123,456,false,CodexWindow.Empty,CodexWindow.Empty,at);
        Check(ClaudeContract.Snapshot([Session("waiting",now)],now.AddMinutes(10),_=>true).Activity!.Items[0].State=="waiting","a permission prompt keeps waiting while nobody answers");
        Check(ClaudeContract.Snapshot([Session("waiting",now)],now.AddMinutes(31),_=>true).Activity!.Items[0].State=="unknown","a forgotten prompt stops alerting after half an hour");
        Check(ClaudeContract.Snapshot([Session("working",now)],now.AddMinutes(3),_=>true).Activity!.Items[0].State=="unknown","silent work still expires after two minutes");
        Check(ClaudeContract.Snapshot([Session("waiting",now)],now,_=>false).Activity!.Items[0].State=="unknown","a closed session never waits");
        return count;
    }
}
