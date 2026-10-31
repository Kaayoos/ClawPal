using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClawPal.Companion;

internal static class ClawTheme
{
    /* The companion follows the display's look: the crab's warm black and orange, or Studio's graphite and accent. */
    internal static ThemeLook Look { get; private set; } = Themes.Get(Themes.Default,Themes.DefaultAccent);
    internal static event Action? Changed;
    internal static Color Background=>Look.Background;
    internal static Color Surface=>Look.Surface;
    internal static Color Hover=>Look.Hover;
    internal static Color Border=>Look.Border;
    internal static Color Ink=>Look.Ink;
    internal static Color Muted=>Look.Muted;
    internal static Color Accent=>Look.Accent;
    internal static Color WeatherApp=>Look.WeatherApp;
    internal static Color AccentHover=>Mix(Accent,Color.White,.15f);
    internal static Color AccentPressed=>Mix(Accent,Color.Black,.13f);
    internal static Color Track=>Mix(Border,Ink,.02f);
    internal static Color TrackHover=>Mix(Border,Ink,.1f);
    internal static Color Thumb=>Mix(Border,Ink,.25f);
    internal static bool Mascot=>Look.Mascot;
    internal static Color Mix(Color a,Color b,float amount)=>Color.FromArgb((int)(a.R+(b.R-a.R)*amount),(int)(a.G+(b.G-a.G)*amount),(int)(a.B+(b.B-a.B)*amount));
    /* Switches every open window at once; colours assigned from the old look move to the matching role. */
    internal static void Use(string? theme,string? accent)
    {
        var next=Themes.Get(theme,accent);if(next==Look)return;var old=Look;Look=next;
        var roles=new[]{(old.Background,next.Background),(old.Surface,next.Surface),(old.Hover,next.Hover),(old.Border,next.Border),(old.Ink,next.Ink),(old.Muted,next.Muted),(old.Accent,next.Accent)};
        void Recolor(Control control){
            foreach(var (from,to) in roles){if(control.BackColor.ToArgb()==from.ToArgb()&&control.BackColor!=Color.Transparent){control.BackColor=to;break;}}
            foreach(var (from,to) in roles){if(control.ForeColor.ToArgb()==from.ToArgb()){control.ForeColor=to;break;}}
            foreach(Control child in control.Controls)Recolor(child);
        }
        foreach(Form form in Application.OpenForms.Cast<Form>().ToArray()){Recolor(form);form.Invalidate(true);}
        L.SetVariant(next.Mascot?null:next.Id);Changed?.Invoke();
    }
    internal static Font Font(float size=10, bool bold=false) => new("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular);
    internal static GraphicsPath Round(RectangleF r,float radius=10)
    {
        var path=new GraphicsPath();if(radius<=0){path.AddRectangle(r);return path;}float d=radius*2;
        path.AddArc(r.X,r.Y,d,d,180,90);path.AddArc(r.Right-d,r.Y,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.X,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
    }
    internal static void Fill(Graphics g,Color color,RectangleF r,float radius=10) { using var p=Round(r,radius);using var b=new SolidBrush(color);g.FillPath(b,p); }
    internal static void Text(Graphics g,string text,Font font,Color color,Rectangle bounds,TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis) => TextRenderer.DrawText(g,text,font,bounds,color,flags|TextFormatFlags.NoPrefix);
    internal static Label Label(string text,int width=600,float size=10,bool bold=false,int height=30) => new() { Text=text,Width=width,Height=height,ForeColor=bold?Ink:Muted,Font=Font(size,bold),UseMnemonic=false };
    internal static ClawTextField Input(string placeholder="") => new() { PlaceholderText=placeholder };
    internal static void Apply(Form form)
    {
        foreach(Control child in form.Controls) ApplyControl(child);
    }
    private static void ApplyControl(Control c)
    {
        if(c is Label label)label.UseMnemonic=false;
        if(c is Button b){b.UseMnemonic=false;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Border;}
        foreach(Control child in c.Controls)ApplyControl(child);
    }
}

internal sealed class ClawMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground=>ClawTheme.Surface;
    public override Color ImageMarginGradientBegin=>ClawTheme.Surface;
    public override Color ImageMarginGradientMiddle=>ClawTheme.Surface;
    public override Color ImageMarginGradientEnd=>ClawTheme.Surface;
    public override Color MenuItemSelected=>ClawTheme.Hover;
    public override Color MenuItemBorder=>ClawTheme.Accent;
    public override Color MenuBorder=>ClawTheme.Border;
    public override Color SeparatorDark=>ClawTheme.Border;
    public override Color SeparatorLight=>ClawTheme.Border;
}

internal class ClawButton : Button
{
    internal bool Primary { get; set; }
    internal bool Selected { get; set; }
    private bool hovered, pressed;
    public ClawButton() { FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;UseMnemonic=false;Cursor=Cursors.Hand;Font=ClawTheme.Font(10,true);Size=new(155,42);SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true); }
    protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hovered=pressed=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.Clear(Parent?.BackColor??ClawTheme.Background);
        var fill=!Enabled?ClawTheme.Surface:Primary?(pressed?ClawTheme.AccentPressed:hovered?ClawTheme.AccentHover:ClawTheme.Accent):Selected||hovered?ClawTheme.Hover:ClawTheme.Surface;
        ClawTheme.Fill(e.Graphics,fill,new RectangleF(0,0,Width-1,Height-1),9);
        var ink=!Enabled?ClawTheme.Muted:Primary?ClawTheme.Background:Selected?ClawTheme.Accent:ClawTheme.Ink;
        ClawTheme.Text(e.Graphics,Text,Font,ink,ClientRectangle,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues){using var p=ClawTheme.Round(new RectangleF(2,2,Width-5,Height-5),8);using var pen=new Pen(ClawTheme.Accent);e.Graphics.DrawPath(pen,p);}
    }
}

