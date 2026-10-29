/*
 * Barfender
 * Copyright (C) 2026 JustABrit1512
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProtoDivision.Assemblies.UI;
using ProtoDivision.Configuration.Core;
using ProtoDivision.Game;
using ProtoDivision.Gameplay;
using UnityEngine;

public enum AutoplayMode { Off, Sequential, Shuffle }
public enum AutoplayScope { All, CustomOnly, GameOnly }

[BepInPlugin("com.you.customjukebox", "CustomJukebox", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> ArtistFirst, UseOperations, OverrideCheckRules, FallbackToDirect, DumpNetworkTypes;
    internal static ConfigEntry<AutoplayMode> ApMode;
    internal static ConfigEntry<AutoplayScope> ApScope;
    internal static ConfigEntry<float> ApEarlySeconds;
    internal static ConfigEntry<bool> ApDebug;
	internal static ConfigEntry<int> LayoutColumns;
    internal static readonly List<Track> Tracks = new List<Track>();
    internal static readonly List<JukeboxDatabase> Dbs = new List<JukeboxDatabase>();
    internal static JukeboxDatabase LiveDb; // the instance the game itself is using for jukebox actions
    internal static bool IsAuthority;       // true once this machine has run a jukebox operation itself (solo or host)
    internal static object LastJukeboxId;   // boxed ItemInstanceId of the jukebox the screen last used
    internal static int OriginalCount = -1; // playlists in the unmodified game
    internal static int TotalCount = -1;    // playlists after we add ours

    const int MaxTotalPlaylists = 31;       // the unlock mask is a 32-bit int; stay clear of the sign bit
    static readonly HashSet<string> SupportedExtensions = new HashSet<string> { ".wav", ".mp3", ".ogg" };
    static bool _applied;
    static Thread _decodeThread;

    public override void Load()
    {
        L = Log;
        ArtistFirst = Config.Bind("Files", "ArtistFirst", false,
            "Read file names as 'Artist - Title' instead of 'Title - Artist'.");
        UseOperations = Config.Bind("Coop", "UseOperations", true,
            "Send custom selections through the game's own operation system (needed for co-op). False = local-only select.");
        OverrideCheckRules = Config.Bind("Coop", "OverrideCheckRules", true,
            "Let the game accept custom slots (it normally treats them as locked).");
        FallbackToDirect = Config.Bind("Coop", "FallbackToDirect", true,
            "If submitting the operation throws an error, fall back to a local-only select.");
        DumpNetworkTypes = Config.Bind("Coop", "DumpNetworkTypes", false,
            "Write BepInEx/coop_types.txt, a list of the game's networking/operation classes (for debugging).");

        ApMode = Config.Bind("Autoplay", "Mode", AutoplayMode.Shuffle,
            "Off = the game's normal behaviour (a track repeats forever). Sequential = play the next track when one ends. " +
            "Shuffle = play a random track next, without repeating until every track has played.");
        ApScope = Config.Bind("Autoplay", "Scope", AutoplayScope.All,
            "Which tracks autoplay may pick: All = every unlocked track, CustomOnly = only your own tracks, GameOnly = only the game's tracks.");
        ApEarlySeconds = Config.Bind("Autoplay", "AdvanceEarlySeconds", 1.0f,
            "How many seconds before a track ends the next one is requested (covers the delay in co-op).");
        ApDebug = Config.Bind("Autoplay", "Debug", false,
            "Log what autoplay sees every 20 seconds (track position, length, who is in charge).");
		LayoutColumns = Config.Bind("Layout", "Columns", 0,
            "Columns in the jukebox card grid. 0 = automatic (recommended, picks the layout that keeps the cards biggest). " +
            "Or force a number from 3 to 8.");

        var dir = Path.Combine(Paths.PluginPath, "CustomJukebox");
        Directory.CreateDirectory(dir);

        var files = Directory.GetFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var f in files) Tracks.Add(Track.FromFileName(f, ArtistFirst.Value));
        L.LogInfo($"Found {Tracks.Count} audio file(s) in {dir}");

        // Decoding is plain managed code, so it can run in the background while the game loads
        _decodeThread = new Thread(DecodeAll) { IsBackground = true, Name = "CustomJukebox decode" };
        _decodeThread.Start();

        var harmony = new Harmony("com.you.customjukebox");
        TryPatch(harmony, typeof(ConfigPatch), "GameConfiguration.InitializeConfigurations");
        TryPatch(harmony, typeof(ViewPatch), "JukeboxView.Start");
        TryPatch(harmony, typeof(ScreenPatch), "JukeboxScreenView.InitializeContent");
        TryPatch(harmony, typeof(AfterLoadPatch), "JukeboxDatabase.OnAfterLoad");
        TryPatch(harmony, typeof(LiveDbPatch), "JukeboxDatabase.SetPlayingPlaylist");
        TryPatch(harmony, typeof(ClickPatch), "card click lambda");
        TryPatch(harmony, typeof(ExecPatch), "SelectJukeboxPlaylistOperation.Execute");
        TryPatch(harmony, typeof(LockVisualFix), "lock visual fix");
        TryPatch(harmony, typeof(LayoutFit), "layout fit");
        TryPatch(harmony, typeof(AutoplayUpdatePatch), "JukeboxView.JukeboxUpdate (autoplay)");
        TryPatch(harmony, typeof(AutoplayObservePatch), "JukeboxView.ObservePlaybackState (autoplay)");

        try { Coop.InstallCheckRules(harmony); }
        catch (Exception e) { L.LogError("InstallCheckRules failed: " + e); }
        try { Coop.InstallRequestLoggers(harmony); }
        catch (Exception e) { L.LogError("InstallRequestLoggers failed: " + e); }
    }

    static void TryPatch(Harmony h, Type patch, string label)
    {
        try { h.PatchAll(patch); L.LogInfo("Patched " + label); }
        catch (Exception e) { L.LogError($"Patch {label} failed: {e}"); }
    }

    static void DecodeAll()
    {
        foreach (var t in Tracks)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AudioDecoder.Decode(t);
                L.LogInfo($"Decoded '{t.FileName}': {t.Pcm.Length / t.Channels / (float)t.SampleRate:F1}s, {t.SampleRate}Hz, {t.Channels}ch ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                t.Pcm = null;
                L.LogError($"Could not decode '{t.FileName}': {e.Message}");
            }
        }
        if (DumpNetworkTypes.Value) Coop.DumpTypes();
    }

    internal static bool IsCustomIndex(int i) => OriginalCount >= 0 && i >= OriginalCount && i < TotalCount;

    // Remember every database instance we see and make sure our custom slots are unlocked on it
    internal static void Remember(JukeboxDatabase db, string where)
    {
        if (db == null) return;
        try
        {
            if (!Dbs.Any(d => d.Pointer == db.Pointer))
            {
                Dbs.Add(db);
                L.LogInfo($"JukeboxDatabase 0x{db.Pointer.ToInt64():X} seen in {where} (mask {db.UnlockedPlaylistsMask})");
            }
            UnlockCustom(db);
        }
        catch (Exception e) { L.LogError("Remember: " + e.Message); }
    }

    // Sets the unlock bit(s) for our custom slots on an in-memory database
    internal static void UnlockCustom(JukeboxDatabase db)
    {
        if (db == null || OriginalCount < 0 || TotalCount <= OriginalCount) return;
        try
        {
            int before = db.UnlockedPlaylistsMask;
            int after = before;
            for (int i = OriginalCount; i < TotalCount; i++) after |= (1 << i);
            if (after != before)
            {
                db.UnlockedPlaylistsMask = after;
                L.LogInfo($"unlock mask on 0x{db.Pointer.ToInt64():X}: {before} -> {after}");
            }
        }
        catch (Exception e) { L.LogError("UnlockCustom: " + e.Message); }
    }

    // Builds the AudioClip a chunk at a time so no big temporary float array is needed
    static AudioClip MakeClip(Track t)
    {
        int ch = t.Channels;
        int frames = t.Pcm.Length / ch;
        var clip = AudioClip.Create(t.Title ?? t.BaseName, frames, ch, t.SampleRate, false);

        const int ChunkFrames = 48000;
        var full = new float[ChunkFrames * ch];
        for (int pos = 0; pos < frames; pos += ChunkFrames)
        {
            int n = Math.Min(ChunkFrames, frames - pos);
            var arr = n == ChunkFrames ? full : new float[n * ch];
            int src = pos * ch;
            for (int i = 0; i < n * ch; i++) arr[i] = t.Pcm[src + i] / 32768f;
            clip.SetData(arr, pos);
        }
        clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
        t.Pcm = null; // free the managed copy
        return clip;
    }

    // Idempotent. Called from several hooks, all on the game's main thread.
    internal static void Apply(GameConfiguration gc, string where)
    {
        if (_applied) return;
        try
        {
            if (gc == null)
            {
                if (!GameConfiguration.IsInstanceAvailable) return;
                gc = GameConfiguration.Instance;
            }

            var jc = gc.JukeboxConfiguration;
            var list = jc?.Playlists;
            if (list == null) return; // not ready yet, a later hook will retry

            OriginalCount = list.Length;

            if (_decodeThread != null && _decodeThread.IsAlive)
            {
                L.LogInfo("Waiting for audio decoding to finish...");
                _decodeThread.Join(60000);
            }

            // Work out where each track goes
            var usable = Tracks.Where(t => t.Pcm != null).ToList();
            var replacements = usable.Where(t => t.Requested.HasValue && t.Requested.Value < OriginalCount).ToList();
            var additions = usable.Where(t => !replacements.Contains(t))
                .OrderBy(t => t.Requested ?? int.MaxValue)
                .ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int room = MaxTotalPlaylists - OriginalCount;
            if (additions.Count > room)
            {
                L.LogWarning($"Only {room} extra tracks fit; ignoring {additions.Count - room}");
                additions = additions.Take(room).ToList();
            }
            for (int i = 0; i < additions.Count; i++) additions[i].Slot = OriginalCount + i;
            foreach (var r in replacements) r.Slot = r.Requested.Value;

            int newLen = OriginalCount + additions.Count;
            L.LogInfo($"Apply (from {where}): {OriginalCount} playlists in game config, {replacements.Count} replaced, {additions.Count} added -> {newLen}");

            if (newLen > OriginalCount)
            {
                var bigger = new Il2CppReferenceArray<PlaylistEntry>(newLen);
                for (int i = 0; i < OriginalCount; i++) bigger[i] = list[i];
                for (int i = OriginalCount; i < newLen; i++)
                {
                    var e = new PlaylistEntry();
                    e.DisplayName = "Custom " + i;
                    e.Artist = "";
                    e.CardColor = new Color(0.95f, 0.45f, 0.2f, 1f);
                    e.Loop = true;
                    bigger[i] = e;
                }
                jc.Playlists = bigger;
                list = bigger;
            }
            TotalCount = newLen;

            foreach (var db in Dbs.ToList()) UnlockCustom(db);

            uint hash = 2166136261u; // FNV-1a over slot|title|artist|file size, so players can compare their setups
            foreach (var t in replacements.Concat(additions).OrderBy(x => x.Slot))
            {
                var entry = list[t.Slot];
                entry.Music = MakeClip(t);
                bool isNew = t.Slot >= OriginalCount;

                if (isNew)
                {
                    entry.DisplayName = t.Title ?? t.BaseName;
                    entry.Artist = t.Artist ?? "";
                }
                else if (!t.KeepOriginalText)
                {
                    if (t.Title != null) entry.DisplayName = t.Title;
                    if (t.Artist != null) entry.Artist = t.Artist;
                }
                L.LogInfo($"{(isNew ? "Added" : "Replaced")} playlist {t.Slot}: '{entry.DisplayName}'");

                Coop.Log($"slot {t.Slot} = '{entry.DisplayName}' by '{entry.Artist}' [{t.FileName}, {t.FileSize} bytes]");
                string line = $"{t.Slot}|{entry.DisplayName}|{entry.Artist}|{t.FileSize}";
                foreach (char c in line) { hash ^= c; hash *= 16777619u; }
            }
            Coop.Log($"playlist fingerprint {hash:X8} ({replacements.Count + additions.Count} custom tracks) - this must be identical on every player");
            _applied = true;
        }
        catch (Exception e)
        {
            L.LogError(e.ToString());
            _applied = true; // don't retry in a loop
        }
    }
}

[HarmonyPatch(typeof(GameConfiguration), "InitializeConfigurations")]
static class ConfigPatch
{
    static void Postfix(GameConfiguration __instance) { Plugin.Apply(__instance, "GameConfiguration.InitializeConfigurations"); }
}

[HarmonyPatch(typeof(JukeboxView), "Start")]
static class ViewPatch
{
    static void Prefix() { Plugin.Apply(null, "JukeboxView.Start"); }
}

[HarmonyPatch(typeof(JukeboxScreenView), "InitializeContent")]
static class ScreenPatch
{
    static void Prefix() { Plugin.Apply(null, "JukeboxScreenView.InitializeContent"); }
}

[HarmonyPatch(typeof(JukeboxDatabase), "OnAfterLoad")]
static class AfterLoadPatch
{
    static void Postfix(JukeboxDatabase __instance) { Plugin.Remember(__instance, "OnAfterLoad"); }
}

// SetPlayingPlaylist only runs on the database the game is actually using
[HarmonyPatch(typeof(JukeboxDatabase), "SetPlayingPlaylist")]
static class LiveDbPatch
{
    static void Prefix(JukeboxDatabase __instance, ProtoDivision.Gameplay.DataTypes.ItemInstanceId jukeboxId, int playlistIndex)
    {
        Plugin.LiveDb = __instance;
        Coop.Log($"SetPlayingPlaylist(jukebox {jukeboxId}, playlist {playlistIndex}) on 0x{__instance.Pointer.ToInt64():X} ({Coop.ClickAgo()})");
        Plugin.Remember(__instance, "SetPlayingPlaylist");
    }
}

// The card click handler (a compiler-generated lambda); OnCardClicked itself is inlined into it.
[HarmonyPatch]
static class ClickPatch
{
    static MethodBase TargetMethod()
    {
        var nested = typeof(JukeboxScreenView).GetNestedType("__c__DisplayClass85_0",
            BindingFlags.Public | BindingFlags.NonPublic);
        var m = nested?.GetMethod("_PopulateGrid_b__0",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return m ?? AccessTools.Method("ProtoDivision.Assemblies.UI.JukeboxScreenView+__c__DisplayClass85_0:_PopulateGrid_b__0");
    }

    static bool Prefix(object __instance)
    {
        int idx = -1;
        JukeboxScreenView screen = null;
        try
        {
            var t = __instance.GetType();
            idx = Convert.ToInt32(t.GetProperty("capturedIndex").GetValue(__instance));
            screen = t.GetProperty("__4__this").GetValue(__instance) as JukeboxScreenView;
        }
        catch (Exception e) { Plugin.L.LogError("ClickPatch reflection: " + e.Message); }

        if (screen != null && Plugin.IsCustomIndex(idx))
        {
            Coop.LastLocalClick = Time.realtimeSinceStartup;
            Coop.Log($"local click on custom card {idx}");
            try
            {
                var args = GetActive(screen);
                if (args == null) return false;

                bool handled = Plugin.UseOperations.Value && Coop.TrySubmit(screen, idx, args);
                if (!handled && (!Plugin.UseOperations.Value || Plugin.FallbackToDirect.Value))
                    DirectSelect(args[0], idx);
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Custom select failed: " + e);
            }
            return false; // skip the original handler
        }
        return true;
    }

    // Asks the screen which jukebox/character is active: returns { jukeboxId, characterId }
    static object[] GetActive(JukeboxScreenView screen)
    {
        var m = typeof(JukeboxScreenView).GetMethod("TryGetActiveJukeboxId");
        if (m == null) { Plugin.L.LogWarning("TryGetActiveJukeboxId not found"); return null; }

        var ps = m.GetParameters();
        var args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            args[i] = Activator.CreateInstance(ps[i].ParameterType.GetElementType());

        bool ok = (bool)m.Invoke(screen, args);
        if (ok) Plugin.LastJukeboxId = args[0];
        return ok ? args : null;
    }

    // Local-only selection: tells the jukebox database to play the slot, without involving any other player
    internal static void DirectSelect(object jukeboxId, int idx)
    {
        var setM = typeof(JukeboxDatabase).GetMethod("SetPlayingPlaylist");
        var targets = Plugin.LiveDb != null
            ? new List<JukeboxDatabase> { Plugin.LiveDb }
            : Plugin.Dbs.ToList();
        Coop.Log($"LOCAL-ONLY select of slot {idx} on {targets.Count} database(s)");

        foreach (var db in targets)
        {
            try { setM.Invoke(db, new object[] { jukeboxId, idx }); }
            catch (Exception e) { Plugin.L.LogError("SetPlayingPlaylist threw: " + (e.InnerException ?? e)); }
        }
    }
}

[HarmonyPatch(typeof(ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation), "Execute")]
static class ExecPatch
{
    static void Prefix(ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation __instance)
    {
        try
        {
            // Only the machine that runs the game's world (solo, or the co-op host) ever executes operations locally
            Plugin.IsAuthority = true;

            int idx = __instance.PlaylistIndex;
            bool custom = Plugin.IsCustomIndex(idx);

            // A normal selection that got as far as Execute passed its rules check: remember what "passed" looks like
            if (!custom && Coop.LastNormalResult != null && Coop.ObservedOk == null)
            {
                Coop.ObservedOk = Coop.LastNormalResult;
                Coop.Log("learned the 'rules passed' value from a normal selection: " + Coop.Describe(Coop.ObservedOk));
            }

            Coop.Log($"Execute(playlist {idx}, custom={custom}, jukebox {__instance.ItemId}, operator {__instance.OperatorCharacterId}) - {Coop.ClickAgo()}");
        }
        catch (Exception e) { Plugin.L.LogError("ExecPatch: " + e.Message); }
    }
}

// Removes the game's "locked" look from our custom cards and restores their title/artist after every refresh
[HarmonyPatch(typeof(JukeboxScreenView), "ApplyLockedVisuals")]
static class LockVisualFix
{
    static string _lockedClass;
    static int _errors;

    static void Postfix(JukeboxScreenView __instance)
    {
        if (Plugin.TotalCount <= Plugin.OriginalCount) return;
        try
        {
            if (_lockedClass == null)
                _lockedClass = typeof(JukeboxScreenView).GetProperty("LockedCardClass")?.GetValue(null) as string ?? "";

            var cards = __instance._Cards;
            if (cards == null) return;
            var playlists = GameConfiguration.Instance.JukeboxConfiguration.Playlists;

            for (int i = Plugin.OriginalCount; i < Plugin.TotalCount && i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null) continue;

                foreach (var el in new[] { c.Root, c.BandTop, c.BandBottom, c.Rule })
                {
                    if (el == null) continue;
                    if (_lockedClass.Length > 0) el.RemoveFromClassList(_lockedClass);
                    el.SetEnabled(true);
                }
                if (c.Root != null) c.Root.pickingMode = UnityEngine.UIElements.PickingMode.Position;

                var entry = playlists[i];
                if (c.Title != null) c.Title.text = entry.DisplayName;
                if (c.Artist != null) c.Artist.text = entry.Artist ?? "";
            }
        }
        catch (Exception e)
        {
            if (_errors++ < 4) Plugin.L.LogError("LockVisualFix: " + e.Message);
        }
    }
}

// Re-arranges the card grid into more columns and shrinks it so every row fits inside its frame
[HarmonyPatch(typeof(JukeboxScreenView), "Update")]
static class LayoutFit
{
    internal static int AppliedCols;   // columns we arranged the grid into (0 = left as the game made it)
    static IntPtr _gridPtr;
    static int _frames, _tick, _afterFrames, _cols;
    static bool _fitted, _reported;
    static float _cardPercent;

    static void Postfix(JukeboxScreenView __instance)
    {
        if (Plugin.TotalCount <= Plugin.OriginalCount) return;
        try
        {
            var grid = __instance._PlaylistGrid;
            if (grid == null || grid.childCount < Plugin.TotalCount) return;

            if (grid.Pointer != _gridPtr)
            {
                _gridPtr = grid.Pointer;
                _frames = 0; _tick = 0; _afterFrames = 0;
                _fitted = false; _reported = false;
                AppliedCols = 0;
            }

            if (_fitted)
            {
                // The game may rebuild its cards; keep our column width on them
                if (_cardPercent > 0f && ++_tick % 30 == 0) ApplyCardWidth(grid, _cardPercent);

                // Keep the A1/B2-style code badges in step with the new column count
                BadgeFix.Apply(grid, AppliedCols);

                if (!_reported && ++_afterFrames >= 60)
                {
                    _reported = true;
                    int last = grid.childCount - 1;
                    Plugin.L.LogInfo($"layout check: grid={grid.worldBound} card0={grid[0].worldBound} "
                        + $"card{_cols - 1}={grid[Math.Min(_cols - 1, last)].worldBound} "
                        + $"card{_cols}={grid[Math.Min(_cols, last)].worldBound} last={grid[last].worldBound}");
                }
                return;
            }

            _frames++;
            if (_frames < 10) return; // a few frames for the layout to settle

            int cols0 = Math.Max(1, JukeboxScreenView.CardColumns);   // the game's own column count
            int n = grid.childCount;
            if (n <= cols0) { _fitted = true; return; }

            float rowStride = Math.Abs(grid[0].worldBound.y - grid[cols0].worldBound.y);
            float gridH = grid.worldBound.height;
            float gridW = grid.resolvedStyle.width;
            if (rowStride < 0.0001f || gridH < 0.0001f || float.IsNaN(gridW) || gridW < 1f) return; // layout not ready yet

            // Pick the number of columns that lets the cards stay biggest.
            int cols = cols0;
            float f = Math.Min(1f, gridH / (((n + cols0 - 1) / cols0) * rowStride));

            int forced = Plugin.LayoutColumns.Value;
            if (forced >= cols0 && forced <= 8)
            {
                cols = forced;
                int r = (n + cols - 1) / cols;
                f = Math.Min(1f, Math.Min((float)cols0 / cols, gridH / (r * rowStride)));
            }
            else
            {
                for (int c = cols0 + 1; c <= 8; c++)
                {
                    int r = (n + c - 1) / c;
                    float fc = Math.Min(1f, Math.Min((float)cols0 / c, gridH / (r * rowStride)));
                    if (fc > f + 0.005f) { f = fc; cols = c; }   // ties keep the lower column count
                }
            }

            if (f >= 1f && cols == cols0) { _fitted = true; return; } // everything already fits
            f *= 0.98f; // a little breathing room

            _cols = cols;
            AppliedCols = cols;
            _cardPercent = 100f / cols - 0.02f; // a hair under, so rounding can't push the last card onto a new row
            ApplyCardWidth(grid, _cardPercent);

            grid.style.transformOrigin = new UnityEngine.UIElements.StyleTransformOrigin(
                new UnityEngine.UIElements.TransformOrigin(
                    UnityEngine.UIElements.Length.Percent(0f), UnityEngine.UIElements.Length.Percent(0f), 0f));
            grid.style.scale = new UnityEngine.UIElements.StyleScale(
                new UnityEngine.UIElements.Scale(new Vector3(f, f, 1f)));
            grid.style.width = new UnityEngine.UIElements.StyleLength(gridW / f);

            int rows = (n + cols - 1) / cols;
            Plugin.L.LogInfo($"layout fit: {n} cards -> {cols} columns x {rows} rows (the game's own: {cols0}), scale={f:F3}");
            _fitted = true;
            _tick = 0; _afterFrames = 0;
        }
        catch (Exception ex)
        {
            Plugin.L.LogError("layout fit failed: " + ex);
            _fitted = true; _reported = true; // don't spam
        }
    }

    // Sets how wide each card is, as a share of the grid's width
    static void ApplyCardWidth(UnityEngine.UIElements.VisualElement grid, float percent)
    {
        var w = new UnityEngine.UIElements.StyleLength(UnityEngine.UIElements.Length.Percent(percent));
        for (int i = 0; i < grid.childCount; i++)
        {
            var card = grid[i];
            card.style.width = w;
            card.style.flexBasis = w; // in case the game sizes its cards with flex-basis instead of width
        }
    }
}

// Rewrites the A1/B2-style code badge on each card so it matches the rearranged grid
static class BadgeFix
{
    static readonly Regex CodeRx = new Regex(@"^[A-Z][0-9]{1,2}$");
    static readonly Dictionary<int, UnityEngine.UIElements.Label> Cache = new Dictionary<int, UnityEngine.UIElements.Label>();
    static IntPtr _gridPtr;
    static int _tick, _fixes, _errors;
    static bool _dumped, _summarised;

    public static void Apply(UnityEngine.UIElements.VisualElement grid, int cols)
    {
        if (cols <= 0 || grid == null) return;
        try
        {
            if (grid.Pointer != _gridPtr)
            {
                _gridPtr = grid.Pointer;
                Cache.Clear();
                _tick = 0; _fixes = 0; _dumped = false; _summarised = false;
            }
            if (++_tick % 3 != 0) return; // every few frames is plenty

            int n = Math.Min(grid.childCount, Plugin.TotalCount);
            int found = 0;
            for (int i = 0; i < n; i++)
            {
                UnityEngine.UIElements.Label lbl;
                if (!Cache.TryGetValue(i, out lbl) || lbl == null)
                {
                    lbl = FindCodeLabel(grid[i], 0);
                    if (lbl == null) continue; // for example a locked card
                    Cache[i] = lbl;
                }
                found++;

                string want = ((char)('A' + i / cols)).ToString() + (i % cols + 1);
                if (lbl.text != want) { lbl.text = want; _fixes++; }
            }

            if (!_dumped && _tick >= 30)
            {
                _dumped = true;
                Plugin.L.LogInfo($"[badge] code labels found on {found} of {n} cards; columns={cols}");
                DumpCard(grid, 0);
                DumpCard(grid, 7);
                DumpCard(grid, 9);
            }
            if (!_summarised && _tick >= 300)
            {
                _summarised = true;
                Plugin.L.LogInfo($"[badge] corrections written so far: {_fixes} (a number that keeps climbing means the game keeps changing the badges back)");
            }
        }
        catch (Exception e)
        {
            if (_errors++ < 3) Plugin.L.LogError("[badge] " + e.Message);
        }
    }

    // The badge is a label whose text looks like a slot code (a letter and a number)
    static UnityEngine.UIElements.Label FindCodeLabel(UnityEngine.UIElements.VisualElement ve, int depth)
    {
        var lbl = ve.TryCast<UnityEngine.UIElements.Label>();
        if (lbl != null && lbl.text != null && CodeRx.IsMatch(lbl.text)) return lbl;
        if (depth > 6) return null;
        for (int c = 0; c < ve.childCount; c++)
        {
            var r = FindCodeLabel(ve[c], depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    static void DumpCard(UnityEngine.UIElements.VisualElement grid, int idx)
    {
        if (idx >= grid.childCount) return;
        var sb = new StringBuilder($"[badge] card {idx} element tree:");
        Dump(grid[idx], 0, sb);
        Plugin.L.LogInfo(sb.ToString());
    }

    static void Dump(UnityEngine.UIElements.VisualElement ve, int depth, StringBuilder sb)
    {
        if (depth > 5) return;
        string text = "";
        var lbl = ve.TryCast<UnityEngine.UIElements.Label>();
        if (lbl != null) text = " text='" + lbl.text + "'";
        var wb = ve.worldBound;
        sb.Append("\n   ").Append(new string(' ', depth * 2))
          .Append(ve.GetIl2CppType().Name).Append(" '").Append(ve.name).Append("' ")
          .Append($"{wb.width:F2}x{wb.height:F2} children={ve.childCount}").Append(text);
        for (int c = 0; c < ve.childCount; c++) Dump(ve[c], depth + 1, sb);
    }
}

// ---------------------------------------------------------------------------------------------
// Autoplay: when a track is nearly over, play the next one (sequential or shuffled)
// ---------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(JukeboxView), "JukeboxUpdate")]
static class AutoplayUpdatePatch
{
    static void Postfix(JukeboxView __instance) { Autoplay.Tick(__instance); }
}

[HarmonyPatch(typeof(JukeboxView), "ObservePlaybackState")]
static class AutoplayObservePatch
{
    static void Postfix(JukeboxView __instance) { Autoplay.Tick(__instance); }
}

static class Autoplay
{
    static int _lastFrame = -1;
    static bool _announced;
    static int _trackIdx = int.MinValue, _trackSeq = int.MinValue;
    static bool _fired;
    static float _lastAdvanceAt = -100f;
    static float _nextDebugAt;
    static int _mismatches;
    static int _errors;
    static object _jukeboxId;
    static readonly System.Random Rng = new System.Random();
    static readonly List<int> Bag = new List<int>();

    public static void Tick(JukeboxView v)
    {
        try
        {
            if (Plugin.ApMode.Value == AutoplayMode.Off) return;
            if (Time.frameCount == _lastFrame) return; // both hooks may fire in the same frame
            _lastFrame = Time.frameCount;

            if (!_announced)
            {
                _announced = true;
                Plugin.L.LogInfo($"[autoplay] hooked into the jukebox view (mode {Plugin.ApMode.Value}, scope {Plugin.ApScope.Value})");
            }
            if (Plugin.TotalCount <= 0) return; // playlists are not set up yet

            int idx = v.LastAppliedPlaylistIndex;
            bool paused = v.LastAppliedPaused;
            int seq = v.LastAppliedSelectionSequence;

            // A different track (or a re-selection) starts a new round
            if (idx != _trackIdx || seq != _trackSeq)
            {
                _trackIdx = idx; _trackSeq = seq; _fired = false;
            }

            var src = v.MusicAudioSource;
            var clip = src != null ? src.clip : null;

            if (Plugin.ApDebug.Value && Time.realtimeSinceStartup >= _nextDebugAt)
            {
                _nextDebugAt = Time.realtimeSinceStartup + 20f;
                Plugin.L.LogInfo($"[autoplay] slot {idx} seq {seq} paused={paused} authority={Plugin.IsAuthority} "
                    + (clip != null ? $"clip '{clip.name}' {src.time:F1}/{clip.length:F1}s loop={src.loop} playing={src.isPlaying}" : "no clip"));
            }

            if (_fired || !Plugin.IsAuthority) return;
            if (idx < 0 || paused || clip == null || !src.isPlaying) return;

            float early = Mathf.Max(0.2f, Plugin.ApEarlySeconds.Value);
            float length = clip.length;
            if (length < early + 5f) return;               // too short to bother
            float remaining = length - src.time;
            if (remaining > early) return;                 // not near the end yet
            if (Time.realtimeSinceStartup - _lastAdvanceAt < 8f) return; // safety: never skip more than once per 8 seconds

            _fired = true; // one attempt per track
            Advance(v, idx, seq, remaining);
        }
        catch (Exception e)
        {
            if (_errors++ < 5) Plugin.L.LogError("[autoplay] " + e);
        }
    }

    static int CallInt(JukeboxDatabase db, string method, object id)
    {
        var m = typeof(JukeboxDatabase).GetMethod(method);
        return Convert.ToInt32(m.Invoke(db, new object[] { id }));
    }

    // Works out which jukebox this view belongs to
    static object ResolveJukeboxId(JukeboxView v, JukeboxDatabase db)
    {
        if (_jukeboxId != null) return _jukeboxId;
        var isJuke = typeof(JukeboxDatabase).GetMethod("IsJukebox");

        try
        {
            foreach (var p in v.GetType().GetProperties())
            {
                if (p.PropertyType.Name != "ItemInstanceId" || p.GetIndexParameters().Length > 0) continue;
                object val = p.GetValue(v);
                if (val != null && (bool)isJuke.Invoke(db, new object[] { val }))
                {
                    Plugin.L.LogInfo($"[autoplay] jukebox id {val} found on the view ({p.Name})");
                    return _jukeboxId = val;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("[autoplay] id lookup on the view failed: " + e.Message); }

        try
        {
            if (Plugin.LastJukeboxId != null && (bool)isJuke.Invoke(db, new object[] { Plugin.LastJukeboxId }))
            {
                Plugin.L.LogInfo($"[autoplay] jukebox id {Plugin.LastJukeboxId} taken from the jukebox screen");
                return _jukeboxId = Plugin.LastJukeboxId;
            }
        }
        catch { }

        return null;
    }

    static List<int> Candidates(JukeboxDatabase db)
    {
        var list = new List<int>();
        int orig = Plugin.OriginalCount, total = Plugin.TotalCount;
        var scope = Plugin.ApScope.Value;
        if (scope == AutoplayScope.CustomOnly && total <= orig) scope = AutoplayScope.All;

        for (int i = 0; i < total; i++)
        {
            bool custom = i >= orig;
            if (scope == AutoplayScope.CustomOnly && !custom) continue;
            if (scope == AutoplayScope.GameOnly && custom) continue;
            if (!custom && (((uint)db.UnlockedPlaylistsMask >> i) & 1u) == 0) continue; // still-locked game tracks
            list.Add(i);
        }
        return list;
    }

    static int PickSequential(List<int> cands, int current)
    {
        foreach (int i in cands) if (i > current) return i;
        return cands[0]; // wrap around
    }

    static int PickShuffle(List<int> cands, int current)
    {
        Bag.RemoveAll(i => !cands.Contains(i) || i == current);
        if (Bag.Count == 0)
        {
            Bag.AddRange(cands.Where(i => i != current));
            for (int i = Bag.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                int tmp = Bag[i]; Bag[i] = Bag[j]; Bag[j] = tmp;
            }
        }
        if (Bag.Count == 0) return -1;
        int next = Bag[0];
        Bag.RemoveAt(0);
        return next;
    }

    static string TitleOf(int slot)
    {
        try { return GameConfiguration.Instance.JukeboxConfiguration.Playlists[slot].DisplayName; }
        catch { return "?"; }
    }

    static void Advance(JukeboxView v, int current, int seq, float remaining)
    {
        var db = Plugin.LiveDb;
        if (db == null) { Plugin.L.LogWarning("[autoplay] no live database known yet - skipped"); return; }

        object id = ResolveJukeboxId(v, db);
        if (id == null) { Plugin.L.LogWarning("[autoplay] could not work out which jukebox this is - skipped"); return; }

        // The database must still agree with what the view shows; otherwise a change is already on its way
        int dbIdx = CallInt(db, "GetCurrentPlaylistIndex", id);
        int dbSeq = CallInt(db, "GetSelectionSequence", id);
        if (dbIdx != current || dbSeq != seq)
        {
            Plugin.L.LogInfo($"[autoplay] skipped: database says slot {dbIdx} seq {dbSeq}, view says slot {current} seq {seq}");
            if (++_mismatches >= 2)
            {
                // Probably holding a stale database (for example after loading another save): wait for the next real selection
                Plugin.IsAuthority = false;
                Plugin.LiveDb = null;
                _mismatches = 0;
                Plugin.L.LogInfo("[autoplay] lost track of the live database; will resume after the next track is selected");
            }
            return;
        }
        _mismatches = 0;

        var cands = Candidates(db);
        if (cands.Count < 2) { Plugin.L.LogInfo("[autoplay] fewer than two tracks to choose from - nothing to do"); return; }

        int next = Plugin.ApMode.Value == AutoplayMode.Shuffle ? PickShuffle(cands, current) : PickSequential(cands, current);
        if (next < 0 || next == current) return;

        Plugin.L.LogInfo($"[autoplay] '{TitleOf(current)}' (slot {current}) ends in {remaining:F1}s -> next: '{TitleOf(next)}' (slot {next}), mode {Plugin.ApMode.Value}");

        var setM = typeof(JukeboxDatabase).GetMethod("SetPlayingPlaylist");
        setM.Invoke(db, new object[] { id, next });
        _lastAdvanceAt = Time.realtimeSinceStartup;

        int now = CallInt(db, "GetCurrentPlaylistIndex", id);
        Plugin.L.LogInfo($"[autoplay] database now reports slot {now}" + (now == next ? "" : " - the change did not take"));
    }
}

// ---------------------------------------------------------------------------------------------
// Co-op support and diagnostics
// ---------------------------------------------------------------------------------------------

static class Coop
{
    public static object ObservedOk;       // a "rules passed" result seen on a normal selection that went on to execute
    public static object LastNormalResult; // the last CheckRules result for a normal (non-custom) slot
    public static object HeuristicOk;      // our best guess at a "passed" value, if nothing has been observed yet
    public static string HeuristicHow;
    static bool _heuristicTried;

    public static float LastLocalClick = -100f;

    public static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");
    public static void Log(string msg) { Plugin.L.LogInfo($"[coop {Stamp()}] {msg}"); }

    public static string ClickAgo()
    {
        float d = Time.realtimeSinceStartup - LastLocalClick;
        return d < 10f ? $"local click {d:F1}s ago" : "NO recent local click (remote operation?)";
    }

    static bool SetMember(object target, string name, object value)
    {
        var t = target.GetType();
        var p = t.GetProperty(name);
        if (p != null && p.CanWrite) { p.SetValue(target, value); return true; }
        var f = t.GetField(name);
        if (f != null) { f.SetValue(target, value); return true; }
        return false;
    }

    // Submits a real SelectJukeboxPlaylistOperation through the game's own operation system.
    // The operation must say WHICH jukebox it is for (ItemId) and WHO is operating it (OperatorCharacterId).
    public static bool TrySubmit(JukeboxScreenView screen, int idx, object[] active)
    {
        try
        {
            var op = new ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation();
            op.PlaylistIndex = idx;

            if (!SetMember(op, "OperatorCharacterId", active[1])) { Plugin.L.LogWarning("OperatorCharacterId not found on the operation"); return false; }
            if (!SetMember(op, "ItemId", active[0])) { Plugin.L.LogWarning("ItemId not found on the operation"); return false; }

            Log($"submitting SelectJukeboxPlaylistOperation for slot {idx} (jukebox={active[0]}, character={active[1]})");
            screen.SubmitOperation(op);
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogError("Operation submit failed: " + (e.InnerException ?? e));
            return false;
        }
    }

    public static bool IsFailure(object result)
    {
        try
        {
            var t = result.GetType();
            var f = t.GetProperty("IsFailure");
            if (f != null) return (bool)f.GetValue(result);
            var s = t.GetProperty("IsSuccess");
            if (s != null) return !(bool)s.GetValue(result);
        }
        catch { }
        return true; // unknown: treat as failed
    }

    // ---- describing things in the log ----

    public static string Describe(object o)
    {
        if (o == null) return "null";
        try
        {
            var t = o.GetType();
            var sb = new StringBuilder(t.Name + "{");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native")) continue;
                var pt = p.PropertyType;
                if (!(pt.IsPrimitive || pt.IsEnum || pt == typeof(string))) continue;
                try { sb.Append(p.Name).Append('=').Append(p.GetValue(o)).Append(' '); } catch { }
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name.StartsWith("Native")) continue;
                var ft = f.FieldType;
                if (!(ft.IsPrimitive || ft.IsEnum || ft == typeof(string))) continue;
                try { sb.Append(f.Name).Append('=').Append(f.GetValue(o)).Append(' '); } catch { }
            }
            sb.Append("}");
            return sb.ToString();
        }
        catch (Exception e) { return "<describe failed: " + e.Message + ">"; }
    }

    // Metadata only (no static reads), safe to call while the plugin loads
    static void LogTypeInfo(Type t)
    {
        var sb = new StringBuilder();
        sb.Append($"[coop] rules result type: {t.FullName} (enum={t.IsEnum}, valueType={t.IsValueType})");
        try
        {
            if (t.IsEnum)
            {
                sb.Append(" members: ").Append(string.Join(", ",
                    Enum.GetNames(t).Select(n => n + "=" + Convert.ToInt64(Enum.Parse(t, n)))));
            }
            else
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var members = new List<string>();
                foreach (var p in t.GetProperties(F).Where(p => !p.Name.StartsWith("Native")))
                    members.Add((p.GetMethod != null && p.GetMethod.IsStatic ? "static " : "") + p.PropertyType.Name + " " + p.Name);
                foreach (var f in t.GetFields(F).Where(f => !f.Name.StartsWith("Native")))
                    members.Add((f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);
                foreach (var m in t.GetMethods(F).Where(m => !m.IsSpecialName && !m.Name.StartsWith("Native") && m.DeclaringType == t))
                    members.Add((m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + m.GetParameters().Length + " args)");
                sb.Append(" members: ").Append(string.Join("; ", members));
            }
        }
        catch (Exception e) { sb.Append(" <failed: " + e.Message + ">"); }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---- finding a "rules passed" value ----

    static readonly string[] OkNames =
        { "Ok", "OK", "Success", "Succeeded", "Valid", "Allowed", "Allow", "Passed", "Pass", "Accepted", "Approved" };

    static bool IsOkName(string n) => OkNames.Any(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase));

    public static object GetHeuristicOk(Type t)
    {
        if (_heuristicTried) return HeuristicOk;
        _heuristicTried = true;
        try
        {
            if (t.IsEnum)
            {
                foreach (var n in Enum.GetNames(t))
                    if (IsOkName(n)) { HeuristicHow = "enum member " + n; return HeuristicOk = Enum.Parse(t, n); }
                HeuristicHow = "enum default value (0)";
                return HeuristicOk = Activator.CreateInstance(t);
            }

            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
            foreach (var p in t.GetProperties(S))
                if (p.PropertyType == t && p.GetIndexParameters().Length == 0 && IsOkName(p.Name))
                { HeuristicHow = "static property " + p.Name; return HeuristicOk = p.GetValue(null); }
            foreach (var f in t.GetFields(S))
                if (f.FieldType == t && IsOkName(f.Name))
                { HeuristicHow = "static field " + f.Name; return HeuristicOk = f.GetValue(null); }
            foreach (var m in t.GetMethods(S))
                if (m.ReturnType == t && m.GetParameters().Length == 0 && IsOkName(m.Name))
                { HeuristicHow = "static method " + m.Name; return HeuristicOk = m.Invoke(null, null); }

            if (t.IsValueType)
            {
                HeuristicHow = "default(struct) - a guess";
                return HeuristicOk = Activator.CreateInstance(t);
            }
        }
        catch (Exception e) { HeuristicHow = "failed: " + e.Message; }
        if (HeuristicHow == null) HeuristicHow = "none found";
        return HeuristicOk;
    }

    // ---- hooks ----

    public static void InstallCheckRules(Harmony h)
    {
        var opType = typeof(ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation);
        var original = opType.GetMethod("CheckRules",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (original == null) { Plugin.L.LogWarning("CheckRules not found on SelectJukeboxPlaylistOperation"); return; }

        LogTypeInfo(original.ReturnType);

        // The return type is only known at runtime, so build the patch for it with a generic helper class
        var patchType = typeof(CheckRulesPatch<>).MakeGenericType(original.ReturnType);
        var postfix = patchType.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        h.Patch(original, postfix: new HarmonyMethod(postfix));
        Plugin.L.LogInfo("Patched SelectJukeboxPlaylistOperation.CheckRules (result type " + original.ReturnType.Name + ")");
    }

    static Type FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!a.GetName().Name.StartsWith("ProtoDivision")) continue;
            var t = a.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }

    // Logs every jukebox operation that goes through the game's request layer
    public static void InstallRequestLoggers(Harmony h)
    {
        var prefix = new HarmonyMethod(typeof(RequestLogger).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var typeName in new[] { "ProtoDivision.Game.GameWorldInput", "ProtoDivision.Game.OperationSchedulerViewSubsystem" })
        {
            var type = FindType(typeName);
            if (type == null) { Plugin.L.LogWarning("type not found: " + typeName); continue; }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "RequestOperation").ToList();
            if (methods.Count == 0) Plugin.L.LogWarning($"{type.Name}.RequestOperation not found");
            foreach (var m in methods)
            {
                try { h.Patch(m, prefix: prefix); Plugin.L.LogInfo($"Patched {type.Name}.{m.Name}"); }
                catch (Exception e) { Plugin.L.LogError($"Patch {type.Name}.{m.Name} failed: {e.Message}"); }
            }
        }
    }

    // Writes a list of operation/networking-related classes of the game, with full member lists for the key ones
    public static void DumpTypes()
    {
        try
        {
            var sb = new StringBuilder();
            var rx = new Regex("network|netcode|multiplayer|replicat|lockstep|session|lobby|peer|steam|transport|remote|scheduler|packet|message|sync|operation|gamer",
                RegexOptions.IgnoreCase);
            var detailed = new[] { "OperationScheduler", "GameWorldInput", "OperationCheckResult" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("ProtoDivision")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    var name = t.FullName ?? t.Name;
                    if (!rx.IsMatch(name)) continue;
                    if (name.EndsWith("Operation") && !name.EndsWith(".Operation")) continue; // hundreds of concrete operations

                    sb.AppendLine($"== {name} : {t.BaseType?.Name}   [{an}]");
                    bool full = detailed.Any(d => name.Contains(d)) || name.EndsWith(".Operation");
                    if (!full) continue;

                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    try { foreach (var p in t.GetProperties(F).Where(x => !x.Name.StartsWith("Native"))) sb.AppendLine($"   prop   {p.PropertyType.Name} {p.Name}"); } catch { }
                    try { foreach (var f in t.GetFields(F).Where(x => !x.Name.StartsWith("Native"))) sb.AppendLine($"   field  {f.FieldType.Name} {f.Name}"); } catch { }
                    try
                    {
                        foreach (var m in t.GetMethods(F).Where(x => !x.IsSpecialName && !x.Name.StartsWith("Native")))
                            sb.AppendLine($"   method {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name))})");
                    }
                    catch { }
                }
            }

            var path = Path.Combine(Paths.BepInExRootPath, "coop_types.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.L.LogInfo($"Wrote {path} ({sb.Length} chars)");
        }
        catch (Exception e) { Plugin.L.LogError("DumpTypes failed: " + e.Message); }
    }
}

// Built for the real CheckRules return type at runtime (see Coop.InstallCheckRules)
static class CheckRulesPatch<T>
{
    static int _normalLogged, _customLogged;

    static string OpcodeName(object result)
    {
        try
        {
            var t = result.GetType();
            object v = t.GetProperty("Opcode")?.GetValue(result) ?? t.GetField("Opcode")?.GetValue(result);
            return v?.ToString();
        }
        catch { return null; }
    }

    static void Postfix(ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation __instance, ref T __result)
    {
        try
        {
            int idx = __instance.PlaylistIndex;
            bool custom = Plugin.IsCustomIndex(idx);
            object boxed = __result;

            if (!custom)
            {
                Coop.LastNormalResult = boxed;
                if (_normalLogged++ < 3)
                    Coop.Log($"CheckRules(playlist {idx}, normal) -> {Coop.Describe(boxed)} ({Coop.ClickAgo()})");
                return;
            }

            // Custom slot: with the jukebox and character set properly, the game's own rules pass except for "locked"
            if (!Coop.IsFailure(boxed))
            {
                if (_customLogged++ < 12)
                    Coop.Log($"CheckRules(playlist {idx}, CUSTOM) passed on its own -> {Coop.Describe(boxed)} ({Coop.ClickAgo()})");
                return;
            }

            // Only the "playlist is locked" failure is ours to override; any other failed rule stays a failure
            string opcode = OpcodeName(boxed);
            if (opcode == null) opcode = Coop.Describe(boxed); // fallback: the description contains "Opcode=<name>"
            bool lockedFailure = opcode.IndexOf("Locked", StringComparison.OrdinalIgnoreCase) >= 0;

            string original = Coop.Describe(boxed);
            string action;

            if (!Plugin.OverrideCheckRules.Value)
            {
                action = "left as a failure (OverrideCheckRules is off)";
            }
            else if (!lockedFailure)
            {
                action = "left as a failure (a different rule failed; only 'locked' is overridden)";
            }
            else
            {
                object ok = Coop.ObservedOk;
                string source = "observed on a normal selection";
                if (ok == null)
                {
                    ok = Coop.GetHeuristicOk(typeof(T));
                    source = "guessed: " + Coop.HeuristicHow;
                }

                if (ok is T typed)
                {
                    __result = typed;
                    action = $"OVERRIDDEN with {Coop.Describe(ok)} ({source})";
                }
                else action = "could not determine a 'passed' value - NOT overridden";
            }

            if (_customLogged++ < 12)
                Coop.Log($"CheckRules(playlist {idx}, CUSTOM, jukebox {__instance.ItemId}, operator {__instance.OperatorCharacterId}) FAILED -> {original}; {action} ({Coop.ClickAgo()})");
        }
        catch (Exception e) { Plugin.L.LogError("CheckRules postfix: " + e); }
    }
}

static class RequestLogger
{
    public static void Prefix(object[] __args, MethodBase __originalMethod)
    {
        try
        {
            if (__args == null) return;
            var ps = __originalMethod.GetParameters();
            for (int i = 0; i < __args.Length && i < ps.Length; i++)
            {
                if (!ps[i].ParameterType.Name.Contains("Operation")) continue;
                if (!(__args[i] is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)) continue;

                // Il2CppSystem.Object gives access to the object's real runtime type name
                string name = "?";
                try
                {
                    var o = b.TryCast<Il2CppSystem.Object>();
                    if (o != null) name = o.GetType().Name + "/" + o.ToString();
                }
                catch { }

                var sel = b.TryCast<ProtoDivision.Barfender.Core.SelectJukeboxPlaylistOperation>();
                if (sel == null && name.IndexOf("Jukebox", StringComparison.OrdinalIgnoreCase) < 0) continue;

                Coop.Log($"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name}: {name}"
                         + (sel != null ? $" playlist={sel.PlaylistIndex}" : "")
                         + $" ({Coop.ClickAgo()})");
            }
        }
        catch (Exception e) { Plugin.L.LogError("RequestLogger: " + e.Message); }
    }
}

// ---------------------------------------------------------------------------------------------
// Track files and decoding
// ---------------------------------------------------------------------------------------------

class Track
{
    public string FilePath, FileName, Title, Artist;
    public long FileSize;
    public int? Requested;          // slot number given in the file name, if any
    public bool KeepOriginalText;   // file name was just a number: keep the game's own title/artist
    public int Slot;                // final playlist index, decided in Apply
    public int Channels, SampleRate;
    public short[] Pcm;             // decoded 16-bit audio

    public string BaseName => System.IO.Path.GetFileNameWithoutExtension(FilePath);

    // "7 - Title - Artist.ext" -> slot 7; "Title - Artist.ext" -> new track; "7.ext" -> replace audio only
    public static Track FromFileName(string path, bool artistFirst)
    {
        var t = new Track { FilePath = path, FileName = System.IO.Path.GetFileName(path) };
        try { t.FileSize = new FileInfo(path).Length; } catch { }
        var name = t.BaseName.Trim();

        var m = Regex.Match(name, @"^(\d{1,2})\s+-\s+(.+)$");
        if (m.Success)
        {
            t.Requested = int.Parse(m.Groups[1].Value);
            name = m.Groups[2].Value.Trim();
        }
        else if (Regex.IsMatch(name, @"^\d{1,2}$"))
        {
            t.Requested = int.Parse(name);
            t.KeepOriginalText = true;
            return t;
        }

        var parts = name.Split(new[] { " - " }, 2, StringSplitOptions.None);
        if (parts.Length == 2)
        {
            string first = parts[0].Trim(), second = parts[1].Trim();
            if (artistFirst) { t.Artist = first; t.Title = second; }
            else { t.Title = first; t.Artist = second; }
        }
        else
        {
            t.Title = name;
        }
        return t;
    }
}

static class AudioDecoder
{
    public static void Decode(Track t)
    {
        switch (System.IO.Path.GetExtension(t.FilePath).ToLowerInvariant())
        {
            case ".wav": DecodeWav(t); break;
            case ".mp3": DecodeMp3(t); break;
            case ".ogg": DecodeOgg(t); break;
            default: throw new Exception("unsupported format");
        }
        if (t.Channels < 1 || t.Channels > 2) throw new Exception($"{t.Channels} channels (only mono and stereo are supported)");
        if (t.Pcm == null || t.Pcm.Length == 0) throw new Exception("no audio data");
    }

    static short ToShort(float f)
    {
        float v = f * 32767f;
        if (v > 32767f) v = 32767f;
        else if (v < -32768f) v = -32768f;
        return (short)v;
    }

    static void DecodeMp3(Track t)
    {
        using var mp3 = new NLayer.MpegFile(t.FilePath);
        t.SampleRate = mp3.SampleRate;
        t.Channels = mp3.Channels;

        int capacity = 1 << 20;
        try { capacity = (int)(mp3.Duration.TotalSeconds * t.SampleRate * t.Channels) + t.SampleRate; } catch { }
        var list = new List<short>(Math.Max(capacity, 1 << 16));

        var buf = new float[t.SampleRate * t.Channels];
        int n;
        while ((n = mp3.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) list.Add(ToShort(buf[i]));
        t.Pcm = list.ToArray();
    }

    static void DecodeOgg(Track t)
    {
        using var ogg = new NVorbis.VorbisReader(t.FilePath);
        t.SampleRate = ogg.SampleRate;
        t.Channels = ogg.Channels;

        var list = new List<short>(1 << 20);
        var buf = new float[t.SampleRate * t.Channels];
        int n;
        while ((n = ogg.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) list.Add(ToShort(buf[i]));
        t.Pcm = list.ToArray();
    }

    static void DecodeWav(Track t)
    {
        using var br = new BinaryReader(File.OpenRead(t.FilePath));
        if (new string(br.ReadChars(4)) != "RIFF") throw new Exception("not a RIFF/WAV file");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new Exception("not a WAVE file");

        int fmtTag = 0, ch = 0, rate = 0, bits = 0;
        byte[] data = null;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            long next = br.BaseStream.Position + size + (size & 1);

            if (id == "fmt ")
            {
                fmtTag = br.ReadInt16(); ch = br.ReadInt16(); rate = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                if (fmtTag == unchecked((short)0xFFFE) && size >= 26)
                {
                    br.ReadInt16(); br.ReadInt16(); br.ReadInt32();
                    fmtTag = br.ReadInt16(); // real format code from the sub-format GUID
                }
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            br.BaseStream.Position = next;
        }

        if (data == null || ch <= 0 || rate <= 0 || bits < 8) throw new Exception("missing fmt or data chunk");

        int bytesPer = bits / 8;
        int n = data.Length / bytesPer;
        n -= n % ch;
        var s = new short[n];

        for (int i = 0; i < n; i++)
        {
            int o = i * bytesPer;
            if (fmtTag == 1 && bits == 16) s[i] = BitConverter.ToInt16(data, o);
            else if (fmtTag == 1 && bits == 8) s[i] = (short)((data[o] - 128) << 8);
            else if (fmtTag == 1 && bits == 24) s[i] = (short)((data[o + 2] << 24 | data[o + 1] << 16 | data[o] << 8) >> 16);
            else if (fmtTag == 1 && bits == 32) s[i] = (short)(BitConverter.ToInt32(data, o) >> 16);
            else if (fmtTag == 3 && bits == 32) s[i] = ToShort(BitConverter.ToSingle(data, o));
            else throw new Exception($"unsupported WAV format (tag {fmtTag}, {bits}-bit)");
        }

        t.Channels = ch;
        t.SampleRate = rate;
        t.Pcm = s;
    }
}
