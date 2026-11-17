using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace NHL27Kit.App;

/// <summary>
/// The Open dialog for images, everywhere in the app: it starts in the folder the last image was opened from (any page,
/// remembered in settings.json), so loading a school's files one after another stays in that school's folder instead of
/// jumping back to the team the page has picked. A page's own folder is only the first-time fallback.
/// </summary>
internal static class ImagePicker
{
    /// <summary>Shows the dialog; the chosen file, or null when cancelled.</summary>
    public static string Open(Window owner, string title, string filter = null, string fallback = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter ?? ImageLoader.Filter };
        var settings = Settings.Load();
        if (!string.IsNullOrEmpty(settings.ImageFolder) && Directory.Exists(settings.ImageFolder)) dlg.InitialDirectory = settings.ImageFolder;
        else if (!string.IsNullOrEmpty(fallback) && Directory.Exists(fallback)) dlg.InitialDirectory = fallback;
        if (dlg.ShowDialog(owner) != true) return null;
        settings = Settings.Load();
        settings.ImageFolder = Path.GetDirectoryName(Path.GetFullPath(dlg.FileName));
        settings.Save();
        return dlg.FileName;
    }
}
