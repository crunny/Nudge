// Nudge - Clipboard notification utility
// Copyright (c) 2025 crunny
// All rights reserved.
namespace Nudge
{
    using System;
    using System.Windows.Forms;
    using System.Drawing;
    using System.Runtime.InteropServices;
    using Microsoft.Win32;
    using System.IO;
    using System.Reflection;
    using System.Security;

    // watches for clipboard changes
    public class ClipboardMonitor : IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private bool _disposed;
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private readonly IntPtr _formHandle;
        private readonly Action _clipboardChangedAction;
        
        public ClipboardMonitor(IntPtr formHandle, Action clipboardChangedAction)
        {
            _formHandle = formHandle != IntPtr.Zero ? formHandle : 
                throw new ArgumentNullException(nameof(formHandle));
            _clipboardChangedAction = clipboardChangedAction ?? 
                throw new ArgumentNullException(nameof(clipboardChangedAction));
            
            // start listening
            if (!AddClipboardFormatListener(_formHandle))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to add clipboard format listener. Error code: {error}");
            }
        }
        
        public bool ProcessClipboardMessage(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                _clipboardChangedAction();
                return true;
            }
            
            return false;
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // nothing to do here
                }
                
                // stop listening
                if (_formHandle != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(_formHandle);
                }
                
                _disposed = true;
            }
        }
        
        ~ClipboardMonitor()
        {
            Dispose(false);
        }
    }

    public static class Program
    {
        private static NotifyIcon? trayIcon;
        private static ClipboardMonitor? clipboardMonitor;
        private const string AppName = "Nudge";
        
        // Added for throttling notifications
        private static DateTime _lastNotificationTime = DateTime.MinValue;
        private const int NOTIFICATION_COOLDOWN_MS = 300; // 300ms cooldown between notifications
        
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            
            try
            {
                // make tray icon
                trayIcon = new NotifyIcon();
                LoadApplicationIcon();
                
                trayIcon.Text = "Nudge - Clipboard Notifier";
                trayIcon.Visible = true;

                // setup context menu
                ContextMenuStrip trayMenu = new ContextMenuStrip();
                
                // startup toggle
                bool startupEnabled = IsStartupEnabled();
                ToolStripMenuItem startupMenuItem = new ToolStripMenuItem(
                    startupEnabled ? "Disable Startup with Windows" : "Enable Startup with Windows");
                startupMenuItem.Checked = startupEnabled;
                startupMenuItem.Click += OnToggleStartup;
                
                trayMenu.Items.Add(startupMenuItem);
                trayMenu.Items.Add("-"); // divider
                trayMenu.Items.Add("Exit", null, (sender, e) => { Application.Exit(); });

                trayIcon.ContextMenuStrip = trayMenu;

                // make a form to handle messages
                using (MessageLoopForm messageLoop = new MessageLoopForm())
                {
                    // hook up clipboard watcher
                    clipboardMonitor = new ClipboardMonitor(messageLoop.Handle, HandleClipboardChange);

                    messageLoop.SetClipboardMonitor(clipboardMonitor);

                    // hello notification
                    trayIcon.ShowBalloonTip(3000, AppName, "Running in background.", ToolTipIcon.None);

                    // run the app
                    Application.Run(messageLoop);
                }
            }
            finally
            {
                // cleanup when done
                clipboardMonitor?.Dispose();
                clipboardMonitor = null;
                
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            }
        }
        
        private static void LoadApplicationIcon()
        {
            try
            {
                // try assets folder first
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Nudge.ico");
                
                // try base dir next
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nudge.ico");
                }
                
                // try current dir as last resort
                if (!File.Exists(iconPath))
                {
                    iconPath = "Nudge.ico"; 
                }
                
                // load the icon
                if (File.Exists(iconPath))
                {
                    using (Icon appIcon = new Icon(iconPath))
                    {
                        trayIcon!.Icon = (Icon)appIcon.Clone();
                    }
                }
                else
                {
                    // fallback to exe icon
                    trayIcon!.Icon = Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "Nudge.exe")) 
                        ?? SystemIcons.Application;
                }
            }
            catch (Exception ex)
            {
                // emergency fallback
                System.Diagnostics.Debug.WriteLine($"Error loading icon: {ex.Message}");
                trayIcon!.Icon = SystemIcons.Application;
            }
        }
        
        private static void HandleClipboardChange()
        {
            try 
            {
                if (Clipboard.ContainsText() || Clipboard.ContainsImage() || 
                    Clipboard.ContainsFileDropList() || Clipboard.ContainsAudio())
                {
                    // Throttle notifications
                    if ((DateTime.Now - _lastNotificationTime).TotalMilliseconds >= NOTIFICATION_COOLDOWN_MS)
                    {
                        ShowNotification();
                        _lastNotificationTime = DateTime.Now;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Skipped notification due to cooldown at {DateTime.Now}");
                    }
                }
            }
            catch (ExternalException ex)
            {
                // someone else using clipboard
                System.Diagnostics.Debug.WriteLine($"Clipboard access error: {ex.Message}");
            }
            catch (Exception ex) 
            {
                // something else broke
                System.Diagnostics.Debug.WriteLine($"Error processing clipboard: {ex.Message}");
            }
        }

        public static void ShowNotification()
        {
            try
            {
                // popup the notification
                NudgeNotification notification = new NudgeNotification();
                
                notification.Show();
                notification.BringToFront(); // make sure it's on top
                notification.Activate();
                
                // debug info
                System.Diagnostics.Debug.WriteLine($"Notification shown at {DateTime.Now}: Location={notification.Location}, Opacity={notification.Opacity}");
            }
            catch (Exception ex)
            {
                // log what went wrong
                System.Diagnostics.Debug.WriteLine($"Error showing notification: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // fallback to tray notification
                if (trayIcon != null && trayIcon.Visible)
                {
                    trayIcon.ShowBalloonTip(3000, AppName, "Error displaying notification.", ToolTipIcon.Error);
                }
            }
        }

        private static void OnToggleStartup(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem item)
            {
                bool isEnabled = item.Checked;
                
                // flip the setting
                if (isEnabled)
                {
                    DisableStartup();
                    item.Checked = false;
                    item.Text = "Enable Startup with Windows";
                    trayIcon?.ShowBalloonTip(3000, AppName, "Startup with Windows disabled.", ToolTipIcon.None);
                }
                else
                {
                    EnableStartup();
                    item.Checked = true;
                    item.Text = "Disable Startup with Windows";
                    trayIcon?.ShowBalloonTip(3000, AppName, "Startup with Windows enabled.", ToolTipIcon.None);
                }
            }
        }

        private static void EnableStartup()
        {
            try
            {
                RegistryKey? rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (rk != null)
                {
                    string appPath = Application.ExecutablePath;
                    rk.SetValue(AppName, appPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error enabling startup: {ex.Message}", AppName, 
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void DisableStartup()
        {
            try
            {
                RegistryKey? rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (rk != null)
                {
                    rk.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error disabling startup: {ex.Message}", AppName, 
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                RegistryKey? rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                return rk != null && rk.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            // cleanup time
            NudgeNotification.CleanupCachedResources();
            
            clipboardMonitor?.Dispose();
            clipboardMonitor = null;
            
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }
    }

    // invisible form to handle messages
    public class MessageLoopForm : Form
    {
        private ClipboardMonitor? _clipboardMonitor;
        
        public MessageLoopForm()
        {
            // make the form invisible
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.Size = new Size(1, 1);
            this.Opacity = 0;
            
            // hide off-screen
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(-10000, -10000);
        }
        
        public void SetClipboardMonitor(ClipboardMonitor monitor)
        {
            _clipboardMonitor = monitor;
        }
        
        protected override void SetVisibleCore(bool value)
        {
            // never visible
            base.SetVisibleCore(false);
        }
        
        protected override void WndProc(ref Message m)
        {
            // check for clipboard stuff
            if (_clipboardMonitor != null && _clipboardMonitor.ProcessClipboardMessage(ref m))
            {
                return;
            }
                        
            base.WndProc(ref m);
        }
    }
}
