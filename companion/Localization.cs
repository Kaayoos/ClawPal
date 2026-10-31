using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ClawPal.Companion;

public sealed record LanguageChoice(string Code,string Name,string Culture)
{
    public override string ToString()=>Name;
}

// Both applications are built from languages/*.json. Never translate protocol values.
public static class L
{
    private sealed record Pack(LanguageChoice Choice,Dictionary<string,string> Companion,Dictionary<string,string> Display,Dictionary<string,string> CompanionStudio,Dictionary<string,string> DisplayStudio);
    private static readonly Dictionary<string,Pack> packs=Load();
    private static string current="en";
    private static string? variant;
    private static readonly object gate=new();
    private static readonly Dictionary<string,(string Key,object?[] Args)> rendered=new();
    private static readonly Queue<string> renderOrder=new();
    public static event Action? Changed;
    public static string Language=>Volatile.Read(ref current);
    public static CultureInfo Culture=>CultureInfo.GetCultureInfo(packs[Language].Choice.Culture);
    public static IReadOnlyList<LanguageChoice> Languages=>packs.Values.Select(p=>p.Choice).OrderBy(p=>p.Code=="en"?0:1).ThenBy(p=>p.Name).ToArray();
    public static bool Supports(string? code)=>code is not null && packs.ContainsKey(code);
    public static string Normalize(string? code)=>Supports(code)?code!:"en";
    public static void SetLanguage(string? code)
    {
        string next=Normalize(code);if(next==Language)return;
        Volatile.Write(ref current,next);Changed?.Invoke();
    }
    /* Studio's calmer wording replaces individual keys; everything else keeps the base text. */
    public static void SetVariant(string? theme)
    {
        string? next=theme=="studio"?theme:null;if(next==Volatile.Read(ref variant))return;
        Volatile.Write(ref variant,next);Changed?.Invoke();
    }
    private static string? Studio(Dictionary<string,string> section,string key)=>Volatile.Read(ref variant) is null?null:section.GetValueOrDefault(key);
    public static string Text(string key,params object?[] args)
    {
        var pack=packs[Language];
        string pattern=Studio(pack.CompanionStudio,key)??pack.Companion.GetValueOrDefault(key)??packs["en"].Companion.GetValueOrDefault(key)??key;
        string value=args.Length==0?pattern:string.Format(Culture,pattern,args);
        // Retain a bounded set of formatted UI messages for a live language change.
        lock(gate){if(!rendered.ContainsKey(value)){renderOrder.Enqueue(value);while(renderOrder.Count>1024)rendered.Remove(renderOrder.Dequeue());}rendered[value]=(key,args);}
        return value;
    }
    public static string DisplayText(string code,string key)=>Studio(packs[Normalize(code)].DisplayStudio,key)??packs[Normalize(code)].Display.GetValueOrDefault(key)??packs["en"].Display.GetValueOrDefault(key)??key;
    internal static string Retranslate(string value)
    {
        (string Key,object?[] Args) found;
        lock(gate){if(rendered.TryGetValue(value,out found))return Text(found.Key,found.Args);}
        foreach(var pack in packs.Values)foreach(var entry in pack.Companion.Concat(pack.CompanionStudio))if(entry.Value==value)return Text(entry.Key);
        return value;
    }
    internal static void RefreshControls(Control root)
    {
        // Editors contain user data (SSID, password, city), never translation keys.
        if(root is ClawTextField input){input.PlaceholderText=Retranslate(input.PlaceholderText);return;}
        if(root is TextBoxBase)return;
        root.Text=Retranslate(root.Text);
        if(root.AccessibleName is { } name)root.AccessibleName=Retranslate(name);
        foreach(Control child in root.Controls)RefreshControls(child);
        root.Invalidate();
    }
    private static Dictionary<string,Pack> Load()
    {
        var result=new Dictionary<string,Pack>(StringComparer.Ordinal);
        var assembly=typeof(L).Assembly;
        foreach(string resource in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("ClawPal.Languages.",StringComparison.Ordinal)&&n.EndsWith(".json",StringComparison.Ordinal)))
        {
            using var stream=assembly.GetManifestResourceStream(resource)!;using var document=JsonDocument.Parse(stream);
            var root=document.RootElement;var meta=root.GetProperty("meta");
            var choice=new LanguageChoice(meta.GetProperty("code").GetString()!,meta.GetProperty("name").GetString()!,meta.GetProperty("culture").GetString()!);
            _=CultureInfo.GetCultureInfo(choice.Culture);
            Dictionary<string,string> Section(string name)=>root.TryGetProperty(name,out var section)?section.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!,StringComparer.Ordinal):new(StringComparer.Ordinal);
            result.Add(choice.Code,new(choice,Section("companion"),Section("display"),Section("companion_studio"),Section("display_studio")));
        }
        if(!result.ContainsKey("en"))throw new InvalidDataException("Missing base language catalog: en.json");
        return result;
    }
}
