// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #79 added a third Live TV mode, ExcludeSelected, where the ticked categories are the ones
// NOT synced. The same checkbox list now means opposite things depending on the mode, which is
// exactly the kind of overloading that produced #78 - so the routes into a wrong selection are
// pinned here.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig, element, withDocument } = require('./helpers/config-harness');

const LIVE_CHECKBOX_SELECTOR = 'input[data-category-type="live"]';
const MODE_SELECTOR = 'input[name="LiveChannelMode"]:checked';

/** A rendered category checkbox, as renderCategoryList would have drawn it. */
function categoryCheckbox(id, checked) {
    return element({
        checked,
        getAttribute: (attr) => (attr === 'data-category-id' ? String(id) : null),
    });
}

/** The document-level selector map for a rendered live category list in the given mode. */
function liveDom(mode, checkboxes) {
    return {
        [MODE_SELECTOR]: mode ? [element({ value: mode })] : [],
        [LIVE_CHECKBOX_SELECTOR]: checkboxes,
        [LIVE_CHECKBOX_SELECTOR + ':checked']: checkboxes.filter((c) => c.checked),
    };
}

test('saving does not wipe the category selection before the list has loaded', async (t) => {
    await t.test('an unrendered list falls back to what was loaded from the config', () => {
        const config = loadConfig();
        config.selectedLiveCategoryIds = [10, 20];
        // No entry for the checkbox selector: the list has not been fetched from the provider yet.
        const restore = withDocument({}, liveDom('ExcludeSelected', []));

        try {
            // Reading the empty DOM would save [], which in this mode means "exclude nothing"
            // and floods the library with every channel the provider has.
            assert.deepStrictEqual(config.getLiveCategoryIdsForSave(), [10, 20]);
        } finally {
            restore();
        }
    });

    await t.test('the fallback is a copy, not the live array', () => {
        const config = loadConfig();
        config.selectedLiveCategoryIds = [10];
        const restore = withDocument({}, liveDom('Custom', []));

        try {
            config.getLiveCategoryIdsForSave().push(999);
            assert.deepStrictEqual(config.selectedLiveCategoryIds, [10]);
        } finally {
            restore();
        }
    });

    await t.test('a rendered list wins over the loaded config, so unticking still saves', () => {
        const config = loadConfig();
        config.selectedLiveCategoryIds = [10, 20];
        const checkboxes = [categoryCheckbox(10, true), categoryCheckbox(20, false)];
        const restore = withDocument({}, liveDom('ExcludeSelected', checkboxes));

        try {
            assert.deepStrictEqual(config.getLiveCategoryIdsForSave(), [10]);
        } finally {
            restore();
        }
    });

    await t.test('a rendered list with nothing ticked genuinely saves an empty selection', () => {
        const config = loadConfig();
        config.selectedLiveCategoryIds = [10];
        const checkboxes = [categoryCheckbox(10, false), categoryCheckbox(20, false)];
        const restore = withDocument({}, liveDom('Custom', checkboxes));

        try {
            // The guard must only cover "not loaded", never "loaded and deliberately cleared".
            assert.deepStrictEqual(config.getLiveCategoryIdsForSave(), []);
        } finally {
            restore();
        }
    });
});

test('the category list is shown in both modes that read it', async (t) => {
    const cases = [
        { mode: 'IncludeAll', expected: 'none' },
        { mode: 'Custom', expected: '' },
        { mode: 'ExcludeSelected', expected: '' },
    ];

    for (const { mode, expected } of cases) {
        await t.test(`${mode} sets the custom section display to "${expected}"`, () => {
            const config = loadConfig();
            const customSection = element({ style: { display: 'unset' } });
            const restore = withDocument(
                { liveCustomSection: customSection },
                liveDom(mode, []));

            try {
                config.updateLiveChannelModeVisibility();
                assert.strictEqual(customSection.style.display, expected);
            } finally {
                restore();
            }
        });
    }

    await t.test('no mode ticked at all falls back to IncludeAll', () => {
        const config = loadConfig();
        const customSection = element({ style: { display: 'unset' } });
        const restore = withDocument({ liveCustomSection: customSection }, liveDom(null, []));

        try {
            config.updateLiveChannelModeVisibility();
            assert.strictEqual(customSection.style.display, 'none');
        } finally {
            restore();
        }
    });
});

test('the counter says excluded or selected to match the mode', async (t) => {
    await t.test('exclude mode counts what will NOT be synced', () => {
        const config = loadConfig();
        const counter = element({ textContent: '' });
        const checkboxes = [categoryCheckbox(10, true), categoryCheckbox(20, false)];
        const restore = withDocument(
            { liveCategoryCounter: counter },
            liveDom('ExcludeSelected', checkboxes));

        try {
            config.updateLiveCategoryCounter();
            assert.strictEqual(counter.textContent, '1 of 2 categories excluded');
        } finally {
            restore();
        }
    });

    await t.test('custom mode still counts what will be synced', () => {
        const config = loadConfig();
        const counter = element({ textContent: '' });
        const checkboxes = [categoryCheckbox(10, true), categoryCheckbox(20, false)];
        const restore = withDocument(
            { liveCategoryCounter: counter },
            liveDom('Custom', checkboxes));

        try {
            config.updateLiveCategoryCounter();
            assert.strictEqual(counter.textContent, '1 of 2 categories selected');
        } finally {
            restore();
        }
    });
});
