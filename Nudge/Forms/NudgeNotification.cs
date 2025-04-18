// nudge - clipboard notification utility
// copyright (c) 2025 crunny
// all rights reserved.
using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;

namespace Nudge
{
    // helps make rounded rectangles
    public static class GraphicsExtensions
    {
        public static void AddRoundedRectangle(this GraphicsPath path, RectangleF rect, float radius)
        {
            float diameter = radius * 2;
            SizeF size = new SizeF(diameter, diameter);
            RectangleF arc = new RectangleF(rect.Location, size);
            
            // top left arc
            path.AddArc(arc, 180, 90);
            
            // top right arc
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            
            // bottom right arc
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            
            // bottom left arc
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            
            path.CloseFigure();
        }
        
        // drop shadow for ios-like effect
        public static void DrawModernShadow(this Graphics g, GraphicsPath path, Color shadowColor, int depth, int blur, Point offset)
        {
            // make a bigger path for the shadow
            RectangleF bounds = path.GetBounds();
            bounds.Inflate(blur / 2, blur / 2);
            bounds.Offset(offset.X, offset.Y);
            
            // layer shadows for a better look
            for (int i = 1; i <= depth; i++)
            {
                float alpha = (float)(depth - i + 1) / (depth * 1.5f);
                using (GraphicsPath shadowPath = (GraphicsPath)path.Clone())
                {
                    Matrix translateMatrix = new Matrix();
                    translateMatrix.Translate(offset.X * (i / (float)depth), offset.Y * (i / (float)depth));
                    shadowPath.Transform(translateMatrix);
                    
                    using (PathGradientBrush shadowBrush = new PathGradientBrush(shadowPath))
                    {
                        Color semiTransparentShadow = Color.FromArgb((int)(shadowColor.A * alpha * 0.7), shadowColor);
                        Color transparentShadow = Color.FromArgb(0, shadowColor);
                        
                        shadowBrush.CenterColor = semiTransparentShadow;
                        shadowBrush.SurroundColors = new Color[] { transparentShadow };
                        shadowBrush.FocusScales = new PointF(0.95f, 0.85f);
                        
                        g.FillPath(shadowBrush, shadowPath);
                    }
                }
            }
        }
    }

    public class NudgeNotification : Form
    {
        // colors
        private static readonly Color BackgroundColor = ColorTranslator.FromHtml("#101828"); // dark navy
        private static readonly Color AccentColor = ColorTranslator.FromHtml("#00D492");     // bright green
        private static readonly Color TextColor = Color.White;
        private static readonly Color SecondaryTextColor = Color.FromArgb(220, 220, 220);
        
        // cache for stuff we use a lot
        private static readonly object _cacheLock = new object();
        private static Image? _cachedClipboardIcon = null;
        
        // cleanup method to free cached stuff
        public static void CleanupCachedResources()
        {
            lock (_cacheLock)
            {
                if (_cachedClipboardIcon != null)
                {
                    _cachedClipboardIcon.Dispose();
                    _cachedClipboardIcon = null;
                }
            }
        }
        
        private static readonly int DELAY_BEFORE_FADEOUT = 1800; // 1.8 seconds
        private readonly string _clipboardContent;
        private Point _finalPosition;
        private Point _startPosition;
        private Point _endPosition;
        private System.Windows.Forms.Timer? _animationTimer;
        private System.Windows.Forms.Timer? _delayTimer;
        private System.Windows.Forms.Timer? _fadeOutTimer;
        private bool _disposed;
        
        // p/invoke stuff for shadows
        [DllImport("user32.dll")]
        private static extern int SetClassLong(IntPtr hwnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        private static extern int GetClassLong(IntPtr hwnd, int nIndex);
        
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int GCL_STYLE = -26;
        private const int CS_DROPSHADOW = 0x20000;

        public NudgeNotification()
        {
            // grab clipboard content
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (string.IsNullOrEmpty(text))
                    {
                        _clipboardContent = "[Empty text]";
                    }
                    else
                    {
                        // trim long text
                        if (text.Length > 50)
                        {
                            _clipboardContent = text.Substring(0, 47) + "...";
                        }
                        else
                        {
                            _clipboardContent = text;
                        }
                        
                        // fix control chars
                        _clipboardContent = string.Join("", 
                            _clipboardContent.Select(c => char.IsControl(c) ? ' ' : c));
                    }
                }
                else if (Clipboard.ContainsImage())
                {
                    _clipboardContent = "[Image]";
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    _clipboardContent = $"[{Clipboard.GetFileDropList().Count} file(s)]";
                }
                else if (Clipboard.ContainsAudio())
                {
                    _clipboardContent = "[Audio]";
                }
                else
                {
                    _clipboardContent = "[Content copied]";
                }
            }
            catch (ExternalException ex)
            {
                // clipboard error
                System.Diagnostics.Debug.WriteLine($"Clipboard access error: {ex.Message}");
                _clipboardContent = "[Content copied]";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading clipboard: {ex.Message}");
                _clipboardContent = "[Content copied]";
            }

