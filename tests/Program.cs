using ClawPal.Companion;
using System.Security.Cryptography;
using System.Text;

int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS: "+name);}
void Reject(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new Exception("Accepted invalid input: "+name);}
var good=new SetupOptions("COM4","Test network","password123","Test City",52.1,20.9,"CET-1CEST,M3.5.0,M10.5.0/3",false,false,true);
good.Validate(); Check(true,"valid setup accepted");
Reject(()=>(good with {Port="COM4 & erase_flash"}).Validate(),"port injection rejected");
Reject(()=>(good with {Ssid=""}).Validate(),"empty network rejected");
Reject(()=>(good with {Ssid=new string('ą',17)}).Validate(),"SSID byte limit enforced");
Reject(()=>(good with {Password="short"}).Validate(),"short password rejected");
(good with {Password=""}).Validate();Check(true,"open network supported");
(good with {Password=new string('a',64)}).Validate();Check(true,"raw hexadecimal PSK supported");
Reject(()=>(good with {Password=new string('x',64)}).Validate(),"nonhex PSK rejected");
Reject(()=>(good with {Latitude=double.NaN}).Validate(),"NaN coordinate rejected");
Reject(()=>(good with {Longitude=181}).Validate(),"out of bounds location rejected");
Reject(()=>(good with {Timezone="UTC0\0garbage"}).Validate(),"invalid timezone rejected");
Reject(()=>NvsImage.Create(new Dictionary<string,string>{{"too_long_for_nvs_key","x"}}),"NVS key bounds enforced");
Reject(()=>NvsImage.Create(new Dictionary<string,string>{{"city","x\0y"}}),"embedded NUL rejected");
var values=new Dictionary<string,string>{{"ssid","Test network"},{"password","password123"},{"token",new string('a',64)},{"setup_key","12345678"},{"city","Łódź"},{"latitude","52.100000"},{"longitude","20.900000"},{"timezone","CET-1CEST,M3.5.0,M10.5.0/3"}};
var image=NvsImage.Create(values);
Check(image.Length==0x6000,"NVS partition size correct");
Check(image.Skip(4096).All(b=>b==255),"five spare NVS pages erased");
var directory=args.Length>0?args[0]:Path.GetTempPath();Directory.CreateDirectory(directory);
var path=Path.Combine(directory,"nvs-fixture.bin");File.WriteAllBytes(path,image);
var hash=Convert.ToHexString(SHA256.HashData(image));Installer.CheckHash(path,hash);Check(true,"valid checksum accepted");
try{Installer.CheckHash(path,new string('0',64));throw new Exception("corrupted hash accepted");}catch(IOException){Check(true,"corrupted checksum rejected");}
var profile=new BoardProfile("test","Test board","esp32s3",800,480,16777216,"test","",[]);
foreach(string scenario in new[]{"wrong-flash","no-mac","read-failure","truncated-backup","verification-failure"}) {
    var commands=new List<string[]>();
    Task<string> Tool(BoardProfile _,string port,string[] arguments) {
        commands.Add(arguments);
        if(arguments.Contains("flash_id")) return Task.FromResult(scenario=="wrong-flash"?"Chip is ESP32-S3\nDetected flash size: 8MB\nMAC: 00:11:22:33:44:55":scenario=="no-mac"?"Chip is ESP32-S3\nDetected flash size: 16MB":"Chip is ESP32-S3\nDetected flash size: 16MB\nMAC: 00:11:22:33:44:55");
        if(arguments.Contains("read_flash")) {
            if(scenario=="read-failure")throw new IOException("Simulated disconnected cable");
            using var f=File.Create(arguments[^1]);f.SetLength(scenario=="truncated-backup"?512:profile.FlashBytes);return Task.FromResult("");
        }
        if(arguments.Contains("verify_flash"))throw new IOException("Simulated verification failure");
        throw new Exception("Unexpected USB write command");
    }
    var installer=new Installer(new Progress<string>(),()=>profile,Tool,Path.Combine(directory,"backup-tests",scenario));
    try{await installer.Install(good,new Configuration(),()=>{});throw new Exception("Installation accepted failed preflight");}catch(IOException){}
    Check(!commands.Any(c=>c.Contains("write_flash")),scenario+" prevents every flash write");
}
passed += await InstallerUpdateChecks.Run(directory, good, profile);
passed += SettingsChecks.Run();
passed += ClaudeChecks.Run(directory);
passed += LocalizationChecks.Run();
passed += TimeZoneChecks.Run();
passed += CompanionChecks.Run(directory);
passed += UpdateChecks.Run();
passed += BoardChecks.Run();
passed += ChoiceBoxChecks.Run();
passed += DesktopLayoutChecks.Run();
Console.WriteLine($"{passed} installer and UI checks passed.");

