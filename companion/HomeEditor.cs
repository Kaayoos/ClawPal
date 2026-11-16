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
    /* accent and weather: the display's colours when drawing a preview of it; this app's otherwise. */
    internal static void Draw(Graphics g,int id,Rectangle r,Color? accent=null,Color? weather=null)
    {
        var ink=accent??ClawTheme.Accent;
        if(logos.TryGetValue(id,out var image)){var mode=g.InterpolationMode;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(image,r);g.InterpolationMode=mode;return;}
        if(id==8){ShortcutArt.Draw(g,"grid",r,ink);return;}
        var smoothing=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var pen=new Pen(id==6?weather??ClawTheme.WeatherApp:ink,Math.Max(1.5f,r.Width/16f)){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        float x=r.X,y=r.Y,w=r.Width,h=r.Height;
        if(id==0){g.DrawRectangle(pen,x+w*.12f,y+h*.16f,w*.76f,h*.52f);g.DrawLine(pen,x+w*.5f,y+h*.68f,x+w*.5f,y+h*.84f);g.DrawLine(pen,x+w*.3f,y+h*.86f,x+w*.7f,y+h*.86f);}
        else if(id==2){using var frame=ClawTheme.Round(new RectangleF(x+w*.13f,y+h*.17f,w*.74f,h*.66f),w*.16f);g.DrawPath(pen,frame);
            g.DrawPolygon(pen,new[]{new PointF(x+w*.43f,y+h*.36f),new PointF(x+w*.64f,y+h*.5f),new PointF(x+w*.43f,y+h*.64f)});}
        // Three faders at different levels, the display's own Audio mark.
        else if(id==9){
            using var knob=new SolidBrush(ink);
            foreach(var (cx,ky) in new[]{(.26f,.54f),(.5f,.24f),(.74f,.64f)}){
                g.DrawLine(pen,x+w*cx,y+h*.14f,x+w*cx,y+h*.86f);
                using var cap=ClawTheme.Round(new RectangleF(x+w*(cx-.1f),y+h*ky,w*.2f,h*.14f),w*.06f);g.FillPath(knob,cap);
            }
        }
        else if(id==6){g.DrawEllipse(pen,x+w*.14f,y+h*.08f,w*.38f,h*.38f);g.DrawArc(pen,x+w*.18f,y+h*.4f,w*.32f,h*.34f,90,180);g.DrawArc(pen,x+w*.35f,y+h*.3f,w*.4f,h*.4f,180,180);g.DrawArc(pen,x+w*.64f,y+h*.47f,w*.25f,h*.27f,270,180);g.DrawLine(pen,x+w*.34f,y+h*.74f,x+w*.77f,y+h*.74f);}
        else {g.DrawEllipse(pen,x+w*.16f,y+h*.16f,w*.68f,h*.68f);g.DrawLine(pen,x+w*.5f,y+h*.5f,x+w*.5f,y+h*.3f);g.DrawLine(pen,x+w*.5f,y+h*.5f,x+w*.68f,y+h*.6f);if(id==3)g.DrawLine(pen,x+w*.4f,y+h*.05f,x+w*.6f,y+h*.05f);}
        g.SmoothingMode=smoothing;
    }
}

/* The display as it stands on a desk: the black slim case (BuddyOS/hardware/slim-case), rendered
   straight on with its screen cut out, so the live preview shows through the glass. Screens of
   another shape get the same case drawn to fit. Every preview of the display sits in one. */
