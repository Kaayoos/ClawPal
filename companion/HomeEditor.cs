using System.Drawing.Drawing2D;

namespace ClawPal.Companion;

internal static class AppArtwork
{
    private static readonly Dictionary<int,Bitmap> logos=Load();
    private static Dictionary<int,Bitmap> Load()
    {
        var result=new Dictionary<int,Bitmap>();var assembly=typeof(AppArtwork).Assembly;
        foreach(var (id,name) in new[]{(4,"CodexLogo.png"),(7,"ClaudeLogo2.png")}){
            var resource=assembly.GetManifestResourceNames().FirstOrDefault(s=>s.EndsWith(name));
            if(resource is null)continue;using var stream=assembly.GetManifestResourceStream(resource)!;using var bitmap=new Bitmap(stream);result[id]=new Bitmap(bitmap);
        }
        return result;
    }
    internal static void Draw(Graphics g,int id,Rectangle r)
    {
        if(logos.TryGetValue(id,out var image)){g.DrawImage(image,r);return;}
        using var pen=new Pen(id==6?ClawTheme.WeatherApp:ClawTheme.Accent,Math.Max(1.5f,r.Width/18f)){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        float x=r.X,y=r.Y,w=r.Width,h=r.Height;
        if(id==0){g.DrawRectangle(pen,x+w*.12f,y+h*.16f,w*.76f,h*.52f);g.DrawLine(pen,x+w*.5f,y+h*.68f,x+w*.5f,y+h*.84f);g.DrawLine(pen,x+w*.3f,y+h*.86f,x+w*.7f,y+h*.86f);}
        else if(id==2){using var frame=ClawTheme.Round(new RectangleF(x+w*.13f,y+h*.17f,w*.74f,h*.66f),w*.16f);g.DrawPath(pen,frame);
            g.DrawPolygon(pen,new[]{new PointF(x+w*.43f,y+h*.36f),new PointF(x+w*.64f,y+h*.5f),new PointF(x+w*.43f,y+h*.64f)});}
        else if(id==6){g.DrawEllipse(pen,x+w*.14f,y+h*.08f,w*.38f,h*.38f);g.DrawArc(pen,x+w*.18f,y+h*.4f,w*.32f,h*.34f,90,180);g.DrawArc(pen,x+w*.35f,y+h*.3f,w*.4f,h*.4f,180,180);g.DrawArc(pen,x+w*.64f,y+h*.47f,w*.25f,h*.27f,270,180);g.DrawLine(pen,x+w*.34f,y+h*.74f,x+w*.77f,y+h*.74f);}
        else {g.DrawEllipse(pen,x+w*.16f,y+h*.16f,w*.68f,h*.68f);g.DrawLine(pen,x+w*.5f,y+h*.5f,x+w*.5f,y+h*.3f);g.DrawLine(pen,x+w*.5f,y+h*.5f,x+w*.68f,y+h*.6f);if(id==3)g.DrawLine(pen,x+w*.4f,y+h*.05f,x+w*.6f,y+h*.05f);}
    }
}

internal sealed class HomeOrderList : Control
{
    private DeviceSettings settings;
    private readonly Dictionary<int,float> positions=new();
    private readonly System.Windows.Forms.Timer motion=new(){Interval=16};
    private int dragId=-1, selected, mouseY, offset;
    internal event Action<DeviceSettings>? Changed;
    private const int Row=62;
    public HomeOrderList(DeviceSettings value)
    {
        settings=value;Height=Row*5;TabStop=true;Cursor=Cursors.Hand;DoubleBuffered=true;
        AccessibleName=L.Text("home_shortcuts_drag_to_reorder_alt_and_up_or");AccessibleRole=AccessibleRole.List;
        motion.Tick+=(_,_)=>{
            bool moving=false;
            for(int i=0;i<settings.HomeOrder.Length;i++) {int id=settings.HomeOrder[i];float target=i*Row;float y=positions.GetValueOrDefault(id,target);y+=(target-y)*.3f;if(Math.Abs(target-y)>.2)moving=true;else y=target;positions[id]=y;}
            Invalidate();if(!moving&&dragId<0)motion.Stop();
        };
    }
    internal void SetSettings(DeviceSettings value){settings=value;selected=Math.Clamp(selected,0,Math.Max(0,value.HomeOrder.Length-1));if(SystemInformation.IsMenuAnimationEnabled)motion.Start();else{positions.Clear();Invalidate();}}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);Focus();int index=e.Y/Row;if(index<0||index>=settings.HomeOrder.Length)return;selected=index;
        if(e.X>Width-34){Change(settings with { HomeOrder=settings.HomeOrder.Where((_,i)=>i!=index).ToArray() });return;}
        dragId=settings.HomeOrder[index];mouseY=e.Y;offset=e.Y-index*Row;Capture=true;motion.Start();Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(dragId<0)return;mouseY=e.Y;int from=Array.IndexOf(settings.HomeOrder,dragId),to=Math.Clamp(e.Y/Row,0,settings.HomeOrder.Length-1);
        if(from!=to){selected=to;Change(settings.Move(from,to));}Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragId=-1;Capture=false;motion.Start();Invalidate();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragId=-1;motion.Start();}}
    protected override bool IsInputKey(Keys keyData)=> (keyData&Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(settings.HomeOrder.Length==0)return;
        if(e.KeyCode is Keys.Up or Keys.Down){int target=Math.Clamp(selected+(e.KeyCode==Keys.Up?-1:1),0,settings.HomeOrder.Length-1);if(e.Alt)Change(settings.Move(selected,target));selected=target;e.Handled=true;Invalidate();}
        if(e.KeyCode==Keys.Delete){Change(settings with { HomeOrder=settings.HomeOrder.Where((_,i)=>i!=selected).ToArray() });e.Handled=true;}
    }
    private void Change(DeviceSettings value){SetSettings(value);Changed?.Invoke(value);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        for(int i=0;i<settings.HomeOrder.Length;i++){int id=settings.HomeOrder[i];if(id!=dragId)DrawRow(e.Graphics,id,(int)positions.GetValueOrDefault(id,i*Row),i==selected&&Focused,false);}
        if(dragId>=0)DrawRow(e.Graphics,dragId,Math.Clamp(mouseY-offset,0,Math.Max(0,Height-Row)),false,true);
        if(settings.HomeOrder.Length==0)ClawTheme.Text(e.Graphics,L.Text("just_the_crab_add_a_shortcut_below_whenever_you"),Font,ClawTheme.Muted,new Rectangle(10,10,Width-20,100),TextFormatFlags.WordBreak);
    }
    private void DrawRow(Graphics g,int id,int y,bool focus,bool dragging)
    {
        ClawTheme.Fill(g,dragging?ClawTheme.Hover:ClawTheme.Surface,new RectangleF(1,y+2,Width-3,54),9);
        if(focus||dragging){using var p=ClawTheme.Round(new RectangleF(1,y+2,Width-3,54),9);using var pen=new Pen(ClawTheme.Accent);g.DrawPath(pen,p);}
        using var dots=new SolidBrush(ClawTheme.Muted);for(int x=0;x<2;x++)for(int j=0;j<3;j++)g.FillEllipse(dots,13+x*5,y+19+j*5,2,2);
        AppArtwork.Draw(g,id,new Rectangle(35,y+13,30,30));using var font=ClawTheme.Font(10,true);
        ClawTheme.Text(g,DisplayApps.Get(id).Name,font,ClawTheme.Ink,new Rectangle(78,y+3,Width-113,52));
        using var pen2=new Pen(ClawTheme.Muted,1.4f);g.DrawLine(pen2,Width-24,y+29,Width-14,y+29);
    }
    protected override void Dispose(bool disposing){if(disposing)motion.Dispose();base.Dispose(disposing);}
}

