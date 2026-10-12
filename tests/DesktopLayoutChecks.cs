using ClawPal.Companion;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class DesktopLayoutChecks
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
    private static IEnumerable<Control> Descendants(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    internal static int Run()
    {
        int passed=0;Exception? failure=null;
        var thread=new Thread(()=>{
            try{
                void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS: "+name);}
                using var connection=new Connection(new Configuration());
                using var form=new SettingsForm(new Configuration{DisplaySettings=new DeviceSettings()},connection,true){ShowInTaskbar=false};
                form.Show();Application.DoEvents();
                void Page(string name){Descendants(form).OfType<ClawButton>().Single(c=>c.Text==name).PerformClick();}
                void NoNativeBars(){Check(Descendants(form).OfType<Panel>().All(p=>(GetWindowLong(p.Handle,-16)&0x00300000)==0),"No native scrollbar styles during navigation and focus");}
                var scroll=Descendants(form).OfType<ClawScrollPanel>().Single();
                foreach(var size in new[]{new Size(960,680),new Size(1120,790),new Size(1600,900),new Size(1040,730)}){
                    form.ClientSize=size;Page("Your Home");Application.DoEvents();
                    var preview=Descendants(form).OfType<HomePreview>().Single();var list=Descendants(form).OfType<HomeOrderList>().Single();
                    Check(Math.Abs(preview.ScreenBounds.Width*3/5-preview.ScreenBounds.Height)<=1,"Home preview retains 800:480 aspect ratio after resizing");
                    Check(preview.ScreenBounds.Contains(preview.CrabBounds),"Crab stays within the preview when the window changes size");
                    Check(!preview.Bounds.IntersectsWith(list.Bounds),"Preview and shortcut editor never overlap");
                    Check(preview.Right<=scroll.Content.Width&&list.Right<=scroll.Content.Width,"Home content fits the viewport at every supported width");
                    NoNativeBars();
                }
                form.ClientSize=new(960,680);
                foreach(string name in new[]{"Apps","Clock & weather","PC companion","Device","Updates","Screen"}){
                    Page(name);int left=scroll.Content.Left,width=scroll.Content.Width;NoNativeBars();Application.DoEvents();
                    scroll.ScrollTo(scroll.MaximumOffset);Application.DoEvents();
                    Check(scroll.Content.Left==left&&scroll.Content.Width==width,"Scrolling preserves page width and horizontal position");
                    NoNativeBars();scroll.ResetScroll();
                }
                Page("Screen");Application.DoEvents();
                var slider=Descendants(form).OfType<ClawSlider>().Single();slider.Focus();int original=slider.Value;
                SendMessage(slider.Handle,0x100,new IntPtr((int)Keys.Right),IntPtr.Zero);
                Check(slider.Value==original+1,"Brightness slider supports precise keyboard adjustment");
                SendMessage(slider.Handle,0x201,new IntPtr(1),new IntPtr((24<<16)|12));SendMessage(slider.Handle,0x202,IntPtr.Zero,new IntPtr((24<<16)|12));
                Check(slider.Value==5,"Brightness slider mouse selection respects the lower limit");
                slider.Value=999;Check(slider.Value==90,"Brightness cannot exceed firmware limits");
                var hours=Descendants(form).OfType<ClawTimeInput>().Where(c=>c.Format==ClawNumberFormat.Hour).ToArray();
                hours[0].Editor.Text="23:00";Check(hours[0].Value==23,"Night hour accepts direct text entry");
                hours[0].Editor.Text="25:00";SendMessage(hours[0].Editor.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);
                Check(hours[0].Value==23&&hours[0].Editor.Text=="23:00","Invalid hours restore the previous valid value");
                var duration=Descendants(form).OfType<ClawTimeInput>().Single(c=>c.Format==ClawNumberFormat.Duration);
                duration.Editor.Text="2:30";Check(duration.Value==150,"Idle delay accepts minutes and seconds");
                duration.Editor.Text="0:05";SendMessage(duration.Editor.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);
                Check(duration.Value==150,"Idle delay enforces the device minimum");
                var offline=Descendants(form).OfType<ChoiceBox>().Single();offline.Focus();Application.DoEvents();
                Check(scroll.ScrollOffset>0&&scroll.Content.Left==0,"Keyboard focus reveals lower controls without horizontal shifting");NoNativeBars();
                form.Close();
                using var flowForm=new Form{Size=new(500,350),ShowInTaskbar=false};
                using var scroller=new ClawScrollPanel{Dock=DockStyle.Fill,Padding=new Padding(24)};
                var flow=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
                scroller.Content.Controls.Add(flow);flowForm.Controls.Add(scroller);flowForm.Show();Application.DoEvents();
                for(int i=0;i<12;i++)flow.Controls.Add(new Label{Text="Setup field "+i,Width=360,Height=42});Application.DoEvents();
                Check(scroller.MaximumOffset>0,"Setup scrolling follows dynamically added fields");
                scroller.ScrollTo(scroller.MaximumOffset);Application.DoEvents();
                var last=flow.Controls[^1];var visible=scroller.RectangleToClient(last.RectangleToScreen(last.ClientRectangle));
                Check(visible.Bottom<=scroller.Height&&visible.Top>=0,"Last setup field is reachable without clipping");
                Check((GetWindowLong(scroller.Handle,-16)&0x00300000)==0,"Setup never creates native scrollbar styles");
                flowForm.Close();
            }catch(Exception e){failure=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40)))throw new Exception("Desktop layout checks timed out");
        if(failure is not null)throw new Exception("Desktop layout checks failed",failure);return passed;
    }
}
