using System.Drawing.Drawing2D;
using System.Globalization;

namespace ClawPal.Companion;

// Keep the native editor (caret, selection, IME, paste and accessibility), with our own frame.
internal sealed class ClawTextField : UserControl
{
    internal TextBox Editor { get; } = new() { BorderStyle=BorderStyle.None, BackColor=ClawTheme.Surface, ForeColor=ClawTheme.Ink };
    private bool hover;
    public ClawTextField()
    {
        AutoScaleMode=AutoScaleMode.None;Height=44;Width=560;MinimumSize=new(80,40);BackColor=ClawTheme.Background;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        Editor.Font=ClawTheme.Font(11);Controls.Add(Editor);TabStop=false;
        Editor.TextChanged+=(_,_)=>base.OnTextChanged(EventArgs.Empty);
        Editor.Enter+=(_,_)=>Invalidate();Editor.Leave+=(_,_)=>Invalidate();
        Editor.MouseEnter+=(_,_)=>{hover=true;Invalidate();};Editor.MouseLeave+=(_,_)=>{hover=false;Invalidate();};
        Resize+=(_,_)=>PlaceEditor();PlaceEditor();
    }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get=>Editor.Text;set=>Editor.Text=value; }
    public string PlaceholderText {get=>Editor.PlaceholderText;set=>Editor.PlaceholderText=value;}
    public bool UseSystemPasswordChar {get=>Editor.UseSystemPasswordChar;set=>Editor.UseSystemPasswordChar=value;}
    public void Clear()=>Editor.Clear();
    private void PlaceEditor(){int inset=12*DeviceDpi/96;Editor.SetBounds(inset,(Height-Editor.PreferredHeight)/2,Math.Max(1,Width-2*inset),Editor.PreferredHeight);}
    protected override void OnEnter(EventArgs e){base.OnEnter(e);Editor.Focus();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Editor.Focus();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var field=ClawTheme.Field(this);if(Editor.BackColor!=field)Editor.BackColor=field;
        ClawTheme.Fill(e.Graphics,field,new RectangleF(0,0,Width-1,Height-1),8);
        using var path=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8);
        using var pen=new Pen(ContainsFocus?ClawTheme.Accent:hover?ClawTheme.Muted:ClawTheme.Border);e.Graphics.DrawPath(pen,path);
    }
}

