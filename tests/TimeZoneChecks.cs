using ClawPal.Companion;

internal static class TimeZoneChecks
{
    internal static int Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;Console.WriteLine("PASS: "+label);}
        Check(TimeZones.FromIana("Europe/Warsaw")=="CET-1CEST,M3.5.0,M10.5.0/3","Warsaw maps to the Central Europe preset rule");
        Check(TimeZones.FromIana("America/Sao_Paulo")=="<-03>3","Sao Paulo gets its own rule");
        Check(TimeZones.FromIana("America/Phoenix")=="MST7","Arizona keeps standard time all year");
        Check(TimeZones.FromIana("Asia/Kathmandu")=="<+0545>-5:45","Quarter-hour offsets are kept");
        Check(TimeZones.FromIana("Asia/Jerusalem") is null,"Rules the display cannot parse fall back to manual choice");
        Check(TimeZones.FromIana("Mars/Olympus_Mons") is null && TimeZones.FromIana("") is null && TimeZones.FromIana(null) is null,"Unknown zones are not guessed");
        foreach(var name in new[]{"Europe/Warsaw","America/New_York","Australia/Sydney","Pacific/Chatham","Africa/Nairobi","Asia/Tokyo"})
            new DeviceSettings{City="Test",Latitude=1,Longitude=1,Timezone=TimeZones.FromIana(name)!}.Validate();
        Check(true,"Generated rules pass display settings validation");

        var box=new ChoiceBox();
        TimeZones.Fill(box,"CET-1CEST,M3.5.0,M10.5.0/3");
        Check(box.Items.Count==TimeZones.Presets.Length && box.SelectedItem is TimeZoneChoice preset && preset==TimeZones.Presets[1],"A preset rule selects its preset without extra entries");
        TimeZones.Fill(box,"<-03>3");
        Check(box.Items.Count==TimeZones.Presets.Length+1 && ((TimeZoneChoice)box.SelectedItem!).Posix=="<-03>3","A saved non-preset rule stays selected instead of falling back to UTC");
        Check(TimeZones.SelectFor(box,new Place("Lima","Peru","Lima",-12,-77,"America/Lima")) && ((TimeZoneChoice)box.SelectedItem!).Posix=="<-05>5","Choosing a place selects its time zone");
        Check(box.SelectedItem!.ToString()!.Contains("America/Lima"),"The location zone is labeled with its IANA name");
        Check(!TimeZones.SelectFor(box,new Place("Haifa","Israel","Haifa",32,35,"Asia/Jerusalem")) && ((TimeZoneChoice)box.SelectedItem!).Posix=="<-05>5","An unsupported place zone leaves the current choice alone");
        return count;
    }
}
