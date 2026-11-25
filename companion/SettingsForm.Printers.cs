namespace ClawPal.Companion;

/* Printers: the 3D printers ClawPal reads for the display's Printer app. Saved at once, on this PC
   only; the display hears of them through telemetry, never their addresses, codes or keys. */
internal sealed partial class SettingsForm
{
    private string? printerFeedback;
    private sealed record PrinterKindChoice(PrinterKind Kind, string Label) { public override string ToString() => Label; }
    private static string KindName(PrinterKind kind) => kind switch
    {
        PrinterKind.Bambu => L.Text("printer_kind_bambu"), PrinterKind.Moonraker => L.Text("printer_kind_moonraker"),
        PrinterKind.OctoPrint => L.Text("printer_kind_octoprint"), _ => L.Text("printer_kind_prusalink")
    };

    private void PrintersPage()
    {
        title.Text = L.Text("printers_title"); subtitle.Text = L.Text("printers_subtitle");
        var p = Stack();
        var monitor = connection.Printers;
        var trust = p.Add(new ClawCard(L.Text("printers_read_only_title")));
        trust.Add(ClawTheme.Label(L.Text("printers_read_only_note"), 400, ClawTheme.Body, false, 22));

        // The printers already saved, each with how it is doing right now.
        var yours = p.Add(new ClawSection(L.Text("printers_yours")));
        var entries = preview ? [] : monitor.Book.Entries();
        var statuses = new List<(int Id, Label Label)>();
        if (!draft.Enabled(10)) yours.Add(Note(L.Text("printers_off_in_apps")));
        if (entries.Count == 0) yours.Add(Note(L.Text("printers_none")));
        foreach (var entry in entries)
        {
            var name = Caption(entry.Name);
            var detail = Note(KindName(entry.Kind) + " · " + entry.Address);
            var state = Note(monitor.StatusText(entry.Id));
            statuses.Add((entry.Id, state));
            var remove = new ClawButton() { Text = L.Text("remove_printer"), AccessibleName = L.Text("remove_printer") + " " + entry.Name };
            int id = entry.Id;
            remove.Click += (_, _) => { monitor.Book.Remove(id); printerFeedback = null; ShowPage(SettingsPage.Printers); };
            yours.Add(Row(520, (Column(name, detail, state), 0), (remove, 150)), 18);
        }
        var alerts = Switch(L.Text("printer_alerts"), preview || monitor.Book.AlertsOn, value => { if (!preview) monitor.Book.SetAlerts(value); });
        yours.Add(alerts, 2);
        yours.Add(Note(L.Text("printer_alerts_note")));
        if (config.Paused) yours.Add(Note(L.Text("printer_display_paused")));

        // Adding one: what the network found, or typed by hand.
        var add = p.Add(new ClawSection(L.Text("add_printer")));
        var find = new ClawButton() { Text = L.Text("find_printers") };
        var found = new ChoiceBox() { Visible = false };
        var searching = Note("");
        add.Add(Row(0, (find, 200)), 6);
        add.Add(searching, 4);
        add.Add(found, 14);
        var kind = new ChoiceBox();
        foreach (PrinterKind k in Enum.GetValues<PrinterKind>()) kind.Items.Add(new PrinterKindChoice(k, KindName(k)));
        kind.SelectedIndex = 0;
        var name_ = ClawTheme.Input(L.Text("printer_name"));
        var address = ClawTheme.Input(L.Text("printer_address"));
        var serial = ClawTheme.Input(L.Text("printer_serial"));
        var secret = ClawTheme.Input(L.Text("printer_access_code")); secret.UseSystemPasswordChar = true;
        var serialColumn = Column(Caption(L.Text("printer_serial")), serial);
        var secretCaption = Caption(L.Text("printer_access_code"));
        add.Add(Column(Caption(L.Text("printer_kind")), kind), 10);
        add.Add(Row(560, (Column(Caption(L.Text("printer_name")), name_), 0), (Column(Caption(L.Text("printer_address")), address), 0)), 10);
        add.Add(Row(560, (serialColumn, 0), (Column(secretCaption, secret), 0)), 6);
        var hint = add.Add(Note(""), 12);
        var save = new ClawButton() { Text = L.Text("add_printer"), Primary = true };
        add.Add(Row(0, (save, 200)), 8);
        var feedback = add.Add(Note(printerFeedback ?? ""));
        printerFeedback = null;

        PrinterKind Kind() => kind.SelectedItem is PrinterKindChoice choice ? choice.Kind : PrinterKind.Bambu;
        void Fields()
        {
            bool bambu = Kind() == PrinterKind.Bambu;
            serialColumn.Visible = bambu;
            secretCaption.Text = bambu ? L.Text("printer_access_code") : Kind() == PrinterKind.Moonraker ? L.Text("printer_api_key_optional") : L.Text("printer_api_key");
            secret.PlaceholderText = secretCaption.Text;
            hint.Text = L.Text(Kind() switch
            {
                PrinterKind.Bambu => "printer_bambu_hint", PrinterKind.Moonraker => "printer_moonraker_hint",
                PrinterKind.OctoPrint => "printer_octoprint_hint", _ => "printer_prusalink_hint"
            });
        }
        kind.SelectedIndexChanged += (_, _) => Fields();
        Fields();
        found.SelectedIndexChanged += (_, _) =>
        {
            if (found.SelectedItem is not FoundPrinter printer) return;
            kind.SelectedIndex = (int)printer.Kind;
            name_.Text = printer.Name; address.Text = printer.Address; serial.Text = printer.Serial;
            secret.Text = ""; secret.Focus();
        };
        find.Click += async (_, _) =>
        {
            if (preview) return;
            find.Enabled = false; searching.Text = L.Text("printers_searching"); SetMoment("thinking");
            try
            {
                var printers = await PrinterDiscovery.FindAsync(TimeSpan.FromSeconds(6), CancellationToken.None);
                if (IsDisposed || found.IsDisposed) return;
                found.Items.Clear();
                foreach (var printer in printers) found.Items.Add(printer);
                found.Visible = printers.Count > 0;
                searching.Text = printers.Count > 0 ? L.Text("printers_found_0", printers.Count) : L.Text("printers_found_none");
                if (printers.Count > 0) found.SelectedIndex = 0;
                SetMoment(null);
            }
            catch (Exception error) when (error is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
            {
                if (!searching.IsDisposed) searching.Text = L.Text("printers_found_none");
                Log.Write("Printer discovery failed: " + error.GetType().Name);
            }
            finally { if (!find.IsDisposed) find.Enabled = true; }
        };
        save.Click += (_, _) =>
        {
            if (preview) return;
            try
            {
                var added = monitor.Book.Add(Kind(), name_.Text, address.Text, serial.Text, secret.Text);
                secret.Text = "";
                printerFeedback = L.Text("printer_added_0", added.Name);
                SetMoment("happy");
                ShowPage(SettingsPage.Printers);
            }
            catch (ArgumentException error) { feedback.Text = error.Message; SetMoment("error"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { feedback.Text = error.Message; SetMoment("error"); }
        };

        // Status lines follow the printers while the page is open.
        var tick = new System.Windows.Forms.Timer() { Interval = 1000 };
        tick.Tick += (_, _) => { foreach (var (id, label) in statuses) if (!label.IsDisposed) { string text = monitor.StatusText(id); if (label.Text != text) label.Text = text; } };
        tick.Start();
        p.Disposed += (_, _) => tick.Dispose();
    }
}
