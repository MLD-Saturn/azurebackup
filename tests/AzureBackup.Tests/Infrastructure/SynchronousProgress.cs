namespace AzureBackup.Tests;

/// <summary>
/// An <see cref="IProgress{T}"/> implementation that invokes the callback synchronously
/// on the calling thread. Unlike <see cref="Progress{T}"/>, which posts callbacks via
/// <see cref="SynchronizationContext"/> (or the thread pool when no context exists),
/// this ensures the callback has executed by the time <see cref="Report"/> returns.
/// Use in tests to avoid races between assertions and pending thread-pool callbacks.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