internal static class DeviceArt
{
    private static readonly Lazy<Bitmap?> photo=new(()=>{
        using var stream=typeof(DeviceArt).Assembly.GetManifestResourceStream("ClawPal.Companion.SlimCaseFront");
        return stream is null?null:new Bitmap(stream);
    });
    // The opening inside the rim, in the picture's own pixels (1331 x 911). The screen fills it edge to
    // edge: its colour runs right up to the rim and the 5:3 picture sits in the middle of it.
    private static readonly RectangleF Opening=new(45,45,1219,813);
    private const float PictureWidth=1331,PictureHeight=911,Rim=.04f;
    private static bool Photographed(int boardWidth,int boardHeight)=>photo.Value is not null&&Math.Abs(boardWidth/(float)boardHeight-5f/3)<.03f;
    /* Around the screen, as shares of its width: the case's rim, plus on the photographed case the
       screen colour above and below a 5:3 picture in its slightly taller opening. */
    private static (float Left,float Right,float Top,float Bottom) Margins(float ratio)
    {
        if(photo.Value is null||Math.Abs(ratio-.6f)>.03f)return (Rim,Rim,Rim,Rim);
        float band=(Opening.Height-Opening.Width*ratio)/2;
        return (Opening.X/Opening.Width,(PictureWidth-Opening.Right)/Opening.Width,(Opening.Y+band)/Opening.Width,(PictureHeight-Opening.Bottom+band)/Opening.Width);
    }
    /* How tall the whole case is when it is this wide, for a screen of this shape. */
    internal static int HeightFor(int width,float ratio){var m=Margins(ratio);return (int)(width/(1+m.Left+m.Right)*(ratio+m.Top+m.Bottom));}
    /* The largest screen of this shape whose case still fits the bounds. */
    internal static Rectangle FitScreen(Rectangle bounds,int boardWidth,int boardHeight)
    {
        float ratio=boardHeight/(float)boardWidth;var m=Margins(ratio);
        float s=Math.Min(bounds.Width/(1+m.Left+m.Right),bounds.Height/(ratio+m.Top+m.Bottom));
        int width=(int)s,height=(int)Math.Round(s*ratio);
        int caseWidth=(int)(s*(1+m.Left+m.Right)),total=(int)(s*(ratio+m.Top+m.Bottom));
        int left=bounds.X+(bounds.Width-caseWidth)/2+(int)(s*m.Left),top=bounds.Y+(bounds.Height-total)/2+(int)(s*m.Top);
        return new(left,top,width,height);
    }
    /* The screen's colour across the whole opening, its own drawing, then the case over it; a drawn case goes underneath instead. */
    internal static void Draw(Graphics g,Rectangle screen,int boardWidth,int boardHeight,Color ground,Action<Graphics> content)
    {
        if(!Photographed(boardWidth,boardHeight)){DrawnCase(g,screen,ground);content(g);return;}
        var picture=photo.Value!;float k=screen.Width/Opening.Width,band=(Opening.Height*k-screen.Height)/2;
        // A little past the opening, so the rim's soft inner edge never shows the page behind.
        using(var fill=new SolidBrush(ground))g.FillRectangle(fill,RectangleF.Inflate(new RectangleF(screen.X,screen.Y-band,screen.Width,Opening.Height*k),4,4));
        var state=g.Save();content(g);g.Restore(state);
        var mode=g.InterpolationMode;var offset=g.PixelOffsetMode;
        g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
        lock(picture)g.DrawImage(picture,new RectangleF(screen.X-Opening.X*k,screen.Y-band-Opening.Y*k,PictureWidth*k,PictureHeight*k));
        g.InterpolationMode=mode;g.PixelOffsetMode=offset;
    }
    /* The same case for screens of another shape: the black rim, with the screen right up to it. */
    private static void DrawnCase(Graphics g,Rectangle screen,Color ground)
    {
        float s=screen.Width;var mode=g.SmoothingMode;g.SmoothingMode=SmoothingMode.AntiAlias;
        var body=RectangleF.Inflate(screen,s*Rim,s*Rim);
        ClawTheme.Fill(g,Color.FromArgb(0x2A,0x27,0x23),body,s*.06f);
        ClawTheme.Fill(g,Color.FromArgb(0x1F,0x1D,0x1A),RectangleF.Inflate(body,-s*.012f,-s*.012f),s*.05f);
        ClawTheme.Fill(g,ground,screen,s*.02f);
        g.SmoothingMode=mode;
    }
}

