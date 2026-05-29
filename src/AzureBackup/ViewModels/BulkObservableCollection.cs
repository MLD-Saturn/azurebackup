using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with a bulk-replace primitive that
/// raises a single <see cref="NotifyCollectionChangedAction.Reset"/> event for
/// the whole batch, instead of one event per item.
///
/// <para>
/// Use this when:
/// </para>
/// <list type="bullet">
///   <item>The bound view is a virtualised list/tree that re-evaluates its
///     viewport on every <see cref="NotifyCollectionChangedAction.Add"/>
///     (Avalonia's <c>ItemsControl</c> behaves this way).</item>
///   <item>You are doing a "Clear + foreach Add" in a hot path where the
///     N item-add events dominate cost (e.g. the Logs drain or
///     <c>RefreshFromAzureAsync</c> rebuilding 50K rows).</item>
/// </list>
///
/// <para>
/// Trade-off: callers lose per-item delta information. Bound controls that
/// animate Add/Remove will not animate the bulk change.
/// </para>
///
/// <para>
/// <b>DO NOT use <see cref="ReplaceAll"/> for a collection bound to an
/// Avalonia <c>TreeView</c> with a <c>HierarchicalDataTemplate</c> if the
/// host control may be collapsed (<c>IsVisible="False"</c>) at the time
/// of the call.</b> Avalonia's <c>TreeView</c> drops
/// <see cref="NotifyCollectionChangedAction.Reset"/> events that arrive
/// before its first layout pass, leaving the visible tree empty even
/// though the collection is populated. The startup-unlock sequence in
/// <c>MainWindowViewModel.TryUnlockWithPasswordAsync</c> is exactly this
/// pattern -- the local-files tree is filled while <c>SyncView</c> is
/// still hidden, then revealed by <c>CurrentView = "Sync"</c> after the
/// unlock returns. Use <see cref="System.Collections.ObjectModel.Collection{T}.Clear"/>
/// + <c>foreach</c> <see cref="System.Collections.ObjectModel.Collection{T}.Add"/>
/// for tree-root collections instead; the per-item Add events are picked
/// up correctly when the <c>TreeView</c> later measures, and tree-root
/// counts are usually small enough that the N-event cost is irrelevant
/// (e.g. one node per watched folder in
/// <c>MainWindowViewModel.LocalFileTreeRoots</c>). <c>ListBox</c>-bound
/// flat collections are unaffected by this footgun. Bug discovered and
/// fixed in B28.
/// </para>
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the collection contents with <paramref name="items"/> and
    /// raises a single Reset event. Use for batch rebuilds.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
