﻿#nullable enable

using GitCommands;

namespace CommonTestUtils;

public class MemorySettingsCache : SettingsCache
{
    private readonly IDictionary<string, string?> _stringSettings = new Dictionary<string, string?>();

    protected override void LoadImpl()
    {
        // cached in memory, do nothing
    }

    protected override bool NeedRefresh()
    {
        return false;
    }

    protected override void SaveImpl()
    {
        // cached in memory, do nothing
    }

    protected override void SetValueImpl(string key, string? value)
    {
        _stringSettings[key] = value;
    }

    protected override string? GetValueImpl(string key)
    {
        if (_stringSettings.TryGetValue(key, out string? value))
        {
            return value;
        }

        return null;
    }

    protected override void ClearImpl()
    {
        _stringSettings.Clear();
    }
}
