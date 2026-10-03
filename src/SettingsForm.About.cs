using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Semaphore
{
    sealed partial class SettingsForm
    {

        Control StatusRow(Level level, string text)
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Ui.Pad(12, 2, 3, 2) };
            row.Controls.Add(new PictureBox { Image = TrayIcons.Draw(level, Ui.S(22)), SizeMode = PictureBoxSizeMode.AutoSize, Margin = Ui.Pad(0, 0, 8, 0) });
            row.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(RowWidth - Ui.S(50), 0), Margin = Ui.Pad(0, 4, 0, 0) });
            return row;
        }

        LinkLabel WebLink(string text, string url)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                LinkColor = Palette.Link,
                ActiveLinkColor = Palette.LinkActive,
                Margin = Ui.Pad(3, 2, 16, 0),
            };
            link.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            };
            return link;
        }

        // Two separate blocks: what the program is, and (in a card) how to support it.
        void AboutPage(FlowLayoutPanel p)
        {
            p.Controls.Add(new Label
            {
                Text = Loc.T("app.name"),
                AutoSize = true,
                Font = Ui.Font(Font.FontFamily, 13f, FontStyle.Bold),
                Margin = Ui.Pad(3, 12, 3, 2),
            });
            p.Controls.Add(Note(Loc.T("about.version", AppInfo.Version) + "   ·   " + Loc.T("about.author", AppInfo.Author + ", " + AppInfo.Year)));

            // Where the program comes from (new versions, source, problem reports) and under which licence.
            var links = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(RowWidth, 0), Margin = Ui.Pad(0, 2, 0, 8) };
            links.Controls.Add(WebLink(Loc.T("about.repo"), AppInfo.RepoUrl));
            links.Controls.Add(WebLink(Loc.T("about.license"), AppInfo.LicenseUrl));
            p.Controls.Add(links);
            // What the colours mean, with the very shapes the tray shows, then a short guide.
            p.Controls.Add(Plain(Loc.T("wiz.intro")));
            p.Controls.Add(StatusRow(Level.Working, Loc.T("wiz.yellow")));
            p.Controls.Add(StatusRow(Level.Compacting, Loc.T("wiz.blue")));
            p.Controls.Add(StatusRow(Level.Waiting, Loc.T("wiz.red")));
            p.Controls.Add(StatusRow(Level.Idle, Loc.T("wiz.green")));
            p.Controls.Add(Header(Loc.T("sec.usage")));
            for (int i = 1; i <= 5; i++)
                p.Controls.Add(Plain("•  " + Loc.T("usage." + i)));

            // The donation text, the address and its QR code sit together in one card.
            Bitmap qr = Qr.Render(AppInfo.Wallet, Ui.S(5));
            int qrColumn = qr == null ? 0 : qr.Width + Ui.S(28);
            int saved = RowWidth;
            int textWidth = RowWidth - Ui.S(28 + 16 + 12) - qrColumn;

            var card = new CardPanel { Margin = Ui.Pad(3, 36, 3, 3) };
            var inner = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = Ui.Pad(0),
            };
            FlowLayoutPanel text = Column(textWidth, Ui.Pad(0, 0, 16, 0));
            RowWidth = textWidth - Ui.S(8);
            text.Controls.Add(CardHeader(Loc.T("sec.support")));
            text.Controls.Add(Plain(Loc.T("support.text")));
            text.Controls.Add(Row(Loc.T("label.wallet"), new TextBox { Text = AppInfo.Wallet, ReadOnly = true }, 110));
            var copy = new Button { Text = Loc.T("btn.copy"), AutoSize = true };
            copy.Click += (s, e) => { try { Clipboard.SetText(AppInfo.Wallet); } catch { } };
            text.Controls.Add(copy);
            text.Controls.Add(Note(Loc.T("support.network")));
            inner.Controls.Add(text);

            if (qr != null)
            {
                FlowLayoutPanel picture = Column(qrColumn, Ui.Pad(0));
                RowWidth = qrColumn - Ui.S(8);
                picture.Controls.Add(CenteredPicture(qr));
                inner.Controls.Add(picture);
            }
            RowWidth = saved;
            card.Controls.Add(inner);
            p.Controls.Add(card);
        }
    }
}
