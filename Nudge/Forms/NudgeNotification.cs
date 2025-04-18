// Nudge - Clipboard notification utility
// Copyright (c) 2025 crunny
// All rights reserved.
using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.IO;

namespace Nudge
{
    // extension for making rounded rectangles
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
    }

    public class NudgeNotification : Form
    {
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
        
        private static readonly int DELAY_BEFORE_FADEOUT = 1500; // 1.5 seconds
        private readonly string _clipboardContent;
        private Point _finalPosition;
        private Point _startPosition;
        private Point _endPosition;
        private System.Windows.Forms.Timer? _animationTimer;
        private System.Windows.Forms.Timer? _delayTimer;
        private System.Windows.Forms.Timer? _fadeOutTimer;
        private bool _disposed;
        
        // p/invoke stuff for drop shadow
        [DllImport("user32.dll")]
        private static extern int SetClassLong(IntPtr hwnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        private static extern int GetClassLong(IntPtr hwnd, int nIndex);
        
        private const int GCL_STYLE = -26;
        private const int CS_DROPSHADOW = 0x20000;

        public NudgeNotification()
        {
            // get clipboard content for the notification
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
                        // cut off long text
                        if (text.Length > 50)
                        {
                            _clipboardContent = text.Substring(0, 47) + "...";
                        }
                        else
                        {
                            _clipboardContent = text;
                        }
                        
                        // replace control chars that mess up display
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
                // clipboard access error
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
            this.BackColor = Color.FromArgb(58, 58, 60); // dark background
            this.ForeColor = Color.White;
            this.Opacity = 0; // start invisible to avoid flashing
            this.Width = 320; 
            this.Height = 90; 
            
            // smooth rendering
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | 
                          ControlStyles.UserPaint | 
                          ControlStyles.DoubleBuffer | 
                          ControlStyles.OptimizedDoubleBuffer, true);
            
            // multi-screen support - find active screen
            Screen activeScreen = GetActiveScreen();
            int screenWidth = activeScreen.WorkingArea.Width;
            int screenHeight = activeScreen.WorkingArea.Height;
            
            int centerX = activeScreen.WorkingArea.Left + (screenWidth - this.Width) / 2;
            
            // positions for animation
            _finalPosition = new Point(centerX, activeScreen.WorkingArea.Top + 40);
            _startPosition = new Point(centerX, activeScreen.WorkingArea.Top - this.Height); // start above screen
            _endPosition = new Point(centerX, activeScreen.WorkingArea.Top - this.Height); // end above screen
            
            // start off-screen
            this.Location = _startPosition;

            // content panel layout
            Panel contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15),
                BackColor = Color.Transparent
            };
            this.Controls.Add(contentPanel);

            // add icon and text
            AddNotificationContent(contentPanel);

            // animation setup
            this.Load += OnFormLoad;
            
            // add drop shadow when form is created
            this.HandleCreated += (s, e) => {
                SetClassLong(this.Handle, GCL_STYLE, GetClassLong(this.Handle, GCL_STYLE) | CS_DROPSHADOW);
            };
        }
        
        // find the screen with active window or mouse
        private Screen GetActiveScreen()
        {
            try
            {
                // try to get screen with active window
                IntPtr activeWindowHandle = GetForegroundWindow();
                if (activeWindowHandle != IntPtr.Zero)
                {
                    if (GetWindowRect(activeWindowHandle, out RECT windowRect))
                    {
                        // find screen with center of active window
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
                
                // try to get screen with mouse cursor
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
            // app icon - centered vertically
            PictureBox iconBox = new PictureBox
            {
                Size = new Size(40, 40),
                Location = new Point(12, 25),
                BackColor = Color.Transparent,
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            
            // use cached icon or create new one
            if (_cachedClipboardIcon == null)
            {
                lock (_cacheLock)
                {
                    // double-check to avoid race conditions
                    if (_cachedClipboardIcon == null)
                    {
                        // use the logo png instead of icon
                        _cachedClipboardIcon = CreateIconFromAppLogo();
                    }
                }
            }
            
            // clone image for this notification
            iconBox.Image = (Image)_cachedClipboardIcon.Clone();
            contentPanel.Controls.Add(iconBox);

            // container for text elements
            Panel textContainer = new Panel
            {
                Size = new Size(this.Width - 75, this.Height - 30),
                Location = new Point(62, 20),
                BackColor = Color.Transparent,
            };
            contentPanel.Controls.Add(textContainer);

            // create fonts
            Font headerFont = CreatePoppinsFont(12f, FontStyle.Bold);
            Font contentFont = CreatePoppinsFont(11f, FontStyle.Regular);

            // header label
            Label lblHeader = new Label
            {
                Text = "Nudge - Copied",
                Font = headerFont,
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(0, 0),
                BackColor = Color.Transparent,
            };
            textContainer.Controls.Add(lblHeader);

            // content label
            Label lblContent = new Label
            {
                Text = _clipboardContent,
                Font = contentFont,
                ForeColor = Color.FromArgb(230, 230, 230),
                AutoSize = false,
                Size = new Size(this.Width - 80, 35),
                Location = new Point(0, 26),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };
            textContainer.Controls.Add(lblContent);
        }
        
        // create poppins font with fallbacks
        private Font CreatePoppinsFont(float size, FontStyle style)
        {
            try
            {
                // check if poppins is installed
                using (var fontCollection = new System.Drawing.Text.PrivateFontCollection())
                {
                    FontFamily[] families = FontFamily.Families;
                    foreach (FontFamily family in families)
                    {
                        if (family.Name.Equals("Poppins", StringComparison.OrdinalIgnoreCase))
                        {
                            return new Font("Poppins", size, style, GraphicsUnit.Point);
                        }
                    }
                    
                    // font not found, use segoe ui instead
                    return new Font("Segoe UI", size, style, GraphicsUnit.Point);
                }
            }
            catch
            {
                // fallback if something breaks
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }

        // create icon from app icon
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
                
                // fallback to system icon
                return new Bitmap(SystemIcons.Application.ToBitmap(), 24, 24);
            }
        }

        // create icon from logo png
        private static Image CreateIconFromAppLogo()
        {
            try
            {
                // look for png in assets folder
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "NudgeLogo.png");
                
                // if not found, check project folder
                if (!File.Exists(logoPath))
                {
                    // get exe dir
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    
                    // navigate up to find project root
                    DirectoryInfo? dir = new DirectoryInfo(exeDir);
                    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Assets", "NudgeLogo.png")))
                    {
                        dir = dir.Parent;
                    }
                    
                    // if found the parent dir with assets folder
                    if (dir != null)
                    {
                        logoPath = Path.Combine(dir.FullName, "Assets", "NudgeLogo.png");
                    }
                }
                
                // if png exists, load it
                if (File.Exists(logoPath))
                {
                    using (Image originalImage = Image.FromFile(logoPath))
                    {
                        // create a resized copy - 40x40
                        Bitmap resizedImage = new Bitmap(40, 40);
                        using (Graphics g = Graphics.FromImage(resizedImage))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(originalImage, 0, 0, 40, 40);
                        }
                        return resizedImage;
                    }
                }
                
                // fallback if png not found
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
            // position form before showing
            this.Location = _startPosition;
            
            _animationTimer = new System.Windows.Forms.Timer
            {
                Interval = 10 // higher framerate = smoother
            };
            
            int animationStep = 0;
            int maxAnimationSteps = 20; // more steps = smoother
            
            _animationTimer.Tick += (s, ev) =>
            {
                // calculate fade and position with easing
                animationStep++;
                double progress = EaseOutCubic((double)animationStep / maxAnimationSteps);
                
                // increase opacity as we animate
                this.Opacity = Math.Min(0.95, progress);
                
                // position with cubic easing
                int newY = _startPosition.Y + (int)((double)(_finalPosition.Y - _startPosition.Y) * progress);
                this.Location = new Point(_startPosition.X, newY);
                
                if (animationStep >= maxAnimationSteps)
                {
                    _animationTimer.Stop();
                    this.Location = _finalPosition;
                    this.Opacity = 0.95;
                    
                    // wait before fading out
                    _delayTimer = new System.Windows.Forms.Timer
                    {
                        Interval = DELAY_BEFORE_FADEOUT
                    };
                    
                    _delayTimer.Tick += (s2, ev2) =>
                    {
                        _delayTimer.Stop();
                        
                        _fadeOutTimer = new System.Windows.Forms.Timer
                        {
                            Interval = 10
                        };
                        
                        int fadeOutStep = 0;
                        int fadeOutMaxSteps = 20;
                        
                        _fadeOutTimer.Tick += (s3, ev3) =>
                        {
                            fadeOutStep++;
                            double fadeProgress = EaseInCubic((double)fadeOutStep / fadeOutMaxSteps);
                            
                            // fade out
                            this.Opacity = 0.95 * (1 - fadeProgress);
                            
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
        
        // easing for smooth animations
        private double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        private double EaseInCubic(double t) => t * t * t;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            
            // high-quality rendering
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            
            // create rounded corners
            using (GraphicsPath path = new GraphicsPath())
            {
                int radius = 6; // subtle corners
                Rectangle rect = new Rectangle(0, 0, this.Width, this.Height);
                
                // rounded rect
                path.AddRoundedRectangle(rect, radius);
                
                // set form region
                this.Region = new Region(path);
                
                // solid background
                using (SolidBrush backgroundBrush = new SolidBrush(Color.FromArgb(230, 45, 45, 48)))
                {
                    e.Graphics.FillPath(backgroundBrush, path);
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // config for smooth rendering
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000; // WS_EX_LAYERED - for transparency
                cp.ExStyle |= 0x08;    // WS_EX_TOPMOST - always on top
                
                // avoid flickering
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                
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
                
                // prevent double disposal
                _disposed = true;
            }
            
            base.Dispose(disposing);
        }
    }
}
