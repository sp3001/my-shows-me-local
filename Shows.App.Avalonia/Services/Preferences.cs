using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shows.App.Avalonia.Services;

namespace Shows.App.Avalonia;

/// <summary>
/// Avalonia stand-in for <c>Microsoft.Maui.Essentials.Preferences</c>. The ViewModels use only
/// <c>Preferences.Default.Get(key, default)</c> / <c>.Set(key, value)</c> for UI choices — the per-tab sort
/// orders (improvements 12 and 26) — not user data (SPEC P4), so they live in a small JSON file rather than
/// the database. Backed by a per-user app-data file so they survive relaunches like MAUI's Preferences.
/// </summary>
/// <remarks>
/// The MAUI call sites pass an <see cref="int"/> default and store an <see cref="int"/>. The shim keeps
/// values as <see cref="JsonElement"/> in memory and coerces on read, so the generic Get&lt;T&gt; shape
/// the ViewModels expect is preserved without a MAUI dependency.
/// </remarks>
public sealed class Preferences
{
    public static Preferences Default { get; } = new Preferences();

    /// <remarks>
    /// A property, not a field. Static initializers run in declaration order, so a field declared below
    /// <see cref="Default"/> is still null when <see cref="Default"/>'s constructor calls <see cref="Load"/>:
    /// <c>File.Exists(null)</c> is false, the store starts empty, and every preference silently reads its
    /// default on every launch while writes still land in the right file. That is how improvement 12's
    /// persisted sort looked like it worked (the file was written) but never did (the read missed it).
    /// </remarks>
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shows.App.Avalonia",
        "preferences.json");

    private readonly Dictionary<string, JsonElement> _values;
    private readonly object _gate = new object();

    private Preferences()
    {
        _values = Load();
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var element))
            {
                return defaultValue;
            }

            try
            {
                return element.Deserialize<T>() ?? defaultValue;
            }
            catch (JsonException ex)
            {
                // A value stored under a different shape than the caller expects falls back to the
                // default. Warning: the preference is a UI choice, so a bad read only resets one setting.
                Log.LogWarning(ex, "Preference {Key} could not be deserialized to {Type}; using default.",
                    key, typeof(T).Name);
                return defaultValue;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    private static Dictionary<string, JsonElement> Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                       ?? new Dictionary<string, JsonElement>();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable preference file is not worth crashing over — fall back to empty.
            // Warning: only the persisted UI choices are lost, the app still runs with defaults.
            Log.LogWarning(e, "Preference file {Path} could not be read; starting with empty preferences.",
                StorePath);
        }

        return new Dictionary<string, JsonElement>();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(_values));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Persisting a UI preference is best-effort; a failure just means it won't survive relaunch.
            // Warning: the in-memory value still applies for this session.
            Log.LogWarning(e, "Preference file {Path} could not be written; the change will not persist.",
                StorePath);
        }
    }

    private static ILogger Log => _log ??= AppLog.For<Preferences>();

    private static ILogger? _log;
}
