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
