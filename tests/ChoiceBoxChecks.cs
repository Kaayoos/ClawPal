using ClawPal.Companion;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ChoiceBoxChecks
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    private static IntPtr Point(Point p) => new(((p.Y & 0xffff) << 16) | (p.X & 0xffff));
    public static int Run()
    {
        Exception? failure = null;
        int passed = 0;
        var thread = new Thread(() => {
            try {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                using var form = new Form { Text="ClawPal dropdown regression test", Size=new Size(380,180), ShowInTaskbar=false };
                using var choice = new ChoiceBox { Location=new Point(20,20), Size=new Size(300,40) };
                choice.Items.AddRange(new object[] { "COM4", "COM8", "Łódź", "Central Europe" });
                form.Controls.Add(choice); form.Show(); Application.DoEvents();
                void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
                void Select(int index) {
                    choice.PerformClick(); Application.DoEvents();
                    var list=choice.SelectionList;
                    Check(choice.SelectionMenu.Visible,"Clicking the field opens its list");
                    var bounds=list.RowBounds(index);var point=Point(new(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2));
                    // Exercise the actual native mouse dispatch against our own test popup.
                    SendMessage(list.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(list.Handle,0x0202,IntPtr.Zero,point);
                    Application.DoEvents();
                    Check(choice.SelectedIndex==index,"Mouse selection updates the chosen index");
                    Check(choice.Text==choice.Items[index].ToString(),"Selected value remains visible");
                    Check(!choice.SelectionMenu.Visible && !choice.SelectionMenu.IsDisposed,"Closing after selection retains a reusable popup");
                }
                for(int i=0;i<12;i++) Select(i%4);
                choice.PerformClick();Application.DoEvents();
                Check(choice.SelectionMenu.Width>=choice.Width,"Dropdown popup matches the field width");
                choice.PerformClick();Application.DoEvents();
                Check(!choice.SelectionMenu.Visible,"Clicking the open field closes its list instead of reopening it");
                var before=choice.SelectedItem;
                choice.PerformClick(); Application.DoEvents(); choice.SelectionMenu.Close(ToolStripDropDownCloseReason.Keyboard); Application.DoEvents();
                Check(Equals(before,choice.SelectedItem),"Dismissal preserves the selected value");
                Check(!choice.SelectionMenu.IsDisposed,"Dismissal does not dispose the popup");

                choice.Items.Clear();for(int i=0;i<40;i++)choice.Items.Add($"Zone {i:00}");choice.SelectedIndex=0;
                choice.PerformClick();Application.DoEvents();var longList=choice.SelectionList;
                Check(longList.MaximumOffset>0&&choice.SelectionMenu.Height<40*40,"A long list stays a compact scrolling popup");
                var middle=Point(longList.PointToScreen(new(longList.Width/2,longList.Height/2)));
                SendMessage(longList.Handle,0x020A,new IntPtr(unchecked(-120<<16)),middle);Application.DoEvents();
                Check(longList.ScrollOffset>0,"The mouse wheel scrolls a long list");
                SendMessage(longList.Handle,0x020A,new IntPtr(120*20<<16),middle);Application.DoEvents();
                Check(longList.ScrollOffset==0,"Scrolling up stops at the first entry");
                SendMessage(longList.Handle,0x100,new IntPtr((int)Keys.End),IntPtr.Zero);Application.DoEvents();
                Check(longList.Hover==39&&longList.ScrollOffset==longList.MaximumOffset,"End reveals the last entry");
                SendMessage(longList.Handle,0x102,new IntPtr('z'),IntPtr.Zero);SendMessage(longList.Handle,0x100,new IntPtr((int)Keys.Home),IntPtr.Zero);
                SendMessage(longList.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero);SendMessage(longList.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);Application.DoEvents();
                Check(choice.SelectedIndex==1&&!choice.SelectionMenu.Visible,"Arrow keys and Enter choose from the list");

                choice.Items.Clear();choice.Items.Add("COM12");choice.SelectedIndex=-1;Select(0);
                choice.Items.Clear();choice.SelectedIndex=-1;choice.PerformClick();Application.DoEvents();
                Check(choice.SelectionList.Count==0 && choice.SelectionList.Placeholder.Length>0,"Empty list shows a placeholder");
                choice.SelectionMenu.Close();
                var owned=choice.SelectionMenu;choice.Dispose();Check(owned.IsDisposed,"Disposing the control releases its popup");
                form.Close();
            } catch(Exception error) { failure=error; }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20)))throw new Exception("Dropdown regression test timed out");
        if(failure is not null)throw new Exception("Dropdown regression failed",failure);
        Console.WriteLine($"{passed} native dropdown lifecycle checks passed.");return passed;
    }
}
