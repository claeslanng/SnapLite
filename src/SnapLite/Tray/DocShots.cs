using System.Drawing.Imaging;
using SnapLite.Hotkeys;
using SnapLite.Interop;
using SnapLite.Settings;

namespace SnapLite.Tray;

/// <summary>
/// Developer aid for the README screenshots: <c>SnapLite.exe --render-docs &lt;folder&gt; [sample.png]</c>
/// renders the notification (light and dark) and some Settings tabs into PNGs. Windows are drawn
/// off-screen, so nothing appears on the desktop. Machine-specific paths are replaced by placeholders.
/// </summary>
internal static class DocShots
{
    private sealed class DemoHost : ISettingsHost
    {
        public bool IsHotkeyAvailable(HotkeyBinding binding) => true;
        public void SuspendHotkeys() { }
        public void ResumeHotkeys() { }
        public IReadOnlyList<string> ApplySettings(AppSettings settings, bool startWithWindows) => [];
    }

    public static int Run(string[] args)
    {
        string dir = Path.GetFullPath(args.Length > 1 ? args[1] : "docs");
        string? sample = args.Length > 2 ? args[2] : null;
        Directory.CreateDirectory(dir);

        ToastForm.RenderPreview(["", Path.Combine(dir, "toast-light.png"), sample ?? "", "light"]);
        ToastForm.RenderPreview(["", Path.Combine(dir, "toast-dark.png"), sample ?? "", "dark"]);

        using var icon = AppIcons.Create(paused: false, pixelSize: 32);
        using var form = new SettingsForm(new DemoHost(), new AppSettings().Normalize(), icon)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
        };
        form.Show();
        Sanitize(form);
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
        var tabs = FindAll<TabControl>(form).First();
        foreach (var (title, file) in new[] { ("Shortcuts", "settings-shortcuts.png"), ("Browser", "settings-browser.png"), ("Steps", "settings-steps.png") })
        {
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(p => p.Text == title);
            form.Refresh();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); } // let every control paint
            Capture(form, Path.Combine(dir, file));
        }
        form.Close();
        return 0;
    }

    /// <summary>
    /// <c>SnapLite.exe --write-icon path.ico</c>: writes the logo as a Windows icon file with every size
    /// Explorer, the Start menu and the taskbar ask for (16–256 px, PNG-compressed entries).
    /// The result is checked in as src/SnapLite/Assets/SnapLite.ico and embedded in the exe.
    /// </summary>
    public static int WriteIcon(string[] args)
    {
        string path = args.Length > 1 ? args[1] : "SnapLite.ico";
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        // Classic 32-bit bitmap entries for the small sizes (every Windows component reads those),
        // PNG for the big ones (96 px and up), which keeps the file small; Windows reads both.
        var images = sizes.Select(s =>
        {
            using var bmp = AppIcons.DrawBitmap(s);
            return s >= 96 ? Png(bmp) : Dib(bmp);
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length); // ICONDIR
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)                                  // ICONDIRENTRY
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var png in images) w.Write(png);
        return 0;
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>An icon image in the classic format: BITMAPINFOHEADER, bottom-up BGRA rows, then an all-zero AND mask.</summary>
    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);           // header; height counts colour + mask
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(w * h * 4 + maskStride * h);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally { bmp.UnlockBits(data); }
        bw.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }

    /// <summary>No user names in published screenshots.</summary>
    private static void Sanitize(Control root)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var c in FindAll<Control>(root))
        {
            if (c.Text.Contains(local, StringComparison.OrdinalIgnoreCase)) c.Text = c.Text.Replace(local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
            if (c.Text.Contains(profile, StringComparison.OrdinalIgnoreCase)) c.Text = c.Text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The window frame comes from PrintWindow (the real Windows look); the contents are painted by
    /// WinForms itself (DrawToBitmap), since an off-screen window's child controls never reach the screen.
    /// </summary>
    private static void Capture(Form form, string path)
    {
        Native.DwmGetWindowAttributeRect(form.Handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>());
        using var full = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            try { Native.PrintWindow(form.Handle, hdc, Native.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }

            using var client = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
            using (var cg = Graphics.FromImage(client)) cg.Clear(form.BackColor);
            foreach (Control c in form.Controls.Cast<Control>().Reverse())
                c.DrawToBitmap(client, c.Bounds);
            var origin = form.PointToScreen(Point.Empty);
            g.DrawImage(client, origin.X - form.Left, origin.Y - form.Top);
        }
        // Trim the invisible resize borders around the visible frame.
        var visible = new Rectangle(frame.Left - form.Left, frame.Top - form.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        visible.Intersect(new Rectangle(Point.Empty, full.Size));
        if (visible.Width < 50 || visible.Height < 50) visible = new Rectangle(Point.Empty, full.Size);
        using var crop = full.Clone(visible, PixelFormat.Format32bppArgb);
        crop.Save(path, ImageFormat.Png);
    }

    private static IEnumerable<T> FindAll<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) yield return t;
            foreach (var d in FindAll<T>(c)) yield return d;
        }
    }
}
