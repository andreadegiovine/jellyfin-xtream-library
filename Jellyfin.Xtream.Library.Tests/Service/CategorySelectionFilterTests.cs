// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CategorySelectionFilterTests
{
    // Stand-in for what the provider returns; the filter only ever looks at the IDs.
    private static readonly int[] ProviderCategories = [10, 20, 30, 40];

    private static int[] Apply(int[]? selectedIds, string? mode)
    {
        var set = CategorySelectionFilter.BuildSet(selectedIds);
        bool exclude = CategorySelectionFilter.IsExcludeMode(mode);
        return ProviderCategories
            .Where(id => CategorySelectionFilter.ShouldSync(set, id, exclude))
            .ToArray();
    }

    [Theory]
    [InlineData("Exclude", true)]
    [InlineData("exclude", true)]
    [InlineData("EXCLUDE", true)]
    [InlineData("Include", false)]
    [InlineData("include", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("something else", false)]
    public void IsExcludeMode_OnlyExcludeInverts(string? mode, bool expected)
    {
        CategorySelectionFilter.IsExcludeMode(mode).Should().Be(expected);
    }

    [Fact]
    public void BuildSet_NullOrEmpty_ReturnsEmpty()
    {
        CategorySelectionFilter.BuildSet(null).Should().BeEmpty();
        CategorySelectionFilter.BuildSet([]).Should().BeEmpty();
    }

    [Fact]
    public void BuildSet_Duplicates_AreCollapsed()
    {
        CategorySelectionFilter.BuildSet([7, 7, 9]).Should().BeEquivalentTo([7, 9]);
    }

    // The four rows of the behaviour matrix agreed on GitHub #76.

    [Fact]
    public void IncludeMode_NothingSelected_SyncsEverything()
    {
        Apply([], "Include").Should().Equal(ProviderCategories);
    }

    [Fact]
    public void IncludeMode_CategoriesSelected_SyncsOnlyThose()
    {
        Apply([20, 40], "Include").Should().Equal(20, 40);
    }

    [Fact]
    public void ExcludeMode_NothingSelected_SyncsEverything()
    {
        // Excluding nothing excludes nothing. This is the row that was written down backwards
        // in the first #76 reply and corrected afterwards.
        Apply([], "Exclude").Should().Equal(ProviderCategories);
    }

    [Fact]
    public void ExcludeMode_CategoriesSelected_SyncsEverythingElse()
    {
        Apply([20, 40], "Exclude").Should().Equal(10, 30);
    }

    // The point of the feature: a category the provider adds after the user configured their
    // exclusions is synced automatically, without the user touching the settings again.
    [Fact]
    public void ExcludeMode_CategoryAddedByProviderLater_IsSyncedAutomatically()
    {
        var set = CategorySelectionFilter.BuildSet([20, 40]);
        bool exclude = CategorySelectionFilter.IsExcludeMode("Exclude");

        CategorySelectionFilter.ShouldSync(set, 99, exclude).Should().BeTrue();
    }

    // Same scenario under Include, which is the behaviour Exclude mode exists to avoid.
    [Fact]
    public void IncludeMode_CategoryAddedByProviderLater_IsNotSynced()
    {
        var set = CategorySelectionFilter.BuildSet([20, 40]);
        bool exclude = CategorySelectionFilter.IsExcludeMode("Include");

        CategorySelectionFilter.ShouldSync(set, 99, exclude).Should().BeFalse();
    }

    [Fact]
    public void ExcludeMode_AllCategoriesSelected_SyncsNothing()
    {
        Apply(ProviderCategories, "Exclude").Should().BeEmpty();
    }

    // A config written before the mode setting existed has no mode at all. It has to keep
    // behaving exactly as it did, which is Include.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingMode_BehavesAsInclude(string? mode)
    {
        Apply([20, 40], mode).Should().Equal(20, 40);
    }

    [Fact]
    public void ShouldSync_SelectionReferencesUnknownCategory_DoesNotAffectOthers()
    {
        // 999 is not on the provider. Include mode narrows to the one ID that does exist.
        Apply([20, 999], "Include").Should().Equal(20);

        // Exclude mode drops the one that exists and keeps the rest.
        Apply([20, 999], "Exclude").Should().Equal(10, 30, 40);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new int[0], false)]
    [InlineData(new[] { 1 }, true)]
    [InlineData(new[] { 1, 2 }, true)]
    public void NarrowsSelection_OnlyTrueWhenSomethingIsConfigured(int[]? selectedIds, bool expected)
    {
        CategorySelectionFilter.NarrowsSelection(selectedIds).Should().Be(expected);
    }

    // --- CategorySelection: the same primitives, plus what an empty selection means (GitHub #78).

    private static int[] Apply(CategorySelection selection)
        => ProviderCategories.Where(selection.ShouldSync).ToArray();

    [Fact]
    public void FromFolderMappings_Empty_SyncsNothing()
    {
        var selection = CategorySelection.FromFolderMappings([]);

        selection.SyncsNothing.Should().BeTrue();
        selection.NarrowsSelection.Should().BeFalse("there is nothing to narrow down to");
        Apply(selection).Should().BeEmpty();
    }

    [Fact]
    public void FromFolderMappings_WithIds_SyncsOnlyThose()
    {
        var selection = CategorySelection.FromFolderMappings([20, 40]);

        selection.SyncsNothing.Should().BeFalse();
        selection.NarrowsSelection.Should().BeTrue();
        Apply(selection).Should().Equal(20, 40);
    }

    [Theory]
    [InlineData("Include")]
    [InlineData("Exclude")]
    [InlineData(null)]
    public void FromCategoryList_Empty_SyncsEverything(string? mode)
    {
        var selection = CategorySelection.FromCategoryList([], mode);

        selection.SyncsNothing.Should().BeFalse("an empty flat selection has always meant sync everything");
        Apply(selection).Should().Equal(ProviderCategories);
    }

    [Fact]
    public void FromCategoryList_ExcludeWithIds_StillInverts()
    {
        Apply(CategorySelection.FromCategoryList([20, 40], "Exclude")).Should().Equal(10, 30);
    }

    [Fact]
    public void ResolveCategorySelection_SingleFolderMode_UsesTheCategoryList()
    {
        var selection = StrmSyncService.ResolveCategorySelection(
            "Single", "Kids=20", [40], "Include");

        Apply(selection).Should().Equal(new[] { 40 }, "Single folder mode ignores the mappings entirely");
    }

    [Fact]
    public void ResolveCategorySelection_MultipleFolderMode_UsesTheFolderMappings()
    {
        var selection = StrmSyncService.ResolveCategorySelection(
            "Multiple", "Kids=20", [40], "Include");

        Apply(selection).Should().Equal(new[] { 20 }, "the mappings win over a stale selection array");
    }

    [Fact]
    public void ResolveCategorySelection_MultipleFolderMode_EmptyMappings_SyncsNothing()
    {
        var selection = StrmSyncService.ResolveCategorySelection(
            "Multiple", string.Empty, [], "Include");

        selection.SyncsNothing.Should().BeTrue();
        Apply(selection).Should().BeEmpty();
    }

    [Fact]
    public void ResolveCategorySelection_MultipleFolderMode_IgnoresExcludeMode()
    {
        // Only reachable from a hand-edited config; the mapping is an inclusion list either way.
        var selection = StrmSyncService.ResolveCategorySelection(
            "Multiple", "Kids=20", [20], "Exclude");

        Apply(selection).Should().Equal(20);
    }

    [Theory]
    [InlineData("Multiple")]
    [InlineData("multiple")]
    [InlineData("MULTIPLE")]
    public void ResolveCategorySelection_MultipleFolderMode_IsCaseInsensitiveOnTheModeString(string mode)
    {
        Apply(StrmSyncService.ResolveCategorySelection(mode, "Kids=20", [40], "Include"))
            .Should().Equal(20);
    }
}