// A field that opens its own scrolling list: the wheel and a draggable bar move through long
// lists, typing jumps to a match, and clicking the field again closes it.
internal sealed class ChoiceBox : ClawButton
{
    public List<object> Items { get; } = new();
    private int selected=-1;
    private readonly ChoiceList list;
    private readonly ToolStripControlHost host;
    private readonly ToolStripDropDown popup;
    private long closedAt;
    private bool closedByOwnClick;
    internal ToolStripDropDown SelectionMenu=>popup;
    internal ChoiceList SelectionList=>list;
    public event EventHandler? SelectedIndexChanged;
    public int SelectedIndex {
        get=>selected>=0&&selected<Items.Count?selected:-1;
        set{selected=value;Text=SelectedItem?.ToString()??L.Text("select");SelectedIndexChanged?.Invoke(this,EventArgs.Empty);Invalidate();}
    }
    public object? SelectedItem {get=>SelectedIndex>=0?Items[selected]:null;set=>SelectedIndex=Items.IndexOf(value!);}
    public ChoiceBox()
    {
        Text=L.Text("select");Size=new(300,44);MinimumSize=new(90,40);Font=ClawTheme.Font(10);AccessibleRole=AccessibleRole.ComboBox;
        list=new ChoiceList{Font=Font};
        host=new ToolStripControlHost(list){Margin=Padding.Empty,Padding=Padding.Empty,AutoSize=false};
        popup=new ToolStripDropDown{Padding=Padding.Empty,AutoSize=false,DropShadowEnabled=false,Renderer=new PopupRenderer(),BackColor=ClawTheme.Surface};
        popup.Items.Add(host);
        popup.Closed+=(_,e)=>{
            closedAt=Environment.TickCount64;
            // The press that dismissed the list lands on this field next; it must not reopen it.
            closedByOwnClick=e.CloseReason==ToolStripDropDownCloseReason.AppClicked&&IsHandleCreated&&ClientRectangle.Contains(PointToClient(MousePosition));
            Invalidate();
        };
        list.Chosen+=index=>{popup.Close(ToolStripDropDownCloseReason.ItemClicked);if(Items.Count>index)SelectedIndex=index;if(CanFocus)Focus();};
        list.Cancelled+=()=>popup.Close(ToolStripDropDownCloseReason.Keyboard);
        Click+=(_,_)=>Toggle();
    }
    private void Toggle()
    {
        if(popup.Visible){popup.Close(ToolStripDropDownCloseReason.ItemClicked);return;}
        if(closedByOwnClick&&Environment.TickCount64-closedAt<500){closedByOwnClick=false;return;}
        OpenChoices();
    }
    private void OpenChoices()
    {
        var area=Screen.FromControl(this).WorkingArea;
        int row=40*DeviceDpi/96,pad=5*DeviceDpi/96,width=Math.Min(Width,area.Width-16);
        int rows=Math.Clamp(Items.Count,1,8),height=rows*row+2*pad;
        list.Font=Font;list.Reset(Items.Select(i=>i.ToString()??"").ToArray(),SelectedIndex,row,pad,L.Text("no_choices_available"));
        list.Size=new(width,height);host.Size=list.Size;popup.Size=list.Size;
        using(var shape=ClawTheme.Round(new RectangleF(0,0,width,height),8))popup.Region=new Region(shape);
        // Opens upwards when the screen has no room below the field.
        bool above=PointToScreen(new Point(0,Height+4+height)).Y>area.Bottom&&PointToScreen(Point.Empty).Y-height-4>=area.Top;
        popup.Show(this,new Point(0,above?-height-4:Height+4));list.Focus();list.EnsureVisible(SelectedIndex);Invalidate();
    }
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode is Keys.Up or Keys.Down){if(!popup.Visible)OpenChoices();e.Handled=e.SuppressKeyPress=true;}else base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor??ClawTheme.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        bool hot=ClientRectangle.Contains(PointToClient(MousePosition)),open=popup.Visible;
        ClawTheme.Fill(e.Graphics,(hot||open)&&Enabled?ClawTheme.Hover:ClawTheme.Field(this),new RectangleF(0,0,Width-1,Height-1),8);
        using var outline=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8);
        using var border=new Pen(Enabled&&(Focused||open)?ClawTheme.Accent:ClawTheme.Border);e.Graphics.DrawPath(border,outline);
        ClawTheme.Text(e.Graphics,Text,Font,Enabled?ClawTheme.Ink:ClawTheme.Muted,new Rectangle(14,0,Math.Max(1,Width-52),Height));
        using var pen=new Pen(Enabled?ClawTheme.Muted:ClawTheme.Border,1.6f);int x=Width-22,y=Height/2,d=open?-1:1;
        e.Graphics.DrawLines(pen,new Point[]{new(x-4,y-2*d),new(x,y+2*d),new(x+4,y-2*d)});
    }
    protected override void Dispose(bool disposing){if(disposing){popup.Dispose();list.Dispose();}base.Dispose(disposing);}
    private sealed class PopupRenderer : ToolStripProfessionalRenderer
    {
        internal PopupRenderer():base(new ClawMenuColors()){RoundedEdges=false;}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e){}
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){e.Graphics.Clear(ClawTheme.Surface);}
    }
}