internal sealed class HomeOrderList : Control
{
    private DeviceSettings settings;
    private readonly Dictionary<int,float> positions=new();
    private readonly System.Windows.Forms.Timer motion=new(){Interval=15};
    private int dragId=-1, selected, mouseY, offset, hover=-1;
    internal event Action<DeviceSettings>? Changed;
    private const int Row=62;
    public HomeOrderList(DeviceSettings value)
    {
        settings=value;Height=Row*5;TabStop=true;Cursor=Cursors.Hand;DoubleBuffered=true;BackColor=ClawTheme.Screen;
        AccessibleName=L.Text("home_shortcuts_drag_to_reorder_alt_and_up_or");AccessibleRole=AccessibleRole.List;
        motion.Tick+=(_,_)=>{
            bool moving=false;
            for(int i=0;i<settings.HomeOrder.Length;i++) {int id=settings.HomeOrder[i];float target=i*Row;float y=positions.GetValueOrDefault(id,target);y+=(target-y)*.3f;if(Math.Abs(target-y)>.2)moving=true;else y=target;positions[id]=y;}
            Invalidate();if(!moving&&dragId<0)motion.Stop();
        };
    }
    internal void SetSettings(DeviceSettings value){settings=value;selected=Math.Clamp(selected,0,Math.Max(0,value.HomeOrder.Length-1));if(ClawTheme.Motion)motion.Start();else{positions.Clear();Invalidate();}}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);Focus();int index=e.Y/Row;if(index<0||index>=settings.HomeOrder.Length)return;selected=index;
        if(e.X>Width-34){Change(settings with { HomeOrder=settings.HomeOrder.Where((_,i)=>i!=index).ToArray() });return;}
        dragId=settings.HomeOrder[index];mouseY=e.Y;offset=e.Y-index*Row;Capture=true;motion.Start();Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int over=e.Y/Row>=0&&e.Y/Row<settings.HomeOrder.Length?e.Y/Row:-1;if(over!=hover&&dragId<0){hover=over;Invalidate();}
        if(dragId<0)return;mouseY=e.Y;int from=Array.IndexOf(settings.HomeOrder,dragId),to=Math.Clamp(e.Y/Row,0,settings.HomeOrder.Length-1);
        if(from!=to){selected=to;Change(settings.Move(from,to));}Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=-1;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragId=-1;Capture=false;motion.Start();Invalidate();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragId=-1;motion.Start();}}
    protected override bool IsInputKey(Keys keyData)=> (keyData&Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(settings.HomeOrder.Length==0)return;
        if(e.KeyCode is Keys.Up or Keys.Down){int target=Math.Clamp(selected+(e.KeyCode==Keys.Up?-1:1),0,settings.HomeOrder.Length-1);if(e.Alt)Change(settings.Move(selected,target));selected=target;e.Handled=true;Invalidate();}
        if(e.KeyCode==Keys.Delete){Change(settings with { HomeOrder=settings.HomeOrder.Where((_,i)=>i!=selected).ToArray() });e.Handled=true;}
    }
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    private void Change(DeviceSettings value){SetSettings(value);Changed?.Invoke(value);}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;g.Clear(Parent?.BackColor??ClawTheme.Screen);
        for(int i=0;i<settings.HomeOrder.Length;i++){int id=settings.HomeOrder[i];if(id!=dragId)DrawRow(g,id,i,(int)positions.GetValueOrDefault(id,i*Row),i==selected&&Focused&&ShowFocusCues,false,i==hover);}
        if(dragId>=0)DrawRow(g,dragId,Array.IndexOf(settings.HomeOrder,dragId),Math.Clamp(mouseY-offset,0,Math.Max(0,Height-Row)),false,true,false);
        if(settings.HomeOrder.Length==0)ClawTheme.Text(g,L.Text("just_the_crab_add_a_shortcut_below_whenever_you"),Font,ClawTheme.Muted,new Rectangle(0,8,Width,100),TextFormatFlags.WordBreak|TextFormatFlags.Left|TextFormatFlags.Top);
    }
    private void DrawRow(Graphics g,int id,int index,int y,bool focus,bool dragging,bool hot)
    {
        var row=new Rectangle(0,y,Width,Row-8);
        ClawTheme.Fill(g,dragging?ClawTheme.Raised:hot?ClawTheme.Mix(ClawTheme.Surface,ClawTheme.Raised,.5f):ClawTheme.Surface,row);
        if(dragging)ClawTheme.Fill(g,ClawTheme.Accent,new Rectangle(0,y,4,row.Height));
        // Six square dots: something to hold.
        using(var dots=new SolidBrush(dragging?ClawTheme.Accent:ClawTheme.Faint))for(int x=0;x<2;x++)for(int j=0;j<3;j++)g.FillRectangle(dots,14+x*6,y+row.Height/2-8+j*6,3,3);
        AppArtwork.Draw(g,id,new Rectangle(38,y+(row.Height-28)/2,28,28));
        ClawTheme.Text(g,DisplayApps.Get(id).Name,ClawTheme.Font(ClawTheme.Lead,true),ClawTheme.Ink,new Rectangle(80,y,Width-80-44,row.Height));
        // The square minus that takes the app off Home.
        var minus=new Rectangle(Width-32,y+(row.Height-22)/2,22,22);
        ClawTheme.Fill(g,ClawTheme.Raised,minus);
        using(var bar=new SolidBrush(ClawTheme.Ink))g.FillRectangle(bar,minus.X+6,minus.Y+10,10,2);
        if(focus)ClawTheme.Frame(g,ClawTheme.Ink,row);
    }
    protected override void Dispose(bool disposing){if(disposing)motion.Dispose();base.Dispose(disposing);}
}