            InitializeNotificationForm();
        }

        private void InitializeNotificationForm()
        {
            // set up the notification window
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = BackgroundColor;
            this.ForeColor = TextColor;
            this.Opacity = 0; // start invisible
            this.Width = 340; 
            this.Height = 85; 
            
            // smooth rendering
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | 
                          ControlStyles.UserPaint | 
                          ControlStyles.DoubleBuffer | 
                          ControlStyles.OptimizedDoubleBuffer, true);
            
            // find active screen
            Screen activeScreen = GetActiveScreen();
            int screenWidth = activeScreen.WorkingArea.Width;
            int screenHeight = activeScreen.WorkingArea.Height;
            
            int centerX = activeScreen.WorkingArea.Left + (screenWidth - this.Width) / 2;
            
            // animation positions
            _finalPosition = new Point(centerX, activeScreen.WorkingArea.Top + 30);
            _startPosition = new Point(centerX, activeScreen.WorkingArea.Top - this.Height); // off-screen
            _endPosition = new Point(centerX, activeScreen.WorkingArea.Top - this.Height); // off-screen
            
            // start position
            this.Location = _startPosition;

            // content panel
            Panel contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                BackColor = Color.Transparent
            };
            this.Controls.Add(contentPanel);

            // add icon and text
            AddNotificationContent(contentPanel);

            // animation setup
            this.Load += OnFormLoad;
            
            // add shadow when form is created
            this.HandleCreated += (s, e) => {
                SetClassLong(this.Handle, GCL_STYLE, GetClassLong(this.Handle, GCL_STYLE) | CS_DROPSHADOW);
                
                // try dark mode on windows 11
                try
                {
                    int darkMode = 1;
                    DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
                catch
                {
                    // no biggie if not supported
                }
            };
        }
        
        // find screen with active window or mouse
        private Screen GetActiveScreen()
        {
            try
            {
                // check active window screen
                IntPtr activeWindowHandle = GetForegroundWindow();
                if (activeWindowHandle != IntPtr.Zero)
                {
                    if (GetWindowRect(activeWindowHandle, out RECT windowRect))
                    {
                        // find screen with window center
                        Point centerPoint = new Point(
                            (windowRect.Left + windowRect.Right) / 2,
                            (windowRect.Top + windowRect.Bottom) / 2
                        );
                        
                        Screen? screenWithActiveWindow = Screen.AllScreens
                            .FirstOrDefault(s => s.Bounds.Contains(centerPoint));
                            
                        if (screenWithActiveWindow != null)
                            return screenWithActiveWindow;
                    }
                }
                
                // check mouse cursor screen
                Point cursorPosition;
                GetCursorPos(out cursorPosition);
                
                Screen? screenWithCursor = Screen.AllScreens
                    .FirstOrDefault(s => s.Bounds.Contains(cursorPosition));
                    
                if (screenWithCursor != null)
                    return screenWithCursor;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error detecting active screen: {ex.Message}");
            }
            
            // fallback to primary screen
            return Screen.PrimaryScreen ?? Screen.AllScreens[0];
        }
        
        // more p/invoke stuff
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point lpPoint);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void AddNotificationContent(Panel contentPanel)
        {
            // app icon
            PictureBox iconBox = new PictureBox
            {
                Size = new Size(36, 36),
                Location = new Point(15, 22),
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            
            // use cached icon or make new one
            if (_cachedClipboardIcon == null)
            {
                lock (_cacheLock)
                {
                    // double-check
                    if (_cachedClipboardIcon == null)
                    {
                        // use logo png
                        _cachedClipboardIcon = CreateIconFromAppLogo();
                    }
                }
            }
            
            // clone image and apply accent color
            iconBox.Image = ApplyAccentColorToImage((Image)_cachedClipboardIcon.Clone());
            contentPanel.Controls.Add(iconBox);

            // text container
            Panel textContainer = new Panel
            {
                Size = new Size(this.Width - 85, this.Height - 30),
                Location = new Point(62, 15),
                BackColor = Color.Transparent,
            };
            contentPanel.Controls.Add(textContainer);

            // fonts
            Font headerFont = CreateModernFont(12f, FontStyle.Bold);
            Font contentFont = CreateModernFont(11f, FontStyle.Regular);

            // header
            Label lblHeader = new Label
            {
                Text = "Nudge - Copied",
                Font = headerFont,
                ForeColor = AccentColor,
                AutoSize = true,
                Location = new Point(0, 0),
                BackColor = Color.Transparent,
            };
            textContainer.Controls.Add(lblHeader);

            // content
            Label lblContent = new Label
            {
                Text = _clipboardContent,
                Font = contentFont,
                ForeColor = SecondaryTextColor,
                AutoSize = false,
                Size = new Size(this.Width - 95, 35),
                Location = new Point(0, 26),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                UseMnemonic = false // no & as access key
            };
            textContainer.Controls.Add(lblContent);
        }

        // apply accent color to icon
        private Image ApplyAccentColorToImage(Image originalImage)
        {
            Bitmap result = new Bitmap(originalImage.Width, originalImage.Height);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                // color matrix to keep alpha but use accent color
                float r = AccentColor.R / 255f;
                float green = AccentColor.G / 255f;
                float b = AccentColor.B / 255f;
                
                ColorMatrix colorMatrix = new ColorMatrix(new float[][]
                {
                    new float[] {0, 0, 0, 0, 0},
                    new float[] {0, 0, 0, 0, 0},
                    new float[] {0, 0, 0, 0, 0},
                    new float[] {0, 0, 0, 1, 0},
                    new float[] {r, green, b, 0, 1}
                });
                
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(colorMatrix);
                    graphics.DrawImage(originalImage, new Rectangle(0, 0, originalImage.Width, originalImage.Height),
                        0, 0, originalImage.Width, originalImage.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            return result;
        }
        
        // font creation
        private Font CreateModernFont(float size, FontStyle style)
        {
            // use segoe ui for modern look
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        // get icon from app icon
        private static Image CreateIconFromAppIcon()
        {
            try
            {
                // try assets folder
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Nudge.ico");
                
                // try base dir
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nudge.ico");
                }
                
                // try exe icon
                if (!File.Exists(iconPath))
                {
                    using (Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application)
                    {
                        return new Bitmap(appIcon.ToBitmap(), 24, 24);
                    }
                }
                
                // load from file
                using (Icon ico = new Icon(iconPath, 24, 24))
                {
                    return new Bitmap(ico.ToBitmap());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading app icon: {ex.Message}");
                
                // use system icon
                return new Bitmap(SystemIcons.Application.ToBitmap(), 24, 24);
            }
        }

        // get icon from logo png
        private static Image CreateIconFromAppLogo()
        {
            try
            {
                // check assets folder
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "NudgeLogo.png");
                
                // if not found, look in project
                if (!File.Exists(logoPath))
                {
                    // get exe dir
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    
                    // go up to find project root
                    DirectoryInfo? dir = new DirectoryInfo(exeDir);
                    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Assets", "NudgeLogo.png")))
                    {
                        dir = dir.Parent;
                    }
                    
                    // found it
                    if (dir != null)
                    {
                        logoPath = Path.Combine(dir.FullName, "Assets", "NudgeLogo.png");
                    }
                }
                
                // load png if it exists
                if (File.Exists(logoPath))
                {
                    using (Image originalImage = Image.FromFile(logoPath))
                    {
                        // resize it nicely
                        Bitmap resizedImage = new Bitmap(40, 40);
                        using (Graphics g = Graphics.FromImage(resizedImage))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.DrawImage(originalImage, 0, 0, 40, 40);
                        }
                        return resizedImage;
                    }
                }
                
                // use icon if png not found
                return CreateIconFromAppIcon();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading logo: {ex.Message}");
                // fallback to icon
                return CreateIconFromAppIcon();
            }
        }

        private void OnFormLoad(object? sender, EventArgs e)
        {
            // set position
            this.Location = _startPosition;
            
            _animationTimer = new System.Windows.Forms.Timer
            {
                Interval = 8 // ~120fps
            };
            
            int animationStep = 0;
            int maxAnimationSteps = 24; // smooth animation
            
            _animationTimer.Tick += (s, ev) =>
            {
                // animate with easing
                animationStep++;
                double progress = EaseOutQuint((double)animationStep / maxAnimationSteps);
                
                // fade in
                this.Opacity = Math.Min(0.98, progress);
                
                // slide down
                int newY = _startPosition.Y + (int)((double)(_finalPosition.Y - _startPosition.Y) * progress);
                this.Location = new Point(_startPosition.X, newY);
                
                if (animationStep >= maxAnimationSteps)
                {
                    _animationTimer.Stop();
                    this.Location = _finalPosition;
                    this.Opacity = 0.98;
                    
                    // wait before disappearing
                    _delayTimer = new System.Windows.Forms.Timer
                    {
                        Interval = DELAY_BEFORE_FADEOUT
                    };
                    
                    _delayTimer.Tick += (s2, ev2) =>
                    {
                        _delayTimer.Stop();
                        
                        _fadeOutTimer = new System.Windows.Forms.Timer
                        {
                            Interval = 8
                        };
                        
                        int fadeOutStep = 0;
                        int fadeOutMaxSteps = 24;
                        
                        _fadeOutTimer.Tick += (s3, ev3) =>
                        {
                            fadeOutStep++;
                            double fadeProgress = EaseInOutQuart((double)fadeOutStep / fadeOutMaxSteps);
                            
                            // fade out
                            this.Opacity = 0.98 * (1 - fadeProgress);
                            
                            // slide up
                            int newY = _finalPosition.Y + (int)((double)(_endPosition.Y - _finalPosition.Y) * fadeProgress);
                            this.Location = new Point(_finalPosition.X, newY);
                            
                            if (fadeOutStep >= fadeOutMaxSteps)
                            {
                                _fadeOutTimer.Stop();
                                this.Close();
                            }
                        };
                        _fadeOutTimer.Start();
                    };
                    _delayTimer.Start();
                }
            };
            _animationTimer.Start();
        }
        
        // easing functions for animations
        private double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        private double EaseInCubic(double t) => t * t * t;
        private double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);
        private double EaseInOutQuart(double t) => t < 0.5 ? 8 * t * t * t * t : 1 - Math.Pow(-2 * t + 2, 4) / 2;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            
            // good rendering
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            
            // rounded corners
            using (GraphicsPath path = new GraphicsPath())
            {
                int radius = 12; // ios-style corners
                
                // avoid edge issues
                Rectangle rect = new Rectangle(1, 1, this.Width - 2, this.Height - 2);
                
                // path for shadow and fill
                path.AddRoundedRectangle(rect, radius);
                
                // shadow effect
                e.Graphics.DrawModernShadow(path, Color.FromArgb(60, 0, 0, 0), 4, 12, new Point(0, 3));
                
                // region slightly larger
                using (GraphicsPath regionPath = new GraphicsPath())
                {
                    Rectangle regionRect = new Rectangle(0, 0, this.Width, this.Height);
                    regionPath.AddRoundedRectangle(regionRect, radius);
                    this.Region = new Region(regionPath);
                }
                
                // fill background
                using (SolidBrush backgroundBrush = new SolidBrush(BackgroundColor))
                {
                    e.Graphics.FillPath(backgroundBrush, path);
                    
                    // fix edge artifacts
                    using (Pen bleedPen = new Pen(BackgroundColor, 2f))
                    {
                        e.Graphics.DrawPath(bleedPen, path);
                    }
                }
                
                // subtle border
                using (Pen borderPen = new Pen(Color.FromArgb(15, 255, 255, 255), 1))
                {
                    e.Graphics.DrawPath(borderPen, path);
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // window settings
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000;   // layered for transparency
                cp.ExStyle |= 0x08;      // always on top
                
                // prevent flickering
                cp.ExStyle |= 0x02000000; // composited
                
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // cleanup timers
                    _animationTimer?.Stop();
                    _animationTimer?.Dispose();
                    _animationTimer = null;
                    
                    _delayTimer?.Stop();
                    _delayTimer?.Dispose();
                    _delayTimer = null;
                    
                    _fadeOutTimer?.Stop();
                    _fadeOutTimer?.Dispose();
                    _fadeOutTimer = null;
                    
                    // cleanup controls
                    foreach (Control control in Controls)
                    {
                        if (control is PictureBox pb && pb.Image != null)
                        {
                            pb.Image.Dispose();
                            pb.Image = null;
                        }
                        control.Dispose();
                    }
                }
                
                // avoid double dispose
                _disposed = true;
            }
            
            base.Dispose(disposing);
        }
    }
}