internal sealed class ClawSwitch : CheckBox
{
    private readonly System.Windows.Forms.Timer animation=new(){Interval=16};
    private float amount;
    private bool hover;
    public ClawSwitch()
    {
        AutoSize=false;Size=new(600,42);Font=ClawTheme.Font(10);ForeColor=ClawTheme.Ink;Cursor=Cursors.Hand;
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);
        animation.Tick+=(_,_)=>{float target=Checked?1:0;amount+=(target-amount)*.28f;if(Math.Abs(target-amount)<.015f){amount=target;animation.Stop();}Invalidate();};
    }
    protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);if(SystemInformation.IsMenuAnimationEnabled&&IsHandleCreated)animation.Start();else amount=Checked?1:0;Invalidate();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);amount=Checked?1:0;}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.Clear(Parent?.BackColor??ClawTheme.Background);
        ClawTheme.Text(e.Graphics,Text,Font,Enabled?ClawTheme.Ink:ClawTheme.Muted,new Rectangle(0,0,Width-68,Height));
        int left=Width-48,top=(Height-26)/2;
        ClawTheme.Fill(e.Graphics,Enabled&&Checked?ClawTheme.Accent:hover?ClawTheme.TrackHover:ClawTheme.Track,new RectangleF(left,top,46,26),13);
        using var brush=new SolidBrush(Enabled&&Checked?ClawTheme.Background:ClawTheme.Ink);
        e.Graphics.FillEllipse(brush,left+3+20*amount,top+3,20,20);
        if(Focused&&ShowFocusCues){using var pen=new Pen(ClawTheme.Accent);e.Graphics.DrawRectangle(pen,1,1,Width-3,Height-3);}
    }
    protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
}

internal class ClawWindow : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int value,int size);
    private readonly Panel chrome=new(){Height=42,BackColor=ClawTheme.Background,Location=new Point(1,1),Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
    public ClawWindow()
    {
        Padding=new Padding(1,44,1,1);Resize+=(_,_)=>chrome.Width=ClientSize.Width-2;FormBorderStyle=FormBorderStyle.None;BackColor=ClawTheme.Background;ForeColor=ClawTheme.Ink;Font=ClawTheme.Font();DoubleBuffered=true;
        StartPosition=FormStartPosition.CenterScreen;MinimumSize=new(960,680);
        chrome.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}};
        chrome.DoubleClick+=(_,_)=>ToggleMaximize();
        var caption=ClawTheme.Label(L.Text("clawpal"),200,9,false,44);caption.Left=20;caption.TextAlign=ContentAlignment.MiddleLeft;caption.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}};chrome.Controls.Add(caption);
        for(int i=0;i<3;i++){
            int action=i;var b=new WindowAction(i){Width=42,Height=32,Top=6,Anchor=AnchorStyles.Top|AnchorStyles.Right};
            b.Click+=(_,_)=>{if(action==0)WindowState=FormWindowState.Minimized;else if(action==1)ToggleMaximize();else Close();};
            chrome.Controls.Add(b);chrome.Resize+=(_,_)=>b.Left=chrome.Width-8-(3-action)*44;
        }
        Controls.Add(chrome);
    }
    private void ToggleMaximize(){MaximizedBounds=Screen.FromHandle(Handle).WorkingArea;WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);MaximizedBounds=Screen.FromHandle(Handle).WorkingArea;int dark=1;DwmSetWindowAttribute(Handle,20,ref dark,4);int corner=2;DwmSetWindowAttribute(Handle,33,ref corner,4);}
    protected override void OnShown(EventArgs e){chrome.BringToFront();ClawTheme.Apply(this);base.OnShown(e);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var pen=new Pen(ClawTheme.Border);e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);}
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if(m.Msg==0x84 && WindowState==FormWindowState.Normal){var p=PointToClient(new Point(unchecked((short)(long)m.LParam),unchecked((short)((long)m.LParam>>16))));int x=p.X,y=p.Y,b=6;
            m.Result=(IntPtr)(x<b?(y<b?13:y>Height-b?16:10):x>Width-b?(y<b?14:y>Height-b?17:11):y<b?12:y>Height-b?15:(int)m.Result);}
    }
    private sealed class WindowAction(int action) : ClawButton
    {
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var pen=new Pen(ClawTheme.Muted,1.4f);int x=Width/2,y=Height/2;if(action==0)e.Graphics.DrawLine(pen,x-5,y+3,x+5,y+3);else if(action==1)e.Graphics.DrawRectangle(pen,x-5,y-5,10,10);else{e.Graphics.DrawLine(pen,x-4,y-4,x+4,y+4);e.Graphics.DrawLine(pen,x+4,y-4,x-4,y+4);}}
        protected override void OnCreateControl(){base.OnCreateControl();AccessibleName=action==0?L.Text("minimize"):action==1?L.Text("maximize_or_restore"):L.Text("close_to_tray");}
    }
}