internal sealed class ChoiceList : Control
{
    private string[] items=[];
    private string empty="";
    private int selected=-1,hover=-1,row=40,pad=5,dragFrom=-1;
    private float offset,dragOffset;
    internal event Action<int>? Chosen;
    internal event Action? Cancelled;
    internal int Count=>items.Length;
    internal int Hover=>hover;
    internal int ScrollOffset=>(int)offset;
    internal int MaximumOffset=>Math.Max(0,items.Length*row-(Height-2*pad));
    internal string Placeholder=>items.Length==0?empty:"";
    internal ChoiceList()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.Selectable,true);
        TabStop=true;BackColor=ClawTheme.Surface;AccessibleRole=AccessibleRole.List;
    }
    internal void Reset(string[] values,int chosen,int rowHeight,int padding,string placeholder)
    {
        items=values;selected=chosen;hover=chosen;row=rowHeight;pad=padding;empty=placeholder;offset=0;dragFrom=-1;BackColor=ClawTheme.Surface;Invalidate();
    }
    private bool Scrolls=>MaximumOffset>0;
    private Rectangle Track=>new(Width-pad-6,pad+2,4,Height-2*pad-4);
    private Rectangle Thumb{get{var t=Track;int view=Height-2*pad,content=Math.Max(1,items.Length*row);int h=Math.Max(24,t.Height*view/content);
        int y=t.Top+(int)((t.Height-h)*(MaximumOffset==0?0:offset/MaximumOffset));return new(t.Left-2,y,t.Width+4,h);}}
    internal Rectangle RowBounds(int index)=>new(pad,pad+index*row-(int)offset,Width-2*pad-(Scrolls?12:0),row);
    private int IndexAt(Point p){if(p.Y<pad||p.Y>=Height-pad||p.X>=Width-pad-(Scrolls?12:0))return -1;int i=(int)((p.Y-pad+offset)/row);return i>=0&&i<items.Length?i:-1;}
    private void ScrollTo(float value){offset=Math.Clamp(value,0,MaximumOffset);Invalidate();}
    internal void EnsureVisible(int index)
    {
        if(index<0||index>=items.Length)return;int top=index*row,view=Height-2*pad;
        if(top<offset)ScrollTo(top);else if(top+row>offset+view)ScrollTo(top+row-view);
    }
    private void HoverAt(int index){if(items.Length==0)return;hover=Math.Clamp(index,0,items.Length-1);EnsureVisible(hover);Invalidate();}
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int lines=SystemInformation.MouseWheelScrollLines;if(lines<=0)lines=3;
        ScrollTo(offset-e.Delta/120f*row*Math.Min(lines,4));hover=IndexAt(e.Location);
        if(e is HandledMouseEventArgs handled)handled.Handled=true;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||!Scrolls||e.X<Width-pad-14)return;
        var thumb=Thumb;
        // Pressing the track jumps a page; the thumb itself follows the pointer.
        if(e.Y<thumb.Top)ScrollTo(offset-(Height-2*pad));else if(e.Y>thumb.Bottom)ScrollTo(offset+(Height-2*pad));
        else{dragFrom=e.Y;dragOffset=offset;Capture=true;}
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(dragFrom>=0){var t=Track;float travel=Math.Max(1,t.Height-Thumb.Height);ScrollTo(dragOffset+(e.Y-dragFrom)*MaximumOffset/travel);return;}
        int next=IndexAt(e.Location);if(next!=hover){hover=next;Invalidate();}
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(dragFrom>=0){dragFrom=-1;Capture=false;return;}
        if(e.Button==MouseButtons.Left&&IndexAt(e.Location) is var index and >=0)Chosen?.Invoke(index);
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(dragFrom<0){hover=-1;Invalidate();}}
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Enter or Keys.Escape||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page=Math.Max(1,(Height-2*pad)/row-1),from=hover>=0?hover:selected;
        switch(e.KeyCode){
            case Keys.Down:HoverAt(from+1);break;case Keys.Up:HoverAt(from<0?0:from-1);break;
            case Keys.PageDown:HoverAt(from+page);break;case Keys.PageUp:HoverAt(from-page);break;
            case Keys.Home:HoverAt(0);break;case Keys.End:HoverAt(items.Length-1);break;
            case Keys.Enter:if(hover>=0)Chosen?.Invoke(hover);break;case Keys.Escape:Cancelled?.Invoke();break;
            default:base.OnKeyDown(e);return;
        }
        e.Handled=e.SuppressKeyPress=true;
    }
    /* Typing a letter jumps to the next entry that starts with it. */
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);if(char.IsControl(e.KeyChar)||items.Length==0)return;
        int start=hover>=0?hover:selected;
        for(int step=1;step<=items.Length;step++){int i=((start<0?-1:start)+step)%items.Length;
            if(items[i].StartsWith(e.KeyChar.ToString(),StringComparison.CurrentCultureIgnoreCase)){HoverAt(i);e.Handled=true;return;}}
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(ClawTheme.Surface);
        using(var outline=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8))using(var border=new Pen(ClawTheme.Border))g.DrawPath(border,outline);
        var view=new Rectangle(pad,pad,Width-2*pad,Height-2*pad);g.SetClip(view);
        if(items.Length==0)ClawTheme.Text(g,empty,Font,ClawTheme.Muted,new Rectangle(pad+12,pad,Width-2*pad-24,row));
        int first=Math.Max(0,(int)(offset/row)),last=Math.Min(items.Length-1,(int)((offset+view.Height)/row));
        for(int i=first;i<=last;i++){
            var r=RowBounds(i);bool chosen=i==selected;
            if(i==hover)ClawTheme.Fill(g,ClawTheme.Hover,new RectangleF(r.X,r.Y+1,r.Width,r.Height-2),6);
            ClawTheme.Text(g,items[i],Font,chosen?ClawTheme.Accent:ClawTheme.Ink,new Rectangle(r.X+12,r.Y,r.Width-(chosen?44:24),r.Height));
            if(chosen){using var pen=new Pen(ClawTheme.Accent,1.8f);int x=r.Right-20,y=r.Y+r.Height/2;g.DrawLines(pen,new Point[]{new(x-5,y),new(x-2,y+3),new(x+4,y-4)});}
        }
        g.ResetClip();
        if(Scrolls){ClawTheme.Fill(g,ClawTheme.Track,Track,2);var thumb=Thumb;ClawTheme.Fill(g,dragFrom>=0?ClawTheme.Muted:ClawTheme.Thumb,new RectangleF(thumb.X+2,thumb.Y,thumb.Width-4,thumb.Height),2);}
    }
}

