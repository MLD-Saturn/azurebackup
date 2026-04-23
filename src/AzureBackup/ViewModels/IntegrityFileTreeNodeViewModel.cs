using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureBackup.Core.Models;

namespace AzureBackup.ViewModels;

/// <summary>
/// Tree node for the Data Integrity tab's file-selection panel (D2).
/// Mirrors <see cref="LocalFileTreeNodeViewModel"/>'s shape (checkbox
/// propagation via <see cref="TreeNodeViewModelBase{T}"/>) but the leaf
/// nodes carry a <see cref="BackedUpFile"/> rather than a local file
/// because the integrity check operates on the backed-up corpus, not
/// the local filesystem.
/// </summary>
/// <remarks>
/// Static <see cref="SelectionChanged"/> event is the bridge from
/// checkbox-toggle to ViewModel: when the user manually toggles a
/// checkbox, <see cref="DataIntegrityViewModel"/> resets the
/// time/history dropdown to "(Custom selection)" -- the bidirectional
/// sync rule from the design discussion.
/// </remarks>
public partial class IntegrityFileTreeNodeViewModel : TreeNodeViewModelBase<IntegrityFileTreeNodeViewModel>
{
    public static event System.EventHandler? SelectionChanged;

    protected override void OnSelectionPropagationComplete()
        => SelectionChanged?.Invoke(this, System.EventArgs.Empty);

    public override string Name { get; }
    public override string FullPath { get; }
    public override bool IsFolder { get; }

    /// <summary>
    /// The persisted <see cref="BackedUpFile"/> this leaf represents.
    /// Null for folder nodes. The integrity check uses
    /// <c>File.Id</c> as the FileId.
    /// </summary>
    public BackedUpFile? File { get; }

    /// <summary>
    /// Backed-up timestamp for time-filter matching. Folders carry the
    /// most recent of their descendants so a folder selects-on-time-filter
    /// as soon as any child file matches.
    /// </summary>
    public System.DateTime BackedUpAt { get; }

    private IntegrityFileTreeNodeViewModel(string name, string fullPath, bool isFolder,
        BackedUpFile? file, System.DateTime backedUpAt)
    {
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        File = file;
        BackedUpAt = backedUpAt;
    }

    /// <summary>
    /// Folder summary text shown next to the folder name. Counts files
    /// in the subtree to give the tester a sense of scope before they
    /// expand.
    /// </summary>
    public string FolderSummary
    {
        get
        {
            if (!IsFolder) return string.Empty;
            var fileCount = GetAllFiles().Count();
            return $"{fileCount} file{(fileCount == 1 ? "" : "s")}";
        }
    }

    public IEnumerable<IntegrityFileTreeNodeViewModel> GetAllFiles()
    {
        if (IsFile)
        {
            yield return this;
        }
        else
        {
            foreach (var child in Children)
                foreach (var f in child.GetAllFiles())
                    yield return f;
        }
    }

    /// <summary>
    /// Builds a directory-grouped tree from a flat list of backed-up files.
    /// Each file becomes a leaf; common path prefixes become folder nodes.
    /// </summary>
    public static List<IntegrityFileTreeNodeViewModel> BuildTree(IEnumerable<BackedUpFile> files)
    {
        // Group by top-level drive/root for clean visual separation.
        var roots = new Dictionary<string, IntegrityFileTreeNodeViewModel>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.OrderBy(f => f.LocalPath, System.StringComparer.OrdinalIgnoreCase))
        {
            var parts = SplitPath(file.LocalPath);
            if (parts.Length == 0) continue;

            var rootName = parts[0];
            if (!roots.TryGetValue(rootName, out var root))
            {
                root = new IntegrityFileTreeNodeViewModel(rootName, rootName,
                    isFolder: true, file: null, backedUpAt: file.BackedUpAt);
                roots[rootName] = root;
            }

            InsertFile(root, parts, 1, file);
        }

        return roots.Values.OrderBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void InsertFile(IntegrityFileTreeNodeViewModel parent, string[] parts, int index, BackedUpFile file)
    {
        if (index == parts.Length - 1)
        {
            // Leaf
            var leaf = new IntegrityFileTreeNodeViewModel(
                parts[index], file.LocalPath, isFolder: false, file: file, file.BackedUpAt)
            {
                Parent = parent
            };
            parent.Children.Add(leaf);
            return;
        }

        var folderName = parts[index];
        var folderPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(index + 1));
        var existing = parent.Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, folderName, System.StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new IntegrityFileTreeNodeViewModel(folderName, folderPath,
                isFolder: true, file: null, file.BackedUpAt)
            {
                Parent = parent
            };
            parent.Children.Add(existing);
        }
        InsertFile(existing, parts, index + 1, file);
    }

    private static string[] SplitPath(string path)
    {
        // Normalize separators then split. Drop empty segments from
        // leading separators. Keep the drive letter (e.g., "C:\\") as
        // a single root component.
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, System.StringSplitOptions.RemoveEmptyEntries);
        return parts;
    }
}
