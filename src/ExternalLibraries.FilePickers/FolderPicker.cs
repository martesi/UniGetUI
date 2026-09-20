using ExternalLibraries.Pickers.Classes;
using ExternalLibraries.Pickers.Enums;

namespace ExternalLibraries.Pickers;

/// <summary>
/// Class responsible for folder pick dialog.
/// </summary>
public class FolderPicker
{
    /// <summary>
    /// Window handle where dialog should appear.
    /// </summary>
    private readonly IntPtr _windowHandle;

    public string? InitialDirectory { get; set; }

    /// <summary>
    /// Folder pick dialog.
    /// </summary>
    /// <param name="windowHandle">Window handle where dialog should appear.</param>
    public FolderPicker(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>
    /// Shows folder pick dialog.
    /// </summary>
    /// <returns>Path to selected folder or empty string.</returns>
    public string Show()
    {
        return Helper.ShowOpen(_windowHandle, FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM, initialDirectory: InitialDirectory);
    }
}