internal enum ClawNumberFormat { Hour, Duration }

// Editable time with small step buttons, instead of forcing users through a list of numbers.
internal sealed class ClawTimeInput : UserControl
{
    private readonly TextBox editor=new(){BorderStyle=BorderStyle.None,BackColor=ClawTheme.Surface,ForeColor=ClawTheme.Ink,TextAlign=HorizontalAlignment.Center};
    private readonly ClawButton minus=new(){Text="−",Width=40},plus=new(){Text="+",Width=40};
    private int value;
    internal int Minimum {get;set;}
    internal int Maximum {get;set;}=23;
    internal int Step {get;set;}=1;
    internal ClawNumberFormat Format {get;set;}
    internal event EventHandler? ValueChanged;
    internal int Value {get=>value;set{int next=Math.Clamp(value,Minimum,Maximum);bool changed=this.value!=next;this.value=next;FormatText();if(changed)ValueChanged?.Invoke(this,EventArgs.Empty);}}
    internal TextBox Editor=>editor;
    internal ClawTimeInput()
    {
        AutoScaleMode=AutoScaleMode.None;Size=new(220,48);MinimumSize=new(140,44);TabStop=false;DoubleBuffered=true;BackColor=ClawTheme.Surface;
        editor.Font=ClawTheme.Font(13);editor.AccessibleName=L.Text("time_type_a_value_or_use_the_decrease_and");
        minus.AccessibleName=L.Text("decrease_time");plus.AccessibleName=L.Text("increase_time");
        minus.Click+=(_,_)=>Value-=Step;plus.Click+=(_,_)=>Value+=Step;
        Controls.AddRange([minus,editor,plus]);editor.KeyDown+=(_,e)=>{
            if(e.KeyCode==Keys.Up){Value+=Step;e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Down){Value-=Step;e.SuppressKeyPress=true;}
            else if(e.KeyCode is Keys.Enter or Keys.Escape){if(e.KeyCode==Keys.Enter)Commit();else FormatText();e.SuppressKeyPress=true;}
        };
        editor.TextChanged+=(_,_)=>{if(TryParse(editor.Text,out int next)&&next!=value){value=next;minus.Enabled=Enabled&&value>Minimum;plus.Enabled=Enabled&&value<Maximum;ValueChanged?.Invoke(this,EventArgs.Empty);}Invalidate();};
        editor.Leave+=(_,_)=>Commit();editor.Enter+=(_,_)=>{editor.SelectAll();Invalidate();};
        Resize+=(_,_)=>LayoutInput();LayoutInput();FormatText();
    }
    private bool TryParse(string text,out int result)
    {
        result=0;var parts=text.Trim().Split(':');
        if(parts.Length>2||!int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int first))return false;
        int second=0;if(parts.Length==2&&(!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out second)||second is <0 or >59))return false;
        if(Format==ClawNumberFormat.Hour){if(second!=0)return false;result=first;}
        else {if(first>60)return false;result=first*60+second;}
        return result>=Minimum&&result<=Maximum;
    }
    private void Commit(){if(TryParse(editor.Text,out int next))Value=next;else FormatText();Invalidate();}
    private void FormatText(){string text=Format==ClawNumberFormat.Hour?$"{value:00}:00":$"{value/60}:{value%60:00}";if(editor.Text!=text)editor.Text=text;minus.Enabled=Enabled&&value>Minimum;plus.Enabled=Enabled&&value<Maximum;Invalidate();}
    private void LayoutInput(){int step=40*DeviceDpi/96;minus.SetBounds(4,4,step,Height-8);plus.SetBounds(Width-step-4,4,step,Height-8);editor.SetBounds(step+10,(Height-editor.PreferredHeight)/2,Math.Max(1,Width-2*step-20),editor.PreferredHeight);}
    protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);FormatText();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var field=ClawTheme.Field(this);if(editor.BackColor!=field){editor.BackColor=field;BackColor=field;}
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.Clear(Parent?.BackColor??ClawTheme.Background);ClawTheme.Fill(e.Graphics,field,new RectangleF(0,0,Width-1,Height-1),8);
        using var path=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8);using var pen=new Pen(ContainsFocus?ClawTheme.Accent:ClawTheme.Border);e.Graphics.DrawPath(pen,path);
    }
}

