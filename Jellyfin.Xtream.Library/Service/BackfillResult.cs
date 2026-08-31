namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Outcome of a backfill pass.
/// </summary>
/// <param name="FilesSeen">How many <c>.strm</c> files were found.</param>
/// <param name="RowsCreated">How many rows were written.</param>
/// <param name="Unattributed">
/// How many files could not be attributed to a provider, either because the URL was unreadable or
/// because no configured provider matches it. Rows are still created for these so that their names
/// stay reserved.
/// </param>
/// <param name="Failed">How many files could not be read at all.</param>
public sealed record BackfillResult(int FilesSeen, int RowsCreated, int Unattributed, int Failed)
{
    /// <summary>
    /// Gets a value indicating whether the pass covered the whole tree without losing a file. Only
    /// a complete pass may mark the database as backfilled, because a partial one would make the
    /// files it missed look like orphans on the next reconciliation.
    /// </summary>
    public bool IsComplete => Failed == 0;
}
