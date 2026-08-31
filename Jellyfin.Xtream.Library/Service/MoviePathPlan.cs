using System.Collections.Generic;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// The directory and file names a movie must use, as decided by the library database.
/// </summary>
/// <param name="Directory">The directory, relative to the library root.</param>
/// <param name="FileNames">
/// The file names to write, without extension, in the same order as the candidates supplied by
/// the caller.
/// </param>
/// <param name="IsExisting">
/// Whether the database already held rows for this stream in this target folder, meaning the
/// names were reused rather than assigned.
/// </param>
public sealed record MoviePathPlan(string Directory, IReadOnlyList<string> FileNames, bool IsExisting);
