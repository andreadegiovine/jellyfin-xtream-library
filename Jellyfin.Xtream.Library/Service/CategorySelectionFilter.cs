// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Decides which provider categories take part in a sync, given the per-provider selection
/// (<see cref="Jellyfin.Xtream.Library.ProviderConfig.SelectedVodCategoryIds"/> /
/// <see cref="Jellyfin.Xtream.Library.ProviderConfig.SelectedSeriesCategoryIds"/>) and the mode
/// (<see cref="Jellyfin.Xtream.Library.ProviderConfig.MovieCategoriesMode"/> /
/// <see cref="Jellyfin.Xtream.Library.ProviderConfig.SeriesCategoriesMode"/>).
/// <para>
/// The agreed behaviour (GitHub #76) is:
/// </para>
/// <list type="bullet">
/// <item><description>Include, nothing selected: sync everything.</description></item>
/// <item><description>Include, categories selected: sync only those.</description></item>
/// <item><description>Exclude, nothing selected: sync everything (excluding nothing excludes nothing).</description></item>
/// <item><description>Exclude, categories selected: sync everything except those, so categories the
/// provider adds later are picked up automatically.</description></item>
/// </list>
/// </summary>
internal static class CategorySelectionFilter
{
    /// <summary>
    /// The mode value that inverts the selection. Any other value, including null and the
    /// empty string, is treated as Include so that configs written before the mode existed
    /// keep their original behaviour.
    /// </summary>
    public const string ExcludeMode = "Exclude";

    /// <summary>
    /// Returns true when the configured mode means "exclude the selected categories".
    /// </summary>
    /// <param name="mode">The configured mode, may be null on configs predating the setting.</param>
    /// <returns>True for Exclude, false for anything else.</returns>
    public static bool IsExcludeMode(string? mode)
        => string.Equals(mode, ExcludeMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a lookup set from a selection array. Null or empty input yields an empty set.
    /// </summary>
    /// <param name="selectedIds">The configured category IDs, may be null.</param>
    /// <returns>A hash set of the selected IDs.</returns>
    public static HashSet<int> BuildSet(int[]? selectedIds)
    {
        if (selectedIds == null || selectedIds.Length == 0)
        {
            return new HashSet<int>();
        }

        return new HashSet<int>(selectedIds);
    }

    /// <summary>
    /// Returns true when the given category should take part in the sync.
    /// An empty selection means "sync everything" in both modes.
    /// </summary>
    /// <param name="selectedSet">The selection set from <see cref="BuildSet"/>.</param>
    /// <param name="categoryId">The provider category ID to test.</param>
    /// <param name="exclude">True when the selection is an exclusion list, see <see cref="IsExcludeMode"/>.</param>
    /// <returns>True if the category should be synced.</returns>
    public static bool ShouldSync(HashSet<int> selectedSet, int categoryId, bool exclude)
    {
        ArgumentNullException.ThrowIfNull(selectedSet);

        if (selectedSet.Count == 0)
        {
            return true;
        }

        return exclude ? !selectedSet.Contains(categoryId) : selectedSet.Contains(categoryId);
    }

    /// <summary>
    /// Returns true when the selection actually narrows the catalogue down. This is false when
    /// nothing is selected, because then every category is in scope and callers that only make
    /// sense on a reduced set (such as the name-based series retry) should not run at all.
    /// </summary>
    /// <param name="selectedIds">The configured category IDs, may be null.</param>
    /// <returns>True if at least one category ID is configured.</returns>
    public static bool NarrowsSelection(int[]? selectedIds)
        => selectedIds != null && selectedIds.Length > 0;
}

/// <summary>
/// A resolved category selection: which IDs, which mode, and - the part the helpers above cannot
/// express - what an empty selection means.
/// <para>
/// A selection ticked in the flat category list means "sync everything" when it is empty, which is
/// the behaviour agreed in GitHub #76. A selection derived from Multiple folder mode mappings means
/// "sync nothing" when it is empty, because there the folder assignment <em>is</em> the filter: no
/// folder holds a category, so no category has anywhere to go. Overloading the empty array for both
/// is what made a config meant to sync a handful of categories ingest the provider's entire
/// catalogue into the library root (GitHub #78).
/// </para>
/// </summary>
internal sealed class CategorySelection
{
    private readonly HashSet<int> _ids;
    private readonly bool _exclude;
    private readonly bool _emptyMeansNothing;

    private CategorySelection(HashSet<int> ids, bool exclude, bool emptyMeansNothing)
    {
        _ids = ids;
        _exclude = exclude;
        _emptyMeansNothing = emptyMeansNothing;
    }

    /// <summary>
    /// Gets a value indicating whether this selection matches no category at all, making the sync
    /// a deliberate no-op rather than an unfiltered one.
    /// </summary>
    public bool SyncsNothing => _emptyMeansNothing && _ids.Count == 0;

    /// <summary>
    /// Gets a value indicating whether the selection narrows the catalogue down, so that callers
    /// which only make sense on a reduced set should run. Deliberately false for a
    /// <see cref="SyncsNothing"/> selection: those callers would query the provider only to filter
    /// the result down to nothing.
    /// </summary>
    public bool NarrowsSelection => _ids.Count > 0;

    /// <summary>
    /// Gets the configured category IDs, for reporting which of them the provider does not offer.
    /// </summary>
    public IReadOnlyCollection<int> ConfiguredIds => _ids;

    /// <summary>
    /// Builds a selection from the flat category list. An empty selection syncs everything, in both
    /// Include and Exclude mode.
    /// </summary>
    /// <param name="selectedIds">The configured category IDs, may be null.</param>
    /// <param name="mode">The configured Include/Exclude mode, may be null on older configs.</param>
    /// <returns>The resolved selection.</returns>
    public static CategorySelection FromCategoryList(int[]? selectedIds, string? mode)
        => new(
            CategorySelectionFilter.BuildSet(selectedIds),
            CategorySelectionFilter.IsExcludeMode(mode),
            emptyMeansNothing: false);

    /// <summary>
    /// Builds a selection from Multiple folder mode mappings. An empty selection syncs nothing.
    /// <para>
    /// The Include/Exclude mode is deliberately ignored. The config page hides it in Multiple folder
    /// mode and force-writes Include, but a hand-edited config file can still say Exclude, and
    /// honouring that would sync every category which has no folder and drop it in the library
    /// root. A folder mapping enumerates where content goes, so it is an inclusion list by
    /// construction.
    /// </para>
    /// </summary>
    /// <param name="categoryIds">The category IDs assigned to a folder.</param>
    /// <returns>The resolved selection.</returns>
    public static CategorySelection FromFolderMappings(IEnumerable<int> categoryIds)
        => new(new HashSet<int>(categoryIds), exclude: false, emptyMeansNothing: true);

    /// <summary>
    /// Returns true when the given category should take part in the sync.
    /// </summary>
    /// <param name="categoryId">The provider category ID to test.</param>
    /// <returns>True if the category should be synced.</returns>
    public bool ShouldSync(int categoryId)
    {
        if (_ids.Count == 0)
        {
            return !_emptyMeansNothing;
        }

        return _exclude ? !_ids.Contains(categoryId) : _ids.Contains(categoryId);
    }
}