internal sealed class HomePreview : Panel
{
    private DeviceSettings settings;
    private readonly CrabView crab=new(){State="idle"};
    private readonly System.Windows.Forms.Timer clock=new(){Interval=1000};
    internal Rectangle ScreenBounds {get;private set;}
    internal Rectangle CrabBounds=>crab.Bounds;
    private readonly BoardDefinition board;
    internal HomePreview(DeviceSettings value, string boardId=BoardCatalog.DefaultId)
    {
        board=BoardCatalog.Get(boardId);settings=value;DoubleBuffered=true;BackColor=ClawTheme.Background;Size=new(500,300);Controls.Add(crab);
        clock.Tick+=(_,_)=>Invalidate();clock.Start();Resize+=(_,_)=>LayoutPreview();LayoutPreview();ApplyLook();
        AccessibleName=L.Text("illustrative_preview_of_your_display_s_home_screen_shortcut");
    }
    internal void SetSettings(DeviceSettings value){settings=value;Invalidate();}
    /* The preview follows the companion's look, which follows the display's. */
    internal void ApplyLook(){crab.Visible=ClawTheme.Mascot;BackColor=ClawTheme.Background;Invalidate();}
    private void LayoutPreview()
    {
        int width=Math.Min(Width,Height*board.Width/board.Height),height=width*board.Height/board.Width;
        ScreenBounds=new((Width-width)/2,(Height-height)/2,width,height);
        int size=Math.Max(48,(int)(width*.36)/48*48);
        if(board.Width<800){size=Math.Max(48,(int)(width*(board.Height<400?.30:.40))/48*48);crab.SetBounds(ScreenBounds.Right-size-8,ScreenBounds.Top+4,size,size);}
        else crab.SetBounds(ScreenBounds.X+(int)(width*.275)-size/2,ScreenBounds.Y+(int)(height*.61)-size/2,size,size);Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(ScreenBounds.Width<10)return;
        if(!ClawTheme.Mascot){PaintStudio(e.Graphics);return;}
        if(board.Width<800){PaintCompact(e.Graphics);return;}
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var screen=ScreenBounds;float scale=screen.Width/800f;
        Rectangle R(int x,int y,int w,int h)=>new(screen.X+(int)(x*scale),screen.Y+(int)(y*scale),(int)(w*scale),(int)(h*scale));
        Font F(float pixels,bool bold=false)=>new("Segoe UI",Math.Max(8,pixels*scale),bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
        ClawTheme.Fill(e.Graphics,ClawTheme.Background,screen,12);
        using var border=ClawTheme.Round(new RectangleF(screen.X+1,screen.Y+1,screen.Width-3,screen.Height-3),12);
        using var pen=new Pen(ClawTheme.Border);e.Graphics.DrawPath(pen,border);
        e.Graphics.DrawLine(pen,R(446,34,0,0).Location,R(446,446,0,0).Location);
        using var timeFont=F(76,true);using var small=F(19);using var rowFont=F(23,true);
        string time=DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm",System.Globalization.CultureInfo.InvariantCulture);
        ClawTheme.Text(e.Graphics,time,timeFont,ClawTheme.Ink,R(18,26,410,96),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        ClawTheme.Text(e.Graphics,DateTime.Now.ToString(settings.Use24h?"ddd, d MMM":"ddd, d MMM · tt",System.Globalization.CultureInfo.GetCultureInfo(L.Languages.First(l=>l.Code==L.Normalize(settings.Language)).Culture)),small,ClawTheme.Muted,R(18,118,410,36),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        ClawTheme.Text(e.Graphics,L.DisplayText(settings.Language,"your_little_desk_companion"),small,ClawTheme.Muted,R(18,428,410,32),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        int rowHeight=Math.Min(98,412/Math.Max(1,settings.HomeOrder.Length));
        for(int i=0;i<settings.HomeOrder.Length;i++){
            int id=settings.HomeOrder[i],y=34+i*rowHeight;AppArtwork.Draw(e.Graphics,id,R(466,y+15,46,46));
            ClawTheme.Text(e.Graphics,L.DisplayText(settings.Language,id switch{0=>"pc_monitor",1=>"clock",2=>"music",3=>"timer",4=>"codex",7=>"claude",_=>"weather"}),rowFont,ClawTheme.Ink,R(528,y+8,254,34));
            string note=id switch{0=>L.DisplayText(settings.Language,"live_pc_readings"),1=>L.DisplayText(settings.Language,"local_time"),6=>settings.City,2=>L.DisplayText(settings.Language,"spotify_playback"),3=>L.DisplayText(settings.Language,"a_countdown_for_anything"),_=>L.DisplayText(settings.Language,"account_limits")};
            ClawTheme.Text(e.Graphics,note,small,ClawTheme.Muted,R(528,y+43,254,30));
        }
    }
    private void PaintCompact(Graphics g)
    {
        var r=ScreenBounds;float s=r.Width/(float)board.Width;bool tiny=board.Height<400;
        Rectangle R(int x,int y,int w,int h)=>new(r.X+(int)(x*s),r.Y+(int)(y*s),(int)(w*s),(int)(h*s));
        using var time=new Font("Segoe UI",Math.Max(8,48*s),FontStyle.Bold,GraphicsUnit.Pixel);
        using var title=new Font("Segoe UI",Math.Max(8,20*s),FontStyle.Bold,GraphicsUnit.Pixel);
        using var note=new Font("Segoe UI",Math.Max(8,16*s),FontStyle.Regular,GraphicsUnit.Pixel);
        var saved=g.Save();g.SetClip(r);ClawTheme.Fill(g,ClawTheme.Background,r,12);
        ClawTheme.Text(g,DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm"),time,ClawTheme.Ink,R(12,tiny?4:30,tiny?board.Width-120:260,64));
        ClawTheme.Text(g,DateTime.Now.ToString("ddd, d MMM"),note,ClawTheme.Muted,R(12,tiny?65:96,board.Width-24,28));
        ClawTheme.Text(g,L.DisplayText(settings.Language,"your_little_desk_companion"),note,ClawTheme.Muted,R(12,tiny?94:190,board.Width-24,30));
        for(int i=0;i<settings.HomeOrder.Length;i++){int y=(tiny?124:228)+i*76;if(y>board.Height-44)break;int id=settings.HomeOrder[i];AppArtwork.Draw(g,id,R(17,y+4,56,56));
            ClawTheme.Text(g,DisplayApps.Get(id).Name,title,ClawTheme.Ink,R(94,y+4,board.Width-110,28));
            ClawTheme.Text(g,id==6?settings.City:L.DisplayText(settings.Language,"waiting"),note,ClawTheme.Muted,R(94,y+38,board.Width-110,24));
        }
        ClawTheme.Fill(g,ClawTheme.Background,R(0,board.Height-44,board.Width,44),0);
        ClawTheme.Text(g,L.DisplayText(settings.Language,"apps"),note,ClawTheme.Accent,R(12,board.Height-37,90,30));
        ClawTheme.Text(g,L.DisplayText(settings.Language,"clock"),note,ClawTheme.Accent,R(board.Width-92,board.Height-37,80,30));
        g.Restore(saved);
    }
    /* Studio's home: a desk clock (light time, the date and the day's hairline), then hairline-separated shortcuts. */
    private void PaintStudio(Graphics g)
    {
        var r=ScreenBounds;float s=r.Width/(float)board.Width;bool wide=board.Width>=800,tiny=board.Height<400;
        Rectangle R(int x,int y,int w,int h)=>new(r.X+(int)(x*s),r.Y+(int)(y*s),(int)(w*s),(int)(h*s));
        Font F(float pixels,string family="Segoe UI")=>new(family,Math.Max(7,pixels*s),FontStyle.Regular,GraphicsUnit.Pixel);
        g.SmoothingMode=SmoothingMode.AntiAlias;var saved=g.Save();g.SetClip(r);
        ClawTheme.Fill(g,ClawTheme.Background,r,12);
        using(var border=ClawTheme.Round(new RectangleF(r.X+.5f,r.Y+.5f,r.Width-1,r.Height-1),12))using(var pen=new Pen(ClawTheme.Border))g.DrawPath(pen,border);
        using var rule=new Pen(ClawTheme.Border);using var time=F(wide?136:tiny?54:100,"Segoe UI Light");using var date=F(wide?24:16);using var title=F(wide?20:18);using var note=F(wide?16:14);using var hours=F(16);using var hoursSmall=F(14);
        string language=settings.Language;
        ClawTheme.Text(g,DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm",System.Globalization.CultureInfo.InvariantCulture),time,ClawTheme.Ink,wide?R(30,113,420,190):R(6,tiny?18:40,board.Width-24,tiny?66:104));
        var today=DateTime.Now;
        ClawTheme.Text(g,$"{today.Day} {L.DisplayText(language,$"month_long_{today.Month-1}")}",date,ClawTheme.Ink,wide?R(44,121,380,34):R(12,tiny?2:14,board.Width-24,26));
        PaintWeek(g,R,wide?44:12,wide?285:tiny?90:137,wide?292:board.Width-24,tiny?hoursSmall:hours,tiny?19:22);
        if(wide)g.DrawLine(rule,R(441,40,0,0).Location,R(441,420,0,0).Location);
        int count=settings.HomeOrder.Length,top=wide?(count>4?49:67):tiny?124:196,step=wide?(count>4?76:89):76;
        for(int i=0;i<count;i++){int id=settings.HomeOrder[i],x=wide?464:12,y=top+i*step;if(y>board.Height-44)break;
            using(var tile=ClawTheme.Round(R(x,y+2,66,66),12*s))using(var pen=new Pen(ClawTheme.Border))g.DrawPath(pen,tile);
            AppArtwork.Draw(g,id,R(x+11,y+13,44,44));
            ClawTheme.Text(g,DisplayApps.Get(id).Name,title,ClawTheme.Ink,R(x+82,y+4,wide?234:board.Width-110,30));
            ClawTheme.Text(g,id==6?settings.City:L.DisplayText(language,"waiting"),note,ClawTheme.Muted,R(x+82,y+38,wide?234:board.Width-110,26));
            if(i<count-1)g.DrawLine(rule,R(x+82,y+(count>4?70:78)+6,0,0).Location,R(wide?780:board.Width-12,y+(count>4?70:78)+6,0,0).Location);}
        if(!wide){ClawTheme.Fill(g,ClawTheme.Background,R(0,board.Height-44,board.Width,44),0);g.DrawLine(rule,R(0,board.Height-44,0,0).Location,R(board.Width,board.Height-44,0,0).Location);
            ClawTheme.Text(g,L.DisplayText(language,"apps"),note,ClawTheme.Accent,R(12,board.Height-37,90,30));ClawTheme.Text(g,L.DisplayText(language,"clock"),note,ClawTheme.Accent,R(board.Width-92,board.Height-37,80,30));}
        g.Restore(saved);
    }
    /* Monday-first week: today's name in ink over a short accent bar, the rest muted. */
    private void PaintWeek(Graphics g,Func<int,int,int,int,Rectangle> R,int x,int y,int width,Font font,int line)
    {
        // Justified like the display: Monday on the left margin, Sunday on the right, equal gaps between.
        int today=((int)DateTime.Now.DayOfWeek+6)%7;
        var start=R(x,y,width,line);var names=Enumerable.Range(0,7).Select(i=>L.DisplayText(settings.Language,$"weekday_{(i+1)%7}")).ToArray();
        var widths=names.Select(n=>TextRenderer.MeasureText(g,n,font,start.Size,TextFormatFlags.NoPadding).Width).ToArray();
        float gap=(start.Width-widths.Sum())/6f,left=start.X;
        using var mark=new SolidBrush(ClawTheme.Accent);var bar=R(x,y+line+4,1,2);
        for(int i=0;i<7;left+=widths[i]+gap,i++){int at=i==6?start.Right-widths[i]:(int)left;
            ClawTheme.Text(g,names[i],font,i==today?ClawTheme.Ink:ClawTheme.Muted,new Rectangle(at,start.Y,widths[i]+2,start.Height),TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.NoPadding);
            if(i==today)g.FillRectangle(mark,new Rectangle(at,bar.Y,widths[i],Math.Max(2,bar.Height)));}
    }
    protected override void Dispose(bool disposing){if(disposing)clock.Dispose();base.Dispose(disposing);}
}
