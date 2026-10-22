namespace SharpRail.Host.Core;

internal interface ITerminalProcess : IAsyncDisposable
{
    string Id { get; }
    int Pid { get; }
    string? CurrentDirectory { get; }
    bool IsBusy { get; }
    Task<int> Exit { get; }
    // Each chunk is owned by the reader, which releases it once nothing it handed the chunk to still reads it.
    IAsyncEnumerable<TerminalChunk> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    ValueTask KillAsync(CancellationToken cancellationToken = default);
}