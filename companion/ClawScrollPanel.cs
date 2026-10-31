namespace ClawPal.Companion;

// A clipped content canvas and a permanently reserved gutter. No WS_*SCROLL styles
// are ever created, so page changes, focus and resize cannot introduce native bars.
internal sealed class ClawScrollPanel : Panel, IMessageFilter
{
    private readonly Panel viewport=new();
    internal Panel Content {get;}=new();
    private int offset,extent,grip;
    private bool arranging,dragging;
    internal int MaximumContentWidth {get;set;}=1060;
    internal int ScrollOffset=>offset;
    internal int MaximumOffset=>Math.Max(0,extent-ClientSize.Height);
    private int Gutter=>16*DeviceDpi/96;
    public ClawScrollPanel()
    {
        AutoScroll=false;TabStop=true;DoubleBuffered=true;viewport.AutoScroll=Content.AutoScroll=false;
        base.Controls.Add(viewport);viewport.Controls.Add(Content);
        Content.ControlAdded+=(_,e)=>{Watch(e.Control!);Arrange();};Content.ControlRemoved+=(_,_)=>Arrange();
        Content.Layout+=(_,_)=>Arrange();
        Application.AddMessageFilter(this);AccessibleName="Scrollable settings";
    }
    private void Watch(Control control)
    {
        control.Enter+=ChildEntered;control.ControlAdded+=ChildAdded;
        control.SizeChanged+=ChildGeometryChanged;control.VisibleChanged+=ChildGeometryChanged;control.Layout+=ChildLayout;
        foreach(Control child in control.Controls)Watch(child);
    }
    private void ChildAdded(object? sender,ControlEventArgs e){Watch(e.Control!);Arrange();}
    private void ChildGeometryChanged(object? sender,EventArgs e)=>Arrange();
    private void ChildLayout(object? sender,LayoutEventArgs e)=>Arrange();
    private void ChildEntered(object? sender,EventArgs e)
    {
        if(sender is not Control control||!control.IsHandleCreated||!IsHandleCreated)return;
        Rectangle rect=RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        // Large composite containers should not scroll merely because a child gained focus.
        if(control.ContainsFocus&&!control.Focused)return;
        if(rect.Bottom>Height-12)ScrollTo(offset+rect.Bottom-Height+12);
        else if(rect.Top<12)ScrollTo(offset+rect.Top-12);
    }
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);Arrange();}
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);Arrange();}
    private void Arrange()
    {
        if(arranging||viewport is null||Content is null||IsDisposed)return;arranging=true;
        try{
            int width=Math.Max(1,ClientSize.Width-Padding.Horizontal-Gutter);
            if(MaximumContentWidth>0)width=Math.Min(width,MaximumContentWidth*DeviceDpi/96);
            viewport.SetBounds(Padding.Left,0,width,Math.Max(1,ClientSize.Height));
            Content.Width=width;
            extent=Padding.Vertical+Content.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>c.Bottom).DefaultIfEmpty(0).Max();
            offset=Math.Clamp(offset,0,MaximumOffset);
            Content.SetBounds(0,Padding.Top-offset,width,Math.Max(1,extent-Padding.Vertical));
            Invalidate();
        }finally{arranging=false;}
    }
    internal void ScrollTo(int position){offset=Math.Clamp(position,0,MaximumOffset);Content.Top=Padding.Top-offset;Invalidate();}
    internal void ResetScroll()=>ScrollTo(0);
    public bool PreFilterMessage(ref Message m)
    {
        if(m.Msg!=0x20A||!Visible||!Enabled||IsDisposed)return false;
        var target=Control.FromChildHandle(m.HWnd);
        if(target is null||(target!=this&&!Contains(target)))return false;
        int delta=unchecked((short)((long)m.WParam>>16));
        int lines=SystemInformation.MouseWheelScrollLines;
        ScrollTo(offset-delta/120*(lines<0?Height:Math.Max(1,lines)*20*DeviceDpi/96));return true;
    }
    protected override void OnMouseWheel(MouseEventArgs e){ScrollTo(offset-e.Delta/120*60*DeviceDpi/96);if(e is HandledMouseEventArgs handled)handled.Handled=true;}
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.PageDown or Keys.PageUp or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch(e.KeyCode){case Keys.PageDown:ScrollTo(offset+Height-40);break;case Keys.PageUp:ScrollTo(offset-Height+40);break;case Keys.Home:ScrollTo(0);break;case Keys.End:ScrollTo(MaximumOffset);break;default:base.OnKeyDown(e);return;}e.Handled=true;
    }
    private Rectangle Thumb
    {
        get{int track=Math.Max(1,Height-8),h=Math.Min(track,Math.Max(32,track*Height/Math.Max(1,extent)));return new(Width-Gutter+4,4+(MaximumOffset>0?offset*(track-h)/MaximumOffset:0),6,h);}
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(MaximumOffset==0)return;e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        ClawTheme.Fill(e.Graphics,dragging?ClawTheme.Accent:ClawTheme.Thumb,Thumb,3);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||e.X<Width-Gutter||MaximumOffset==0)return;
        if(Thumb.Contains(e.Location)){dragging=Capture=true;grip=e.Y-Thumb.Top;}
        else ScrollTo(offset+(e.Y<Thumb.Top?-1:1)*Math.Max(1,Height-40));
    }
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)ScrollTo((int)(Math.Clamp((e.Y-grip-4)/(float)Math.Max(1,Height-8-Thumb.Height),0,1)*MaximumOffset));}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=Capture=false;Invalidate();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
    protected override void Dispose(bool disposing){if(disposing)Application.RemoveMessageFilter(this);base.Dispose(disposing);}
}
