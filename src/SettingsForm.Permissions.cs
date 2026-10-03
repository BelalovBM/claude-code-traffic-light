using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

        // Two ways to answer a permission prompt without going back to the window; both are off by default.
        void ApprovePage(FlowLayoutPanel p)
        {
            Section(p, Loc.T("sec.approve.local"), c =>
            {
                var master = (CheckBox)Check(Loc.T("chk.approve.local"), cfg.LocalApprove, v => cfg.LocalApprove = v);
                // The panel is a way of showing the request, so it only makes sense while answering is on.
                var popup = (CheckBox)Check(Loc.T("chk.approve.popup"), cfg.PopupAsk, v => cfg.PopupAsk = v);
                popup.Margin = Ui.Pad(Ui.S(0) + 24, 3, 3, 3);
                popup.Width = RowWidth - Ui.S(24);
                popup.Enabled = cfg.LocalApprove;
                master.CheckedChanged += (s, e) => popup.Enabled = master.Checked;
                c.Controls.Add(master);
                c.Controls.Add(popup);
                c.Controls.Add(Row(Loc.T("label.approve.minutes"),
                    Spin(cfg.RemoteApproveMinutes, 1, 30, v => cfg.RemoteApproveMinutes = v), NumberLabelWidth));
                Details(c, Loc.T("approve.local.hint"));
            });
            Section(p, Loc.T("sec.approve"), PhoneApprove);
        }

        // The phone variant needs an explicit confirmation of the risks.
        void PhoneApprove(FlowLayoutPanel p)
        {
            var warning = new WarningBox();
            warning.Controls.Add(new Label
            {
                Text = Loc.T("approve.risk"),
                AutoSize = true,
                MaximumSize = new Size(RowWidth - Ui.S(40), 0),
            });
            p.Controls.Add(warning);
            if (!cfg.NtfyEnabled)
            {
                p.Controls.Add(Note(Loc.T("approve.needntfy")));
                return;
            }

            var enable = new CheckBox
            {
                Text = Loc.T("chk.approve"),
                Checked = cfg.RemoteApprove,
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(RowWidth, 0),
                Margin = Ui.Pad(3, 8, 3, 3),
            };
            enable.CheckedChanged += (s, e) =>
            {
                if (enable.Checked)
                {
                    var answer = MessageBox.Show(this, Loc.T("approve.confirm"), Loc.T("app.name"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        enable.Checked = false;
                        return;
                    }
                    // A fresh secret each time it is switched on, so an old leaked one stays useless.
                    cfg.ReplyTopic = Secret.RandomTopic("ccr-");
                }
                cfg.RemoteApprove = enable.Checked;
                app.ApplyConfig();
                BeginInvoke((Action)Rebuild);
            };
            p.Controls.Add(enable);
            if (!cfg.RemoteApprove) return;

            // The listener connects and drops in the background, so the line follows it.
            var state = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(RowWidth, 0),
                Font = new Font(Font, FontStyle.Bold),
                Tag = "keep",
                Margin = Ui.Pad(3, 6, 3, 6),
            };
            Action paint = () =>
            {
                string status = app.ApprovalService.Status;
                bool listening = status == "ok";
                SetResult(state, listening ? "● " + Loc.T("approve.status.on")
                         : string.IsNullOrEmpty(status) ? "○ " + Loc.T("approve.status.off")
                         : "▲ " + Loc.T("approve.status.err", status),
                    listening ? Palette.StatusOk : Palette.StatusWarn);
            };
            paint();
            refresher = paint;
            p.Controls.Add(state);

            // The two actions side by side, the same size, one line for what came of either.
            var test = new Button { Text = Loc.T("btn.approve.test"), AutoSize = false };
            var secret = new Button { Text = Loc.T("btn.approve.secret"), AutoSize = false };
            int buttonWidth = Math.Max(
                TextRenderer.MeasureText(test.Text, Font).Width,
                Math.Max(TextRenderer.MeasureText(Loc.T("approve.test.waiting"), Font).Width, TextRenderer.MeasureText(secret.Text, Font).Width)) + Ui.S(32);
            test.Size = secret.Size = new Size(buttonWidth, Ui.S(30));
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Ui.Pad(0, 6, 0, 0) };
            row.Controls.Add(test);
            row.Controls.Add(secret);
            var result = ResultLabel();
            test.Click += (s, e) =>
            {
                test.Enabled = false;
                test.Text = Loc.T("approve.test.waiting");
                SetResult(result, "", Theme.Fore);
                app.ApprovalService.RunTest(answer => OnUi(() =>
                {
                    if (test.IsDisposed || result.IsDisposed) return;
                    test.Text = Loc.T("btn.approve.test");
                    test.Enabled = true;
                    if (answer == "allow" || answer == "deny")
                        SetResult(result, "✔ " + Loc.T("approve.test.ok", Loc.T(answer == "allow" ? "approve.push.allow" : "approve.push.deny")), Palette.StatusOk);
                    else if (answer == "error")
                        SetResult(result, "✖ " + Loc.T("test.fail", Loc.T("approve.test.senderror").Replace('\n', ' ')), Palette.StatusError);
                    else
                        SetResult(result, "▲ " + Loc.T("approve.test.none"), Palette.StatusWarn);
                }));
            };
            // A new secret topic for the answers: buttons in push messages sent earlier stop working.
            secret.Click += (s, e) =>
            {
                cfg.ReplyTopic = Secret.RandomTopic("ccr-");
                app.ApplyConfig();
                SetResult(result, "✔ " + Loc.T("approve.secret.done"), Palette.StatusOk);
            };
            p.Controls.Add(row);
            p.Controls.Add(result);
            Details(p, Loc.T("approve.secret.note"), Loc.T("approve.questions.hint"), Loc.T("approve.hint"), Loc.T("approve.server.hint"));
        }
    }
}
