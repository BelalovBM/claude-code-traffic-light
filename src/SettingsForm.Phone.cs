using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

        // The phone, all in one place: the state and its switch on top, then how to connect and when to notify.
        // NtfyEnabled means "a topic exists"; NtfyPaused is what the switch turns off.
        // Once a test reached the phone the steps fold away; this opens them again.
        bool stepsOpen;
        FlowLayoutPanel qrHolder;
        int qrWidth;

        void PhonePage(FlowLayoutPanel p)
        {
            if (!cfg.NtfyEnabled)
            {
                Section(p, Loc.T("sec.ntfy"), c =>
                {
                    var chipNone = new Label
                    {
                        Text = "○ " + Loc.T("phone.status.none"),
                        AutoSize = true,
                        Tag = "muted",
                        Font = new Font(Font, FontStyle.Bold),
                        Margin = Ui.Pad(3, 2, 3, 6),
                    };
                    c.Controls.Add(chipNone);
                    c.Controls.Add(Plain(Loc.T("phone.intro")));
                    var setup = new Button { Text = Loc.T("btn.phone.setup"), AutoSize = true, Margin = Ui.Pad(3, 8, 3, 3) };
                    setup.Click += (s, e) =>
                    {
                        cfg.NtfyEnabled = true;
                        cfg.NtfyPaused = false;
                        if (string.IsNullOrEmpty(cfg.NtfyTopic)) cfg.NtfyTopic = Secret.RandomTopic();
                        app.ApplyConfig();
                        BeginInvoke((Action)Rebuild);
                    };
                    c.Controls.Add(setup);
                });
                return;
            }

            Section(p, Loc.T("sec.ntfy"), c =>
            {
                var chip = new Label { AutoSize = true, Tag = "keep", Font = new Font(Font, FontStyle.Bold), Margin = Ui.Pad(3, 2, 3, 2) };
                c.Controls.Add(chip);
                var master = (CheckBox)Check(Loc.T("chk.remote.active"), !cfg.NtfyPaused, v => { cfg.NtfyPaused = !v; });
                int width = RowWidth;
                Action paint = () =>
                {
                    bool on = !cfg.NtfyPaused;
                    SetResult(chip, on ? "● " + Loc.T("phone.status.on") : "‖ " + Loc.T("phone.status.paused"),
                        on ? Palette.StatusOk : Palette.StatusWarn);
                    chip.MaximumSize = new Size(width, 0);
                };
                master.CheckedChanged += (s, e) => paint();
                c.Controls.Add(master);
                paint();
                // The tray menu has the same switch; follow it while this window is open.
                refresher = () =>
                {
                    if (master.Checked == cfg.NtfyPaused) master.Checked = !cfg.NtfyPaused;
                };
            });

            TwoColumns(p, PhoneColumns);
        }

        void PhoneColumns(FlowLayoutPanel p, FlowLayoutPanel right)
        {
            Section(p, Loc.T("sec.connect"), c =>
            {
                bool folded = cfg.NtfyTested && !stepsOpen;
                if (folded)
                {
                    Label done = ResultLabel();
                    done.Font = new Font(Font, FontStyle.Bold);
                    SetResult(done, "✔ " + Loc.T("ntfy.tested"), Palette.StatusOk);
                    c.Controls.Add(done);
                    c.Controls.Add(Note(Loc.T("ntfy.tested.note")));
                }
                else
                {
                    // The simple case: install the app, subscribe to the topic, test.
                    c.Controls.Add(Step(1, Loc.T("ntfy.step1")));
                    c.Controls.Add(Step(2, Loc.T("ntfy.step2")));
                    qrWidth = RowWidth;
                    qrHolder = Column(RowWidth, Ui.Pad(0));
                    c.Controls.Add(qrHolder);
                    RefreshQr();
                }

                var topic = new TextBox { Text = cfg.NtfyTopic, ReadOnly = true };
                c.Controls.Add(Row(Loc.T("label.ntfy.topic"), topic, 60));

                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                var copy = new Button { Text = Loc.T("btn.copy"), AutoSize = true };
                copy.Click += (s, e) => { try { Clipboard.SetText(cfg.NtfyTopic); } catch { } };
                var change = new Button { Text = Loc.T("btn.topic.change"), AutoSize = true };
                change.Click += (s, e) =>
                {
                    TopicDialog created;
                    // The dialog does not follow monitor scales itself: on another monitor Windows stretches it.
                    using (Native.SystemAwareWindows()) { created = new TopicDialog(cfg.NtfyTopic, Icon); created.CreateControl(); }
                    using (var dlg = created)
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Topic == cfg.NtfyTopic) return;
                        cfg.NtfyTopic = dlg.Topic;
                        app.ApplyConfig();
                        BeginInvoke((Action)Rebuild);
                    }
                };
                buttons.Controls.Add(copy);
                buttons.Controls.Add(change);
                c.Controls.Add(buttons);
                c.Controls.Add(Note(Loc.T("ntfy.change.note")));

                int indent = folded ? 3 : 31;
                if (!folded) c.Controls.Add(Step(3, Loc.T("ntfy.step3")));
                var test = new Button { Text = Loc.T("btn.test"), AutoSize = true, Margin = Ui.Pad(indent, 3, 3, 3) };
                var testResult = ResultLabel();
                testResult.Margin = Ui.Pad(indent, 2, 3, 4);
                test.Click += (s, e) => RunTest(test, testResult, done => Notifier.SendNtfy(cfg.NtfyServer, Notifier.Authorization(cfg), cfg.NtfyTopic,
                    Loc.T("test.title"), Loc.T("test.body"), false, null, done),
                    () => { cfg.NtfyTestedHash = cfg.NtfyCurrentHash; cfg.Save(); });
                c.Controls.Add(test);
                c.Controls.Add(testResult);
                c.Controls.Add(Note(Loc.T("ntfy.secret.note")));

                if (cfg.NtfyTested)
                {
                    var steps = new LinkLabel
                    {
                        Text = (stepsOpen ? "▾  " : "▸  ") + Loc.T("ntfy.steps.again"),
                        AutoSize = true,
                        MaximumSize = new Size(RowWidth, 0),
                        LinkColor = Palette.Link,
                        ActiveLinkColor = Palette.LinkActive,
                        Margin = Ui.Pad(3, 6, 3, 4),
                    };
                    steps.LinkClicked += (s, e) =>
                    {
                        stepsOpen = !stepsOpen;
                        BeginInvoke((Action)Rebuild);
                    };
                    c.Controls.Add(steps);
                }
            });

            Section(right, Loc.T("sec.when"), c =>
            {
                c.Controls.Add(Note(Loc.T("remote.hint")));
                c.Controls.Add(Check(Loc.T("chk.remote.ignored"), cfg.PhoneOnlyIfIgnored, v => cfg.PhoneOnlyIfIgnored = v));
                c.Controls.Add(Check(Loc.T("chk.remote.waiting"), cfg.RemoteWaiting, v => cfg.RemoteWaiting = v));
                c.Controls.Add(Row(Loc.T("label.remote.waiting.after"), Spin(cfg.RemoteWaitingAfterMinutes, 0, 240, v => cfg.RemoteWaitingAfterMinutes = v), NumberLabelWidth));
                c.Controls.Add(Check(Loc.T("chk.remote.done"), cfg.RemoteDone, v => cfg.RemoteDone = v));
                c.Controls.Add(Row(Loc.T("label.remote.done.min"), Spin(cfg.RemoteDoneMinMinutes, 0, 240, v => cfg.RemoteDoneMinMinutes = v), NumberLabelWidth));
                c.Controls.Add(Check(Loc.T("chk.remote.details"), cfg.RemoteDetails, v => cfg.RemoteDetails = v));
            });

            // Which server carries the messages: an explicit choice, so going to an own server and back is one click.
            // The own server's address and sign-in stay remembered while the public one is used.
            Section(right, Loc.T("sec.server"), c =>
            {
                RadioButton publicChoice = ServerChoice(Loc.T("ntfy.mode.public"), !cfg.NtfyOwn);
                RadioButton ownChoice = ServerChoice(Loc.T("ntfy.mode.own"), cfg.NtfyOwn);
                EventHandler changed = (s, e) =>
                {
                    if (cfg.NtfyOwn == ownChoice.Checked) return;
                    cfg.NtfyOwn = ownChoice.Checked;
                    cfg.ApplyNtfyServer();
                    app.ApplyConfig();
                    BeginInvoke((Action)Rebuild);
                };
                publicChoice.CheckedChanged += changed;
                ownChoice.CheckedChanged += changed;
                c.Controls.Add(publicChoice);
                c.Controls.Add(ownChoice);

                if (cfg.NtfyOwn)
                {
                    c.Controls.Add(Note(Loc.T("ntfy.own.hint")));

                    var server = new TextBox { Text = cfg.NtfyOwnServer };
                    Autosave(server, v =>
                    {
                        cfg.NtfyOwnServer = v;
                        cfg.ApplyNtfyServer();
                        app.ApplyConfig();
                        RefreshQr();
                    });
                    c.Controls.Add(Row(Loc.T("label.ntfy.server"), server, 125));

                    var user = new TextBox { Text = cfg.NtfyUser };
                    Autosave(user, v => { cfg.NtfyUser = v; app.ApplyConfig(); });
                    c.Controls.Add(Row(Loc.T("label.ntfy.user"), user, 125));

                    var secret = new TextBox { Text = cfg.NtfySecret, UseSystemPasswordChar = true };
                    Autosave(secret, v => { cfg.NtfySecret = v; app.ApplyConfig(); });
                    c.Controls.Add(Row(Loc.T("label.ntfy.secret"), secret, 125));
                }
                // The phone listens on one server: after a switch it has to subscribe there again.
                c.Controls.Add(Note(Loc.T("ntfy.mode.note")));
            });
        }

        RadioButton ServerChoice(string text, bool selected)
        {
            Size wrapped = TextRenderer.MeasureText(text, Font, new Size(RowWidth - Ui.S(24), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new RadioButton
            {
                Text = text,
                Checked = selected,
                AutoSize = false,
                UseMnemonic = false,
                Width = RowWidth,
                Height = Math.Max(Ui.S(22), wrapped.Height + Ui.S(6)),
                Margin = Ui.Pad(3, 3, 3, 3),
            };
        }

        // (Re)builds the QR code inside the connect card; the link follows the server address, which can change while typing.
        void RefreshQr()
        {
            using (Ui.Use(dpiFactor)) RefreshQrCore();
        }

        void RefreshQrCore()
        {
            if (qrHolder == null || qrHolder.IsDisposed) return;
            foreach (Control old in qrHolder.Controls.Cast<Control>().ToArray()) { qrHolder.Controls.Remove(old); old.Dispose(); }
            int saved = RowWidth;
            RowWidth = qrWidth;
            string link = NtfyLink();
            Bitmap qr = link == null ? null : Qr.Render(link, Ui.S(4));
            if (qr != null)
            {
                Control picture = CenteredPicture(qr);
                picture.AccessibleName = Loc.T("sec.subscribe");
                picture.AccessibleDescription = link;
                qrHolder.Controls.Add(picture);
            }
            else
            {
                qrHolder.Controls.Add(Note(Loc.T("ntfy.noqr")));
            }
            RowWidth = saved;
            Describe(qrHolder);
            Theme.Apply(qrHolder);
        }

        // ntfy:// deep link opens the subscribe screen in the ntfy phone app.
        string NtfyLink()
        {
            Uri uri;
            if (!Uri.TryCreate((cfg.NtfyServer ?? "").Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return null;
            return "ntfy://" + uri.Authority + "/" + cfg.NtfyTopic;
        }
    }
}
