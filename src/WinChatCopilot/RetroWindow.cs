using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinChatCopilot;

public class RetroWindow : Window
{
    private ContextMenu? systemMenu;
    private (int Timestamp, Point Position)? lastSystemBoxPress;

    public RetroWindow()
    {
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => Close(), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => WindowState = WindowState.Minimized,
            (_, e) => e.CanExecute = CanResize && WindowState != WindowState.Minimized));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => WindowState = WindowState.Maximized,
            (_, e) => e.CanExecute = CanResize && WindowState != WindowState.Maximized));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => WindowState = WindowState.Normal,
            (_, e) => e.CanExecute = CanResize && WindowState != WindowState.Normal));
        CommandBindings.Add(new CommandBinding(ShellCommands.Move,
            (_, _) => BeginSystemOperation(0xF010),
            (_, e) => e.CanExecute = WindowState == WindowState.Normal));
        CommandBindings.Add(new CommandBinding(ShellCommands.Size,
            (_, _) => BeginSystemOperation(0xF000),
            (_, e) => e.CanExecute = CanResize && WindowState == WindowState.Normal));
        CommandBindings.Add(new CommandBinding(ShellCommands.SystemMenu,
            (_, _) => OpenSystemMenu(), (_, e) => e.CanExecute = true));
    }

    private bool CanResize => ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetDwmAttribute(handle, 2, 1);
        // Windows 10 does not support the Windows 11 corner preference attribute.
        SetDwmAttribute(handle, 33, 1, allowUnsupported: true);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.Space &&
            Keyboard.Modifiers == ModifierKeys.Alt)
        {
            OpenSystemMenu();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source)
        {
            for (var current = source; current is not null;
                 current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is Button { Tag: "SystemMenu" } button)
                {
                    if (!TryCloseSystemBoxPress(e))
                        lastSystemBoxPress = (e.Timestamp, e.GetPosition(button));
                    break;
                }
                if (current == this) break;
            }
        }
        base.OnPreviewMouseDown(e);
    }

    private bool TryCloseSystemBoxPress(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || lastSystemBoxPress is not { } first ||
            Template.FindName("SystemMenuButton", this) is not Button button)
            return false;
        var position = e.GetPosition(button);
        var dpi = VisualTreeHelper.GetDpi(button);
        if (!IsSecondSystemBoxPress(first.Timestamp, e.Timestamp, first.Position, position,
                new Size(button.ActualWidth, button.ActualHeight), GetDoubleClickTime(),
                new Size(GetSystemMetricsForDpi(36, (uint)(96 * dpi.DpiScaleX)) / dpi.DpiScaleX,
                    GetSystemMetricsForDpi(37, (uint)(96 * dpi.DpiScaleY)) / dpi.DpiScaleY)))
            return false;
        lastSystemBoxPress = null;
        systemMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        SystemCommands.CloseWindowCommand.Execute(null, this);
        e.Handled = true;
        return true;
    }

    internal static bool IsSecondSystemBoxPress(int firstTime, int nextTime, Point first, Point next,
        Size buttonSize, uint doubleClickTime, Size doubleClickArea) =>
        unchecked((uint)(nextTime - firstTime)) <= doubleClickTime &&
        first.X >= 0 && first.Y >= 0 && first.X < buttonSize.Width && first.Y < buttonSize.Height &&
        next.X >= 0 && next.Y >= 0 && next.X < buttonSize.Width && next.Y < buttonSize.Height &&
        Math.Abs(next.X - first.X) <= doubleClickArea.Width / 2 &&
        Math.Abs(next.Y - first.Y) <= doubleClickArea.Height / 2;

    private void OpenSystemMenu()
    {
        ApplyTemplate();
        var button = (Button)Template.FindName("SystemMenuButton", this);
        systemMenu ??= CreateSystemMenu();
        systemMenu.PlacementTarget = button;
        systemMenu.IsOpen = true;
    }

    private ContextMenu CreateSystemMenu()
    {
        var menu = new ContextMenu { Resources = Resources };
        // The first click opens a popup that can capture the second click away from the window.
        menu.AddHandler(Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, e) => TryCloseSystemBoxPress(e)), handledEventsToo: true);
        menu.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent,
            new MouseButtonEventHandler((_, e) => TryCloseSystemBoxPress(e)), handledEventsToo: true);
        menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
        Add("_Restore", SystemCommands.RestoreWindowCommand);
        Add("_Move", ShellCommands.Move);
        Add("_Size", ShellCommands.Size);
        Add("Mi_nimize", SystemCommands.MinimizeWindowCommand);
        Add("Ma_ximize", SystemCommands.MaximizeWindowCommand);
        menu.Items.Add(new Separator());
        Add("_Close", SystemCommands.CloseWindowCommand, "Alt+F4");
        return menu;

        void Add(string header, ICommand command, string gesture = "")
        {
            var item = new MenuItem { Header = header, Command = command, CommandTarget = this,
                InputGestureText = gesture };
            item.SetResourceReference(StyleProperty, typeof(MenuItem));
            menu.Items.Add(item);
        }
    }

    private void BeginSystemOperation(int operation)
    {
        systemMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        SendMessage(new WindowInteropHelper(this).Handle, 0x0112, new IntPtr(operation), IntPtr.Zero);
    }

    private static void SetDwmAttribute(IntPtr handle, int attribute, int value, bool allowUnsupported = false)
    {
        var result = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        if (allowUnsupported && result == unchecked((int)0x80070057))
            return;
        Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
