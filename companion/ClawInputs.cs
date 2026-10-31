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
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        ClawTheme.Fill(e.Graphics,ClawTheme.Surface,new RectangleF(0,0,Width-1,Height-1),8);
        using var path=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8);
        using var pen=new Pen(ContainsFocus?ClawTheme.Accent:hover?ClawTheme.Muted:ClawTheme.Border);e.Graphics.DrawPath(pen,path);
    }
}

internal sealed class ChoiceBox : ClawButton
{
    public List<object> Items { get; } = new();
    private int selected=-1;
    private readonly ContextMenuStrip menu=new() { BackColor=ClawTheme.Surface,ForeColor=ClawTheme.Ink,ShowImageMargin=false,ShowCheckMargin=false,Padding=new Padding(5),DropShadowEnabled=false,Renderer=new ChoiceRenderer() };
    internal ContextMenuStrip SelectionMenu=>menu;
    public event EventHandler? SelectedIndexChanged;
    public int SelectedIndex {
        get=>selected>=0&&selected<Items.Count?selected:-1;
        set{selected=value;Text=SelectedItem?.ToString()??L.Text("select");SelectedIndexChanged?.Invoke(this,EventArgs.Empty);Invalidate();}
    }
    public object? SelectedItem {get=>SelectedIndex>=0?Items[selected]:null;set=>SelectedIndex=Items.IndexOf(value!);}
    public ChoiceBox()
    {
        Text=L.Text("select");Size=new(300,44);MinimumSize=new(90,40);Font=ClawTheme.Font(10);AccessibleRole=AccessibleRole.ComboBox;
        Click+=(_,_)=>OpenChoices();menu.Closed+=(_,_)=>Invalidate();
    }
    private void OpenChoices()
    {
        // Reuse through Windows' selection/close dispatch; disposal belongs to the owner.
        foreach(var item in menu.Items.Cast<ToolStripItem>().ToArray())item.Dispose();menu.Items.Clear();
        menu.Font=Font;int width=Math.Min(Width,Screen.FromControl(this).WorkingArea.Width-16);menu.MinimumSize=new(width,0);menu.MaximumSize=new(width,Math.Min(440*DeviceDpi/96,Screen.FromControl(this).WorkingArea.Height-32));int row=44*DeviceDpi/96;
        for(int i=0;i<Items.Count;i++){
            int index=i;var item=new ToolStripMenuItem(Items[i].ToString()){Checked=i==SelectedIndex,AutoSize=false,Size=new(Math.Max(100,width-10),row)};
            item.Click+=(_,_)=>SelectedIndex=index;menu.Items.Add(item);
        }
        if(Items.Count==0)menu.Items.Add(new ToolStripMenuItem(L.Text("no_choices_available")){Enabled=false,AutoSize=false,Size=new(Math.Max(100,width-10),row)});
        menu.Show(this,new Point(0,Height+4));if(SelectedIndex>=0)menu.Items[SelectedIndex].Select();Invalidate();
    }
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode is Keys.Up or Keys.Down){OpenChoices();e.Handled=e.SuppressKeyPress=true;}else base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor??ClawTheme.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        bool hot=ClientRectangle.Contains(PointToClient(MousePosition));
        ClawTheme.Fill(e.Graphics,hot&&Enabled?ClawTheme.Hover:ClawTheme.Surface,new RectangleF(0,0,Width-1,Height-1),8);
        using var outline=ClawTheme.Round(new RectangleF(.5f,.5f,Width-2,Height-2),8);
        using var border=new Pen(Enabled&&(Focused||menu.Visible)?ClawTheme.Accent:ClawTheme.Border);e.Graphics.DrawPath(border,outline);
        ClawTheme.Text(e.Graphics,Text,Font,Enabled?ClawTheme.Ink:ClawTheme.Muted,new Rectangle(14,0,Math.Max(1,Width-52),Height));
        using var pen=new Pen(Enabled?ClawTheme.Muted:ClawTheme.Border,1.6f);int x=Width-22,y=Height/2;
        e.Graphics.DrawLines(pen,new Point[]{new(x-4,y-2),new(x,y+2),new(x+4,y-2)});
    }
    protected override void Dispose(bool disposing){if(disposing)menu.Dispose();base.Dispose(disposing);}
    private sealed class ChoiceRenderer : ToolStripProfessionalRenderer
    {
        internal ChoiceRenderer():base(new ClawMenuColors()){RoundedEdges=false;}
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {if(e.Item.Selected&&e.Item.Enabled)ClawTheme.Fill(e.Graphics,ClawTheme.Hover,new RectangleF(0,1,e.Item.Width-1,e.Item.Height-2),6);}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool selected=e.Item is ToolStripMenuItem {Checked:true};
            ClawTheme.Text(e.Graphics,e.Text??"",e.TextFont!,!e.Item.Enabled?ClawTheme.Muted:selected?ClawTheme.Accent:ClawTheme.Ink,new Rectangle(12,0,e.ToolStrip!.Width-46,e.Item.Height));
            if(selected){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var pen=new Pen(ClawTheme.Accent,1.8f);int x=e.ToolStrip!.Width-24,y=e.Item.Height/2;e.Graphics.DrawLines(pen,new Point[]{new(x-4,y),new(x-1,y+3),new(x+5,y-4)});}
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e){e.ArrowColor=ClawTheme.Muted;base.OnRenderArrow(e);}
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
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;ClawTheme.Fill(e.Graphics,ClawTheme.Surface,new RectangleF(0,0,Width-1,Height-1),8);
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