internal sealed class ClawSlider : Control
{
    private int value=40;
    internal int Minimum {get;set;}=5;
    internal int Maximum {get;set;}=90;
    internal event EventHandler? ValueChanged;
    internal int Value {get=>value;set{int next=Math.Clamp(value,Minimum,Maximum);if(this.value==next)return;this.value=next;Invalidate();ValueChanged?.Invoke(this,EventArgs.Empty);AccessibilityNotifyClients(AccessibleEvents.ValueChange,-1);}}
    internal ClawSlider(){Size=new(320,48);TabStop=true;DoubleBuffered=true;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.Slider;AccessibleName=L.Text("dimmed_brightness_percent");Font=ClawTheme.Font(12,true);}
    private Rectangle Rail=>new(12,Height/2-2,Math.Max(1,Width-94),4);
    private void PointValue(int x)=>Value=Minimum+(int)Math.Round(Math.Clamp((x-Rail.Left)/(double)Rail.Width,0,1)*(Maximum-Minimum));
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();Capture=true;PointValue(e.X);}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(Capture)PointValue(e.X);}
    protected override void OnMouseUp(MouseEventArgs e){Capture=false;base.OnMouseUp(e);}
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step=e.Shift?10:1;
        switch(e.KeyCode){case Keys.Right:case Keys.Up:Value+=step;break;case Keys.Left:case Keys.Down:Value-=step;break;case Keys.Home:Value=Minimum;break;case Keys.End:Value=Maximum;break;case Keys.PageUp:Value+=10;break;case Keys.PageDown:Value-=10;break;default:base.OnKeyDown(e);return;}
        e.Handled=e.SuppressKeyPress=true;
    }
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var rail=Rail;int x=rail.Left+(int)(rail.Width*(value-Minimum)/(float)Math.Max(1,Maximum-Minimum));
        ClawTheme.Fill(e.Graphics,ClawTheme.Border,rail,2);if(x>rail.Left+4)ClawTheme.Fill(e.Graphics,Enabled?ClawTheme.Accent:ClawTheme.Muted,new RectangleF(rail.Left,rail.Top,x-rail.Left,rail.Height),2);
        using var knob=new SolidBrush(Enabled?ClawTheme.Accent:ClawTheme.Muted);e.Graphics.FillEllipse(knob,x-8,Height/2-8,16,16);
        if(Focused&&ShowFocusCues){using var focus=new Pen(ClawTheme.Ink);e.Graphics.DrawEllipse(focus,x-12,Height/2-12,24,24);}
        ClawTheme.Text(e.Graphics,$"{Value}%",Font,Enabled?ClawTheme.Ink:ClawTheme.Muted,new Rectangle(Width-68,0,68,Height),TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
    }
    protected override AccessibleObject CreateAccessibilityInstance()=>new SliderAccessibility(this);
    private sealed class SliderAccessibility(ClawSlider owner):ControlAccessibleObject(owner)
    {
        public override string? Value {get=>$"{owner.Value}%";set{if(int.TryParse(value?.TrimEnd('%'),out int number))owner.Value=number;}}
        public override string? DefaultAction=>L.Text("increase_brightness");
        public override void DoDefaultAction(){if(owner.Enabled)owner.Value++;}
    }
}