/* The display's Home as it looks right now, in the look chosen for the display, standing in its case. */
internal sealed class HomePreview : Panel
{
    private DeviceSettings settings;
    private readonly CrabView crab=new(){State="idle"};
    private readonly System.Windows.Forms.Timer clock=new(){Interval=1000};
    internal Rectangle ScreenBounds {get;private set;}
    internal Rectangle CrabBounds=>crab.Bounds;
    private readonly BoardDefinition board;
    private ThemeLook Look=>Themes.Get(settings.Theme,settings.Accent);
    internal HomePreview(DeviceSettings value, string boardId=BoardCatalog.DefaultId)
    {
        board=BoardCatalog.Get(boardId);settings=value;DoubleBuffered=true;BackColor=ClawTheme.Screen;Size=new(500,300);Controls.Add(crab);
        clock.Tick+=(_,_)=>Invalidate();clock.Start();Resize+=(_,_)=>LayoutPreview();LayoutPreview();ApplyLook();
        AccessibleName=L.Text("illustrative_preview_of_your_display_s_home_screen_shortcut");
    }
    internal void SetSettings(DeviceSettings value){bool look=value.Theme!=settings.Theme||value.Accent!=settings.Accent;settings=value;if(look)ApplyLook();Invalidate();}
    /* The crab is part of the crab look only; Studio's Home has the clock instead. */
    internal void ApplyLook(){crab.Visible=Look.Mascot;crab.BackColor=Look.Background;Invalidate();}
    private void LayoutPreview()
    {
        ScreenBounds=DeviceArt.FitScreen(ClientRectangle,board.Width,board.Height);
        int width=ScreenBounds.Width,height=ScreenBounds.Height;
        int size=Math.Max(48,(int)(width*.36)/48*48);
        if(board.Width<800){size=Math.Max(48,(int)(width*(board.Height<400?.30:.40))/48*48);crab.SetBounds(ScreenBounds.Right-size-8,ScreenBounds.Top+4,size,size);}
        else crab.SetBounds(ScreenBounds.X+(int)(width*.275)-size/2,ScreenBounds.Y+(int)(height*.61)-size/2,size,size);
        // The crab cannot be larger than the screen it lives on.
        if(!ScreenBounds.Contains(crab.Bounds)){size=Math.Max(0,Math.Min(width,height)/2/48*48);crab.SetBounds(ScreenBounds.X+(int)(width*.275)-size/2,ScreenBounds.Y+(int)(height*.61)-size/2,size,size);}
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(ScreenBounds.Width<10)return;
        var look=Look;
        DeviceArt.Draw(e.Graphics,ScreenBounds,board.Width,board.Height,look.Background,g=>{
            if(!look.Mascot)PaintStudio(g,look);else if(board.Width<800)PaintCompact(g,look);else PaintCrab(g,look);});
    }
    /* The crab look's Home on an 800 x 480 screen: the clock and the crab, then the five apps. */
    private void PaintCrab(Graphics g,ThemeLook look)
    {
        g.SmoothingMode=SmoothingMode.AntiAlias;var screen=ScreenBounds;float scale=screen.Width/800f;
        Rectangle R(int x,int y,int w,int h)=>new(screen.X+(int)(x*scale),screen.Y+(int)(y*scale),(int)(w*scale),(int)(h*scale));
        Font F(float pixels,bool bold=false)=>new("Segoe UI",Math.Max(6,pixels*scale),bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
        ClawTheme.Fill(g,look.Background,screen);
        using var pen=new Pen(look.Border);
        if(!look.Cased)g.DrawLine(pen,R(446,34,0,0).Location,R(446,446,0,0).Location);
        // Cased looks set the clock in the display's heavy face, as this app sets its headlines.
        using var timeFont=look.Cased?new Font("Segoe UI Black",Math.Max(6,84*scale),FontStyle.Regular,GraphicsUnit.Pixel):F(76,true);using var small=F(19);using var rowFont=F(23,true);
        string time=DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm",System.Globalization.CultureInfo.InvariantCulture);
        ClawTheme.Text(g,time,timeFont,look.Ink,R(18,26,410,96),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        ClawTheme.Text(g,DateTime.Now.ToString(settings.Use24h?"ddd, d MMM":"ddd, d MMM · tt",System.Globalization.CultureInfo.GetCultureInfo(L.Languages.First(l=>l.Code==L.Normalize(settings.Language)).Culture)),small,look.Muted,R(18,118,410,36),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        ClawTheme.Text(g,L.DisplayText(settings.Language,"your_little_desk_companion"),small,look.Muted,R(18,look.Cased?396:428,410,32),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        int count=settings.HomeOrder.Length,rowHeight=Math.Min(98,412/Math.Max(1,count));
        for(int i=0;i<count;i++){
            int id=settings.HomeOrder[i],y=34+i*rowHeight,x=0;
            // On a cased look each shortcut is a solid block, laid out as the display lays them.
            if(look.Cased){int step=count>4?76:89,height=count>4?70:78;y=(count>4?49:67)+i*step;x=6;ClawTheme.Fill(g,look.Surface,R(464,y,308,height));y+=(height-78)/2;}
            AppArtwork.Draw(g,id,R(466+x,y+15,46,46),look.Accent,look.WeatherApp);
            ClawTheme.Text(g,L.DisplayText(settings.Language,id switch{0=>"pc_monitor",1=>"clock",2=>"music",3=>"timer",4=>"codex",7=>"claude",8=>"shortcuts",9=>"audio",_=>"weather"}),rowFont,look.Ink,R(528+x,y+8,254-x*2,34));
            string note=id switch{0=>L.DisplayText(settings.Language,"live_pc_readings"),1=>L.DisplayText(settings.Language,"local_time"),6=>settings.City,2=>L.DisplayText(settings.Language,"spotify_playback"),3=>L.DisplayText(settings.Language,"a_countdown_for_anything"),8=>L.DisplayText(settings.Language,"your_pc_at_a_tap"),9=>L.DisplayText(settings.Language,"every_app_s_volume"),_=>L.DisplayText(settings.Language,"account_limits")};
            ClawTheme.Text(g,note,small,look.Muted,R(528+x,y+43,254-x*2,30));
        }
        if(look.Cased)PaintCase(g,look,R,800,480);
    }
    /* Cased looks: the screen sits in ClawPal's orange case, its rail carrying Apps and the Clock in the case's dark ink. */
    private void PaintCase(Graphics g,ThemeLook look,Func<int,int,int,int,Rectangle> R,int width,int height)
    {
        int frame=width>=800?8:height>=400?5:3;
        ClawTheme.Fill(g,look.Frame,R(0,height-44,width,44));ClawTheme.Fill(g,look.Frame,R(0,0,width,frame));
        ClawTheme.Fill(g,look.Frame,R(0,0,frame,height));ClawTheme.Fill(g,look.Frame,R(width-frame,0,frame,height));
        var probe=R(0,0,1000,1000);float s=probe.Width/1000f;
        using var font=new Font("Segoe UI Semibold",Math.Max(6,16*s),FontStyle.Regular,GraphicsUnit.Pixel);
        for(int i=0;i<4;i++)ClawTheme.Fill(g,look.FrameInk,R(28+i%2*7,height-25+i/2*7,4,4));
        ClawTheme.Text(g,L.DisplayText(settings.Language,"apps"),font,look.FrameInk,R(48,height-40,90,30));
        ClawTheme.Text(g,L.DisplayText(settings.Language,"clock"),font,look.FrameInk,R(width-112,height-40,100,30),TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
    }
    private void PaintCompact(Graphics g,ThemeLook look)
    {
        var r=ScreenBounds;float s=r.Width/(float)board.Width;bool tiny=board.Height<400;
        Rectangle R(int x,int y,int w,int h)=>new(r.X+(int)(x*s),r.Y+(int)(y*s),(int)(w*s),(int)(h*s));
        using var time=new Font("Segoe UI",Math.Max(6,48*s),FontStyle.Bold,GraphicsUnit.Pixel);
        using var title=new Font("Segoe UI",Math.Max(6,20*s),FontStyle.Bold,GraphicsUnit.Pixel);
        using var note=new Font("Segoe UI",Math.Max(6,16*s),FontStyle.Regular,GraphicsUnit.Pixel);
        var saved=g.Save();g.SetClip(r);ClawTheme.Fill(g,look.Background,r);
        ClawTheme.Text(g,DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm"),time,look.Ink,R(12,tiny?4:30,tiny?board.Width-120:260,64));
        ClawTheme.Text(g,DateTime.Now.ToString("ddd, d MMM"),note,look.Muted,R(12,tiny?65:96,board.Width-24,28));
        ClawTheme.Text(g,L.DisplayText(settings.Language,"your_little_desk_companion"),note,look.Muted,R(12,tiny?94:190,board.Width-24,30));
        for(int i=0;i<settings.HomeOrder.Length;i++){int y=(tiny?124:228)+i*76;if(y>board.Height-44)break;int id=settings.HomeOrder[i];
            if(look.Cased)ClawTheme.Fill(g,look.Surface,R(12,y,board.Width-24,70));
            AppArtwork.Draw(g,id,R(17,y+4,56,56),look.Accent,look.WeatherApp);
            ClawTheme.Text(g,DisplayApps.Get(id).Name,title,look.Ink,R(94,y+4,board.Width-110,28));
            ClawTheme.Text(g,id==6?settings.City:L.DisplayText(settings.Language,"waiting"),note,look.Muted,R(94,y+38,board.Width-110,24));
        }
        if(look.Cased)PaintCase(g,look,R,board.Width,board.Height);
        else{
            ClawTheme.Fill(g,look.Background,R(0,board.Height-44,board.Width,44));
            ClawTheme.Text(g,L.DisplayText(settings.Language,"apps"),note,look.Accent,R(12,board.Height-37,90,30));
            ClawTheme.Text(g,L.DisplayText(settings.Language,"clock"),note,look.Accent,R(board.Width-92,board.Height-37,80,30));
        }
        g.Restore(saved);
    }
    /* Studio's home: a desk clock (light time, the date and the day's hairline), then hairline-separated shortcuts. */
    private void PaintStudio(Graphics g,ThemeLook look)
    {
        var r=ScreenBounds;float s=r.Width/(float)board.Width;bool wide=board.Width>=800,tiny=board.Height<400;
        Rectangle R(int x,int y,int w,int h)=>new(r.X+(int)(x*s),r.Y+(int)(y*s),(int)(w*s),(int)(h*s));
        Font F(float pixels,string family="Segoe UI")=>new(family,Math.Max(6,pixels*s),FontStyle.Regular,GraphicsUnit.Pixel);
        g.SmoothingMode=SmoothingMode.AntiAlias;var saved=g.Save();g.SetClip(r);
        ClawTheme.Fill(g,look.Background,r);
        bool cased=look.Cased;
        // Cased looks set the time in the display's heavy face and the shortcuts on solid blocks, without hairlines.
        using var rule=new Pen(look.Border);using var time=cased?new Font("Segoe UI Black",Math.Max(6,(wide?118:tiny?50:90)*s),FontStyle.Regular,GraphicsUnit.Pixel):F(wide?136:tiny?54:100,"Segoe UI Light");
        using var date=F(wide?24:16);using var title=F(wide?20:18);using var note=F(wide?16:14);using var hours=F(16);using var hoursSmall=F(14);
        string language=settings.Language;
        ClawTheme.Text(g,DateTime.Now.ToString(settings.Use24h?"HH:mm":"h:mm",System.Globalization.CultureInfo.InvariantCulture),time,look.Ink,wide?R(30,113,420,190):R(6,tiny?18:40,board.Width-24,tiny?66:104));
        var today=DateTime.Now;
        ClawTheme.Text(g,$"{today.Day} {L.DisplayText(language,$"month_long_{today.Month-1}")}",date,look.Ink,wide?R(44,121,380,34):R(12,tiny?2:14,board.Width-24,26));
        PaintWeek(g,R,wide?44:12,wide?285:tiny?90:137,wide?292:board.Width-24,tiny?hoursSmall:hours,tiny?19:22,look);
        if(wide&&!cased)g.DrawLine(rule,R(441,40,0,0).Location,R(441,420,0,0).Location);
        int count=settings.HomeOrder.Length,top=wide?(count>4?49:67):tiny?124:196,step=wide?(count>4?76:89):76;
        for(int i=0;i<count;i++){int id=settings.HomeOrder[i],x=wide?464:12,y=top+i*step;if(y>board.Height-44)break;
            if(cased){ClawTheme.Fill(g,look.Surface,R(x,y,wide?308:board.Width-24,count>4||!wide?70:78));x+=6;}
            else using(var tile=ClawTheme.Round(R(x,y+2,66,66),12*s))using(var pen=new Pen(look.Border))g.DrawPath(pen,tile);
            AppArtwork.Draw(g,id,R(x+11,y+13,44,44),look.Accent,look.WeatherApp);
            ClawTheme.Text(g,DisplayApps.Get(id).Name,title,look.Ink,R(x+82,y+4,wide?(cased?214:234):board.Width-110,30));
            ClawTheme.Text(g,id==6?settings.City:L.DisplayText(language,"waiting"),note,look.Muted,R(x+82,y+38,wide?(cased?214:234):board.Width-110,26));
            if(!cased&&i<count-1)g.DrawLine(rule,R(x+82,y+(count>4?70:78)+6,0,0).Location,R(wide?780:board.Width-12,y+(count>4?70:78)+6,0,0).Location);}
        if(cased)PaintCase(g,look,R,board.Width,board.Height);
        else if(!wide){ClawTheme.Fill(g,look.Background,R(0,board.Height-44,board.Width,44));g.DrawLine(rule,R(0,board.Height-44,0,0).Location,R(board.Width,board.Height-44,0,0).Location);
            ClawTheme.Text(g,L.DisplayText(language,"apps"),note,look.Accent,R(12,board.Height-37,90,30));ClawTheme.Text(g,L.DisplayText(language,"clock"),note,look.Accent,R(board.Width-92,board.Height-37,80,30));}
        g.Restore(saved);
    }
    /* Monday-first week: today's name in ink over a short accent bar, the rest muted. */
    private void PaintWeek(Graphics g,Func<int,int,int,int,Rectangle> R,int x,int y,int width,Font font,int line,ThemeLook look)
    {
        // Justified like the display: Monday on the left margin, Sunday on the right, equal gaps between.
        int today=((int)DateTime.Now.DayOfWeek+6)%7;
        var start=R(x,y,width,line);var names=Enumerable.Range(0,7).Select(i=>L.DisplayText(settings.Language,$"weekday_{(i+1)%7}")).ToArray();
        var widths=names.Select(n=>TextRenderer.MeasureText(g,n,font,start.Size,TextFormatFlags.NoPadding).Width).ToArray();
        float gap=(start.Width-widths.Sum())/6f,left=start.X;
        using var mark=new SolidBrush(look.Accent);var bar=R(x,y+line+4,1,look.Cased?4:2);
        for(int i=0;i<7;left+=widths[i]+gap,i++){int at=i==6?start.Right-widths[i]:(int)left;
            ClawTheme.Text(g,names[i],font,i==today?look.Ink:look.Muted,new Rectangle(at,start.Y,widths[i]+2,start.Height),TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.NoPadding);
            if(i==today)g.FillRectangle(mark,new Rectangle(at,bar.Y,widths[i],Math.Max(2,bar.Height)));}
    }
    protected override void Dispose(bool disposing){if(disposing)clock.Dispose();base.Dispose(disposing);}
}
