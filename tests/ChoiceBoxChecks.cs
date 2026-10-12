using ClawPal.Companion;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ChoiceBoxChecks
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
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
                    var menu=choice.SelectionMenu;
                    var bounds=menu.Items[index].Bounds;
                    var point=new IntPtr(((bounds.Y+bounds.Height/2)<<16)|(bounds.X+bounds.Width/2));
                    // Exercise the actual native mouse dispatch against our own test popup.
                    SendMessage(menu.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(menu.Handle,0x0202,IntPtr.Zero,point);
                    Application.DoEvents();
                    Check(choice.SelectedIndex==index,"Mouse selection updates the chosen index");
                    Check(choice.Text==choice.Items[index].ToString(),"Selected value remains visible");
                    Check(!menu.Visible && !menu.IsDisposed,"Closing after selection retains a reusable popup");
                }
                for(int i=0;i<12;i++) Select(i%4);
                choice.PerformClick();Application.DoEvents();
                Check(choice.SelectionMenu.Width>=choice.Width,"Dropdown popup matches the field width");
                choice.SelectionMenu.Close();
                var before=choice.SelectedItem;
                choice.PerformClick(); Application.DoEvents(); choice.SelectionMenu.Close(ToolStripDropDownCloseReason.Keyboard); Application.DoEvents();
                Check(Equals(before,choice.SelectedItem),"Dismissal preserves the selected value");
                Check(!choice.SelectionMenu.IsDisposed,"Dismissal does not dispose the popup");
                choice.Items.Clear();choice.Items.Add("COM12");choice.SelectedIndex=-1;Select(0);
                choice.Items.Clear();choice.SelectedIndex=-1;choice.PerformClick();Application.DoEvents();
                Check(choice.SelectionMenu.Items.Count==1 && !choice.SelectionMenu.Items[0].Enabled,"Empty list shows a disabled placeholder");
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
