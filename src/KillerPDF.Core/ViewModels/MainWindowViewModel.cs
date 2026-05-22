using CommunityToolkit.Mvvm.ComponentModel;

namespace KillerPDF;

/// <summary>
/// View-model for the main window. Holds UI-agnostic state and exposes commands.
/// Currently a skeleton — properties and commands will be migrated from
/// MainWindow.xaml.cs code-behind progressively in Phase 5.
///
/// Both the WPF and (forthcoming) Avalonia main windows bind to an instance of this
/// class. Platform-specific concerns (dialogs, file pickers, browser launches) are
/// injected via interfaces defined alongside in KillerPDF.Core.Services.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// True when the current document has unsaved annotation/structural changes.
    /// </summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// Absolute path to the currently-open PDF, or null if no document is open.
    /// Working-copy path (may be a temp file mirror used to allow in-place edits
    /// without locking the original).
    /// </summary>
    [ObservableProperty]
    private string? _currentFile;

    /// <summary>
    /// Status bar message shown at the bottom of the window. Empty by default.
    /// </summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    /// The currently-active editing tool (Select/Text/Highlight/Draw/...).
    /// </summary>
    [ObservableProperty]
    private EditTool _currentTool = EditTool.Select;

    /// <summary>
    /// True if the page list sidebar is expanded. False = collapsed to the toggle strip.
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarOpen = true;

    /// <summary>
    /// Zoom level (1.0 = 100%). Changes drive page re-rendering at appropriate DPI.
    /// </summary>
    [ObservableProperty]
    private double _zoomLevel = 1.0;
}
