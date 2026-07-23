using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace DeckView;

internal static class DeckViewConfig
{
    private const string Path = "user://deckview.cfg";

    private static bool _loaded;
    private static bool _miniDeck = true;
    private static bool _preferFlatMap;
    private static bool _compressMap = true;
    private static bool _dumpMapGraph;

    internal static bool MiniDeck
    {
        get { EnsureLoaded(); return _miniDeck; }
        set
        {
            EnsureLoaded();
            if (_miniDeck == value) return;
            _miniDeck = value;
            Save();
        }
    }

    internal static bool PreferFlatMap
    {
        get { EnsureLoaded(); return _preferFlatMap; }
        set
        {
            EnsureLoaded();
            if (_preferFlatMap == value) return;
            _preferFlatMap = value;
            Save();
        }
    }

    internal static bool CompressMap
    {
        get { EnsureLoaded(); return _compressMap; }
        set
        {
            EnsureLoaded();
            if (_compressMap == value) return;
            _compressMap = value;
            Save();
        }
    }

    // Developer-only opt-in. New and upgraded users default to no graph dumps.
    internal static bool DumpMapGraph
    {
        get { EnsureLoaded(); return _dumpMapGraph; }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var cfg = new ConfigFile();
        Error result = cfg.Load(Path);
        if (result != Error.Ok)
            return; // A missing file is normal on first launch.

        _miniDeck = cfg.GetValue("deck", "mini", true).AsBool();
        _preferFlatMap = cfg.GetValue("map", "flat", false).AsBool();
        _compressMap = cfg.GetValue("map", "compress", true).AsBool();
        _dumpMapGraph = cfg.GetValue("debug", "dump_map_graph", false).AsBool();
    }

    private static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // Preserve manually configured developer/debug keys.
        cfg.SetValue("deck", "mini", _miniDeck);
        cfg.SetValue("map", "flat", _preferFlatMap);
        cfg.SetValue("map", "compress", _compressMap);
        Error result = cfg.Save(Path);
        if (result != Error.Ok)
            Log.Info($"[DeckView] WARNING: could not save preferences ({result})");
    }
}
