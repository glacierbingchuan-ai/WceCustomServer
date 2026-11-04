namespace WceRoomServer;

internal sealed class AboutForm : Form
{
    private const string Notice =
        "By: Glacier\n" +
        "2026.11.4\n" +
        "\n" +
        "本内容仅供学习交流，游戏开发殊为不易，若您喜欢，还请支持正版，给创作者一份鼓励。";

    private const string MitLicense =
        "MIT License\n" +
        "\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
        "of this software and associated documentation files (the \"Software\"), to deal\n" +
        "in the Software without restriction, including without limitation the rights\n" +
        "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
        "copies of the Software, and to permit persons to whom the Software is\n" +
        "furnished to do so, subject to the following conditions:\n" +
        "\n" +
        "The above copyright notice and this permission notice shall be included in all\n" +
        "copies or substantial portions of the Software.\n" +
        "\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
        "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
        "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
        "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
        "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
        "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
        "SOFTWARE.";

    public AboutForm()
    {
        Text = "关于";
        ClientSize = new Size(560, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 252);

        Label notice = new()
        {
            Text = Notice,
            Location = new Point(20, 16),
            Size = new Size(520, 100),
            ForeColor = Color.FromArgb(50, 62, 80)
        };
        Controls.Add(notice);

        Label licenseCaption = new()
        {
            Text = "MIT License",
            Location = new Point(20, 118),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 40, 60)
        };
        Controls.Add(licenseCaption);

        RichTextBox license = new()
        {
            Text = MitLicense,
            Location = new Point(20, 144),
            Size = new Size(520, 270),
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Font = new Font("Consolas", 9F),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false
        };
        Controls.Add(license);

        Button ok = new()
        {
            Text = "关闭",
            DialogResult = DialogResult.OK,
            Location = new Point(450, 424),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(28, 40, 60),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = ok;
    }
}
