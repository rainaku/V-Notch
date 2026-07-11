using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using VNotch.Models;

namespace VNotch.Services;

public static class SettingsMigrator
{

    public const int CurrentVersion = 8;

    private static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> _migrations =
        new Dictionary<int, Func<JsonObject, JsonObject>>
        {

            [0] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.NotchStyle)))
                {
                    root[nameof(NotchSettings.NotchStyle)] = "default";
                }
                if (!root.ContainsKey(nameof(NotchSettings.HoverZoneMargin)))
                {
                    root[nameof(NotchSettings.HoverZoneMargin)] = 60;
                }
                return root;
            },
            [1] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.IsShelfUploadLimitUnlocked)))
                {
                    root[nameof(NotchSettings.IsShelfUploadLimitUnlocked)] = false;
                }
                return root;
            },
            [2] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.MediaBlurBrightnessBoost)))
                {
                    root[nameof(NotchSettings.MediaBlurBrightnessBoost)] = 1.4;
                }
                return root;
            },
            [3] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.DynamicIslandWidth)))
                {
                    int width = 230;
                    if (root.TryGetPropertyValue(nameof(NotchSettings.Width), out var widthNode)
                        && widthNode is JsonValue widthValue
                        && widthValue.TryGetValue(out int parsedWidth))
                    {
                        width = parsedWidth;
                    }

                    root[nameof(NotchSettings.DynamicIslandWidth)] = (int)Math.Round(width * 1.12 / 10.0) * 10;
                }
                return root;
            },
            [4] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableBlurEffects)))
                {
                    root[nameof(NotchSettings.EnableBlurEffects)] = true;
                }
                if (!root.ContainsKey(nameof(NotchSettings.AnimationFps)))
                {
                    root[nameof(NotchSettings.AnimationFps)] = 144;
                }
                return root;
            },
            [5] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.DynamicIslandHeight)))
                {
                    root[nameof(NotchSettings.DynamicIslandHeight)] = 40;
                }
                return root;
            },
            [6] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.ReopenLastViewOnExpand)))
                {
                    root[nameof(NotchSettings.ReopenLastViewOnExpand)] = false;
                }
                return root;
            },
            [7] = root =>
            {
                if (!root.ContainsKey(nameof(NotchSettings.EnableWeather)))
                {
                    root[nameof(NotchSettings.EnableWeather)] = false;
                }
                if (!root.ContainsKey(nameof(NotchSettings.ManualCity)))
                {
                    root[nameof(NotchSettings.ManualCity)] = string.Empty;
                }
                return root;
            },
        };

    public static (NotchSettings settings, bool migrated) Migrate(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new JsonException("Settings JSON is null or empty");

        var node = JsonNode.Parse(rawJson)
                   ?? throw new JsonException("Settings JSON parsed to null root");

        if (node is not JsonObject root)
        {
            throw new JsonException("Settings JSON root must be an object");
        }

        int startVersion = 0;
        if (root.TryGetPropertyValue(nameof(NotchSettings.SettingsVersion), out var versionNode)
            && versionNode is JsonValue vv
            && vv.TryGetValue(out int parsed))
        {
            startVersion = parsed;
        }

        int currentVersion = startVersion;
        bool migrated = false;

        while (currentVersion < CurrentVersion)
        {
            if (_migrations.TryGetValue(currentVersion, out var step))
            {
                root = step(root);
                migrated = true;
            }

            currentVersion++;
        }

        root[nameof(NotchSettings.SettingsVersion)] = CurrentVersion;

        var normalizedJson = root.ToJsonString();
        var settings = JsonSerializer.Deserialize<NotchSettings>(normalizedJson) ?? new NotchSettings();
        settings.SettingsVersion = CurrentVersion;

        if (startVersion != CurrentVersion)
        {
            migrated = true;
        }

        return (settings, migrated);
    }
}
