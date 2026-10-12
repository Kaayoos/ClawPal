using System.Text.Json;

namespace ClawPal.Companion;

public sealed record BoardDefinition(string Id,string Name,string Chip,int Width,int Height,int FlashBytes,
    string Psram,bool Experimental,string Driver,string Layout,int MaxBytes,string AssetName,int Profile)
{
    public override string ToString()=>Name+(Experimental?" · "+L.Text("experimental"):"");
    public string Description=>L.Text("board_dimensions",Width,Height,FlashBytes/1048576)+" · "+L.Text(Experimental?"experimental_untested":"hardware_tested");
}

public static class BoardCatalog
{
    public const string DefaultId="waveshare-esp32-s3-touch-lcd-4.3b";
    private sealed record Catalog(int Schema,string Default,BoardDefinition[] Boards);
    private static readonly Lazy<BoardDefinition[]> catalog=new(()=>{
        using var stream=typeof(BoardCatalog).Assembly.GetManifestResourceStream("ClawPal.Boards")??throw new IOException("Missing board catalog");
        var value=JsonSerializer.Deserialize<Catalog>(stream)??throw new IOException("Invalid board catalog");
        if(value.Schema!=1||value.Default!=DefaultId||value.Boards.Select(b=>b.Id).Distinct().Count()!=value.Boards.Length)
            throw new IOException("Invalid board catalog identity");
        return value.Boards;
    });
    public static IReadOnlyList<BoardDefinition> All=>catalog.Value;
    public static BoardDefinition Get(string id)=>All.SingleOrDefault(b=>b.Id==id)??throw new IOException(L.Text("unsupported_display_model"));
}
