using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeContentsNote = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsNote;

namespace EurekaLog;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IClientState client;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle lifecycle;
    private readonly DiagnosticWindow diagnostic;
    private readonly WindowSystem windows = new("EurekaLog");
    private readonly TrackerWindow tracker;
    private readonly Configuration config;
    private readonly UiProgressSource progress;
    private DateTime nextPoll = DateTime.MinValue;
    private bool previewSettings;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IClientState client, IFramework framework, IGameGui gameGui, IAddonLifecycle lifecycle)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.client = client;
        this.framework = framework;
        this.gameGui = gameGui;
        this.lifecycle = lifecycle;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Previous builds could persist ForceShow=true and reopen the overlay outside Eureka.
        // Retire that behavior; the journal is now strictly Eureka-only.
        if (config.ForceShow) { config.ForceShow = false; pluginInterface.SavePluginConfig(config); }
        // A verified game-counter adapter can replace this source without changing the UI.
        progress = new UiProgressSource(gameGui, config, Save);
        diagnostic = new DiagnosticWindow(client, gameGui, progress);
        lifecycle.RegisterListener(AddonEvent.PostSetup, diagnostic.OnAddonEvent);
        lifecycle.RegisterListener(AddonEvent.PostShow, diagnostic.OnAddonEvent);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, diagnostic.OnAddonEvent);
        windows.AddWindow(diagnostic);
        tracker = new TrackerWindow(config, Save, progress);
        windows.AddWindow(tracker);
        pluginInterface.UiBuilder.Draw += Draw;
        commands.AddHandler("/eurekalog", new CommandInfo(Command) { HelpMessage = "Open tracker; /eurekalog config; /eurekalog show; /eurekalog diagnose" });
        framework.Update += Update;
    }

    private void Update(IFramework _)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(2);
        progress.Refresh();
        tracker.IsOpen = client.IsLoggedIn && IsEureka();
    }

    private bool IsEureka()
    {
        // Eureka Anemos, Pagos, Pyros, Hydatos territory IDs.
        var id = client.TerritoryType;
        return id is 732 or 763 or 795 or 827;
    }

    private void Command(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "diagnose": diagnostic.IsOpen = !diagnostic.IsOpen; break;
            case "show": tracker.IsOpen = client.IsLoggedIn && IsEureka(); break;
            case "config": previewSettings = !previewSettings; config.ShowSettings = previewSettings; Save(); break;
            default: previewSettings = !previewSettings; config.ShowSettings = previewSettings; Save(); break;
        }
    }

    private void Draw() => windows.Draw();
    private void Save() => pluginInterface.SavePluginConfig(config);

    public void Dispose()
    {
        lifecycle.UnregisterListener(diagnostic.OnAddonEvent);
        framework.Update -= Update;
        commands.RemoveHandler("/eurekalog");
        pluginInterface.UiBuilder.Draw -= Draw;
        windows.RemoveAllWindows();
    }
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ForceShow { get; set; }
    public bool ShowSettings { get; set; }
    public bool HideCompleted { get; set; }
    public bool ShowRequirements { get; set; } = true;
    public float Scale { get; set; } = 1f;
    public float Opacity { get; set; } = 0.95f;
    public Dictionary<string, int> CachedCounts { get; set; } = new(StringComparer.Ordinal);
    public DateTime LastSyncedUtc { get; set; } = DateTime.MinValue;
    public Dictionary<string, DateTime> VerifiedAtUtc { get; set; } = new(StringComparer.Ordinal);
}

public record Challenge(string Name, int Goal, int? Current);

public interface IProgressSource
{
    IReadOnlyList<Challenge> Challenges { get; }
    string Status { get; }
    void Refresh();
    void ClearSnapshot();
}

// Synchronizes verified objective-name/progress pairs from visible ContentsNote rows.
// Does not infer kill events. Cached values are labeled as last seen.
public sealed unsafe class UiProgressSource : IProgressSource
{
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly Action save;
    private readonly List<Challenge> challenges = new();
    private static readonly (string Name, int Goal)[] Objectives =
    {
        ("Forbidden Ice, Forbidden Lightning I", 30),
        ("Forbidden Ice, Forbidden Lightning II", 60),
        ("Forbidden Fire, Forbidden Earth I", 30),
        ("Forbidden Fire, Forbidden Earth II", 60),
        ("Forbidden Water, Forbidden Wind I", 30),
        ("Forbidden Water, Forbidden Wind II", 60),
        ("Forbidden Bone, Forbidden Ash I", 20),
        ("Forbidden Bone, Forbidden Ash II", 40),
        ("Forbidden Elements I", 10),
        ("Forbidden Elements II", 20),
    };
    private static readonly Regex CountPattern = new(@"^\s*(\d+)\s*/\s*(\d+)\s*$", RegexOptions.Compiled);
    private string lastAttempt = "Open Challenge Log > Field Operations > Eureka to synchronize.";
    // Counts are accumulated only for the current plugin session. CachedCounts remain
    // independent and may be stale; never treat cached values as freshly verified.
    private readonly HashSet<string> verifiedThisSession = new(StringComparer.Ordinal);
    private int lastScanCount;
    private DateTime lastPersistUtc = DateTime.MinValue;
    private string probeState = "Not checked yet";
    private DateTime probeTime = DateTime.MinValue;
    private int probeVisibleTransitions;
    private int probeHiddenTransitions;
    private bool? priorVisible;
    private readonly Queue<string> probeHistory = new();
    public string ProbeStatus => $"Last check (UTC): {(probeTime == DateTime.MinValue ? "never" : probeTime.ToString("u"))} | {probeState}";
    public string ProbeTransitions => $"Visible transitions: {probeVisibleTransitions}; hidden transitions: {probeHiddenTransitions}";
    public IReadOnlyCollection<string> ProbeHistory => probeHistory;
    private void RecordProbe(string state, bool visible)
    {
        probeTime = DateTime.UtcNow;
        probeState = state;
        if (priorVisible == visible) return;
        if (priorVisible.HasValue)
        {
            if (visible) probeVisibleTransitions++; else probeHiddenTransitions++;
        }
        priorVisible = visible;
        probeHistory.Enqueue($"{probeTime:HH:mm:ss} UTC: {state}");
        while (probeHistory.Count > 20) probeHistory.Dequeue();
    }
    public string Status => $"Verified this session: {verifiedThisSession.Count}/10; latest scan: {lastScanCount}/10. {lastAttempt} " +
        (config.LastSyncedUtc == DateTime.MinValue
            ? "No saved snapshot yet." : $"Last saved verification (UTC): {config.LastSyncedUtc:u}. Cached values may be stale.");
    public IReadOnlyList<Challenge> Challenges => challenges;

    public UiProgressSource(IGameGui gameGui, Configuration config, Action save)
    {
        this.gameGui = gameGui; this.config = config; this.save = save;
        config.CachedCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
        config.VerifiedAtUtc ??= new Dictionary<string, DateTime>(StringComparer.Ordinal);
        UpdateChallenges();
    }
    private void UpdateChallenges()
    {
        challenges.Clear();
        foreach (var (name, goal) in Objectives)
            challenges.Add(new Challenge(name, goal, config.CachedCounts.TryGetValue(name, out var value) ? value : null));
    }
    public void ClearSnapshot()
    {
        config.CachedCounts.Clear();
        config.VerifiedAtUtc.Clear();
        config.LastSyncedUtc = DateTime.MinValue;
        lastAttempt = "Saved snapshot cleared. Open the Eureka Challenge Log to synchronize.";
        verifiedThisSession.Clear();
        lastScanCount = 0;
        UpdateChallenges();
        save();
    }

    public void Refresh()
    {
        var address = gameGui.GetAddonByName("ContentsNote");
        if (address.Address == nint.Zero) { RecordProbe("ContentsNote unavailable; no UI source", false); lastScanCount = 0; lastAttempt = "Challenge Log not available; showing saved snapshot."; return; }
        var addon = (AtkUnitBase*)address.Address;
        if (!addon->IsVisible) { RecordProbe("ContentsNote allocated but hidden; no verified background source", false); lastScanCount = 0; lastAttempt = "Challenge Log closed; showing saved snapshot."; return; }
        RecordProbe("ContentsNote visible; reading displayed rows", true);
        var rows = new Dictionary<string, RowTexts>(StringComparer.Ordinal);
        var entries = new List<TextEntry>();
        var visited = new HashSet<nint>();
        var visitedCount = 0;
        Scan(&addon->UldManager, "ContentsNote", 0, visited, rows, entries, ref visitedCount);
        var verified = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedCounts = new HashSet<int>();

        // First, prefer pairs located within the same component (the v0.5 behavior).
        // Preserve the actual scanned text-node index so a fallback cannot reuse a count.
        foreach (var row in rows.Values)
        {
            foreach (var (name, goal) in Objectives)
            {
                if (!row.Entries.Exists(e => e.Value == name)) continue;
                foreach (var candidate in row.Entries)
                {
                    if (!TryCount(candidate.Value, goal, out var current) || usedCounts.Contains(candidate.Index)) continue;
                    verified[name] = current;
                    usedCounts.Add(candidate.Index);
                    break;
                }
            }
        }

        // The first Eureka entry sometimes has a different UI hierarchy from subsequent
        // list rows. Search nearby nodes with a matching parent/grandparent path instead.
        // Only accept an unambiguous closest match; never guess from a distant counter.
        foreach (var (name, goal) in Objectives)
        {
            if (verified.ContainsKey(name)) continue;
            var names = entries.FindAll(e => e.Value == name);
            if (names.Count != 1) continue;
            var title = names[0];
            var candidates = new List<(TextEntry Entry, int Score)>();
            foreach (var entry in entries)
            {
                if (usedCounts.Contains(entry.Index) || !TryCount(entry.Value, goal, out _)) continue;
                var distance = Math.Abs(entry.Index - title.Index);
                if (distance > 14) continue;
                var shared = SharedPathSegments(entry.Path, title.Path);
                if (shared < 2) continue;
                // Strongly prefer the same row ancestry, then close proximity.
                var score = shared * 100 - distance;
                candidates.Add((entry, score));
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (candidates.Count == 0 || (candidates.Count > 1 && candidates[0].Score == candidates[1].Score)) continue;
            var winner = candidates[0].Entry;
            if (TryCount(winner.Value, goal, out var result))
            {
                verified[name] = result;
                usedCounts.Add(winner.Index);
            }
        }
        lastScanCount = verified.Count;
        if (verified.Count == 0)
        {
            lastAttempt = "No verified objective/count pairs visible. Select Field Operations > Eureka.";
            return;
        }
        var changed = false;
        var newlyVerified = false;
        var now = DateTime.UtcNow;
        foreach (var pair in verified)
        {
            if (!config.CachedCounts.TryGetValue(pair.Key, out var old) || old != pair.Value)
            { config.CachedCounts[pair.Key] = pair.Value; changed = true; }
            config.VerifiedAtUtc[pair.Key] = now;
            if (verifiedThisSession.Add(pair.Key)) newlyVerified = true;
        }
        // Persist on first successful read and when values change; avoid disk writes every poll.
        if (changed || newlyVerified || config.LastSyncedUtc == DateTime.MinValue || (now - lastPersistUtc).TotalMinutes >= 5)
        {
            config.LastSyncedUtc = now;
            lastPersistUtc = now;
            save();
            UpdateChallenges();
        }
        lastAttempt = $"Read {verified.Count} visible objective/count pairs; scroll the Eureka list to capture remaining entries.";
    }
    private static bool TryCount(string value, int goal, out int current)
    {
        current = 0;
        var match = CountPattern.Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out current)
            && int.TryParse(match.Groups[2].Value, out var max) && max == goal
            && current >= 0 && current <= goal;
    }

    private static int SharedPathSegments(string a, string b)
    {
        var aa = a.Split('/'); var bb = b.Split('/');
        var count = 0;
        while (count < aa.Length && count < bb.Length && aa[count] == bb[count]) count++;
        return count;
    }

    private sealed record TextEntry(string Path, string Value, int Index);
    private sealed class RowTexts { public List<TextEntry> Entries { get; } = new(); }
    private static void Scan(AtkUldManager* uld, string path, int depth, HashSet<nint> visited,
        Dictionary<string, RowTexts> rows, List<TextEntry> entries, ref int visitedCount)
    {
        if (uld == null || uld->NodeList == null || depth > 8 || visitedCount >= 12000) return;
        var count = Math.Min((int)uld->NodeListCount, 4096);
        for (var i = 0; i < count && visitedCount < 12000; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !visited.Add((nint)node)) continue;
            visitedCount++;
            var nodePath = $"{path}/{i}[id={node->NodeId},type={(int)node->Type}]";
            if ((int)node->Type == 3)
            {
                var value = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (value.Length is > 0 and < 200)
                {
                    var entry = new TextEntry(nodePath, value, entries.Count);
                    entries.Add(entry);
                    if (!rows.TryGetValue(path, out var row)) rows[path] = row = new RowTexts();
                    row.Entries.Add(entry);
                }
            }
            else if ((int)node->Type >= 1000)
            {
                var component = (AtkComponentNode*)node;
                if (component->Component != null)
                    Scan(&component->Component->UldManager, nodePath, depth + 1, visited, rows, entries, ref visitedCount);
            }
        }
    }

}

public sealed class TrackerWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly IProgressSource source;
    private bool confirmClear;
    private bool compact;
    private readonly bool[] expanded = { true, true, true, true, true };

    private static readonly Vector4 Gold = new(.91f, .77f, .52f, 1f);
    private static readonly Vector4 Muted = new(.62f, .70f, .79f, 1f);
    private static readonly Vector4[] ElementColors =
    {
        new(.48f, .76f, 1f, 1f),
        new(.96f, .60f, .39f, 1f),
        new(.40f, .83f, .72f, 1f),
        new(.79f, .62f, .88f, 1f),
        new(.94f, .81f, .49f, 1f),
    };
    private static readonly string[] GroupNames =
    {
        "ICE  &  LIGHTNING", "FIRE  &  EARTH", "WATER  &  WIND", "BONE  &  ASH", "FORBIDDEN ELEMENTS"
    };

    public TrackerWindow(Configuration config, Action save, IProgressSource source)
        : base("Eureka Expedition Journal###EurekaLogOverlay")
    {
        this.config = config;
        this.save = save;
        this.source = source;
        Size = new Vector2(460, 665);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    private static readonly Vector4 Navy = new(.065f, .105f, .16f, 1f);
    private static readonly Vector4 Panel = new(.105f, .155f, .225f, 1f);
    private static readonly Vector4 PanelRaised = new(.145f, .205f, .29f, 1f);
    private static readonly Vector4 Outline = new(.28f, .34f, .41f, 1f);
    private static readonly Vector4 Pale = new(.86f, .90f, .95f, 1f);

    public override void PreDraw() { }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(config.Scale);
        // Alpha is applied to ALL descendants (including nested opaque child panels).
        // Setting only the parent window background alpha left the navy journal opaque.
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(config.Opacity, .35f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.58f, .46f, .29f, .8f));
        ImGui.PushStyleColor(ImGuiCol.Header, PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(.21f, .29f, .39f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, PanelRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.24f, .32f, .43f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f);
        try
        {
            // The whole journal is an opaque navy child panel; ImGui's window
            // background alone is theme-dependent and looked transparent in v1.0.
            if (ImGui.BeginChild("##expedition-journal", new Vector2(0, 0), true))
                DrawJournal();
            ImGui.EndChild();
        }
        finally { ImGui.PopStyleVar(3); ImGui.PopStyleColor(7); }
    }

    private void DrawStat(string id, string value, string label, float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelRaised);
        if (ImGui.BeginChild(id, new Vector2(width, 62f), true))
        {
            var textWidth = ImGui.CalcTextSize(value).X;
            ImGui.SetCursorPosX(Math.Max(8f, (width - textWidth) / 2f));
            ImGui.TextColored(Gold, value);
            var labelWidth = ImGui.CalcTextSize(label).X;
            ImGui.SetCursorPosX(Math.Max(8f, (width - labelWidth) / 2f));
            ImGui.TextColored(Muted, label);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawTrial(Challenge challenge, int index, Vector4 color)
    {
        if (config.HideCompleted && challenge.Current.HasValue && challenge.Current.Value >= challenge.Goal) return;
        var label = index % 2 == 0 ? "TRIAL I" : "TRIAL II";
        var value = challenge.Current.HasValue ? $"{challenge.Current.Value} / {challenge.Goal}" : $"-- / {challenge.Goal}";
        ImGui.TextColored(Pale, label);
        var textWidth = ImGui.CalcTextSize(value).X;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - textWidth - 8f));
        ImGui.TextColored(Gold, value);
        var ratio = challenge.Current.HasValue ? Math.Clamp((float)challenge.Current.Value / challenge.Goal, 0f, 1f) : 0f;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(ratio, new Vector2(-1, compact ? 7f : 9f), string.Empty);
        ImGui.PopStyleColor();
        if (!compact && config.ShowRequirements)
        {
            var requirement = index >= 6 && index < 8
                ? "Ashkin / sprites: equal or higher level"
                : index < 6 ? "Elemental enemies: at most 2 levels below" : "See Challenge Log for eligibility";
            ImGui.TextColored(Muted, requirement);
        }
    }

    private void DrawJournal()
    {
        var challenges = source.Challenges;
        int completed = 0, known = 0;
        foreach (var c in challenges)
        {
            if (!c.Current.HasValue) continue;
            known++;
            if (c.Current.Value >= c.Goal) completed++;
        }

        ImGui.Dummy(new Vector2(1, 4));
        ImGui.TextColored(Gold, "✦  EUREKA LOG");
        ImGui.TextColored(Muted, "E L E M E N T A L   C H A L L E N G E   J O U R N A L");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail().X;
        var cardWidth = Math.Max(70f, (available - 16f) / 3f);
        DrawStat("##stat-objectives", challenges.Count.ToString(), "OBJECTIVES", cardWidth);
        ImGui.SameLine(0, 8f);
        DrawStat("##stat-completed", completed.ToString(), "COMPLETE", cardWidth);
        ImGui.SameLine(0, 8f);
        DrawStat("##stat-known", $"{known}/10", "SAVED", cardWidth);
        ImGui.Spacing();

        // Short, nonintrusive snapshot indicator instead of the full diagnostic
        // paragraph that occupied the top of the old journal.
        var lastSync = config.LastSyncedUtc == DateTime.MinValue
            ? "No saved verification yet"
            : $"Last verified: {config.LastSyncedUtc.ToLocalTime():MMM d, h:mm tt}";
        ImGui.TextColored(Muted, lastSync);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(source.Status);
        ImGui.Spacing();
        if (ImGui.SmallButton(compact ? "Expanded view" : "Compact view")) compact = !compact;
        ImGui.SameLine();
        if (ImGui.SmallButton(config.ShowSettings ? "Close settings" : "Settings"))
        { config.ShowSettings = !config.ShowSettings; save(); }
        ImGui.Spacing();

        for (var group = 0; group < GroupNames.Length; group++)
        {
            var first = group * 2;
            if (first + 1 >= challenges.Count) break;
            var color = ElementColors[group];
            var visible = 0;
            for (var i = first; i < first + 2; i++)
                if (!config.HideCompleted || !challenges[i].Current.HasValue || challenges[i].Current.Value < challenges[i].Goal) visible++;
            var height = !expanded[group] || visible == 0 ? 36f :
                39f + visible * (compact ? 42f : config.ShowRequirements ? 75f : 52f);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);
            if (ImGui.BeginChild($"##group-card-{group}", new Vector2(0, height), true))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable($"{(expanded[group] ? "▾" : "▸")}  {GroupNames[group]}##group{group}", false))
                    expanded[group] = !expanded[group];
                ImGui.PopStyleColor();
                if (expanded[group])
                {
                    ImGui.Spacing();
                    for (var i = first; i < first + 2; i++)
                    {
                        if (config.HideCompleted && challenges[i].Current.HasValue && challenges[i].Current.Value >= challenges[i].Goal) continue;
                        DrawTrial(challenges[i], i, color);
                        ImGui.Spacing();
                    }
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.TextColored(Gold, "COUNTER SYNC");
        ImGui.TextColored(Muted, "Saved Challenge Log snapshot · Open and scroll Eureka to update");
        if (config.ShowSettings) DrawSettings();
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Gold, "JOURNAL SETTINGS");
        var hide = config.HideCompleted;
        if (ImGui.Checkbox("Hide completed", ref hide)) { config.HideCompleted = hide; save(); }
        var requirements = config.ShowRequirements;
        if (ImGui.Checkbox("Show level requirements", ref requirements)) { config.ShowRequirements = requirements; save(); }
        var opacity = config.Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, .35f, 1f)) { config.Opacity = opacity; save(); }
        var scale = config.Scale;
        if (ImGui.SliderFloat("Scale", ref scale, .7f, 1.5f)) { config.Scale = scale; save(); }
        if (ImGui.Button("Clear saved progress")) confirmClear = true;
        if (confirmClear)
        {
            ImGui.SameLine();
            if (ImGui.Button("Confirm")) { source.ClearSnapshot(); confirmClear = false; }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) confirmClear = false;
        }
        ImGui.TextColored(Muted, "Journal visibility: Eureka zones only");
        ImGui.TextColored(Muted, "/eurekalog diagnose opens the separate diagnostic window");
    }

}

// Read-only addon discovery. Uses Dalamud's documented lifecycle callbacks.
// Records addon names and event counts only, never game text or account information.
public sealed unsafe class DiagnosticWindow : Window
{
    private readonly IClientState client;
    private readonly IGameGui gameGui;
    private readonly UiProgressSource progress;
    private readonly List<string> capturedText = new();
    private int inspectedNodes;
    private int inspectedComponents;
    private bool onlyMatches;
    private string textFilter = "";
    private string captureStatus = "Not captured yet.";
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> baseline = new(StringComparer.Ordinal);
    private bool hasBaseline;
    private readonly List<string> nativeSnapshot = new();
    private string nativeStatus = "Not checked. Use the button to sample native ContentsNote state.";
    private DateTime nativeSampleUtc = DateTime.MinValue;

    // Explicitly compare documented native fields. These are not known kill counters.
    private NativeSample? beforeSample;
    private NativeSample? afterSample;
    private readonly List<string> nativeDifferences = new();
    // Experimental: byte-level comparison of the documented native ContentsNote
    // object itself. Unlike the earlier 145-field audit, this can expose changes
    // in fields not yet named by FFXIVClientStructs. It never writes game memory.
    private byte[]? rawBefore;
    private readonly List<string> rawDifferences = new();
    private string rawStatus = "No raw baseline captured.";
    private const int MaxRawDiffLines = 48;

    private unsafe byte[]? ReadRawNative()
    {
        if (!client.IsLoggedIn || client.TerritoryType is not (732 or 763 or 795 or 827))
        {
            rawStatus = "Enter Eureka before sampling.";
            return null;
        }
        try
        {
            var note = NativeContentsNote.Instance();
            if (note == null) { rawStatus = "Native ContentsNote pointer unavailable."; return null; }
            var size = sizeof(NativeContentsNote);
            // Only read the defined object extent; no pointer traversal or memory scanning.
            var data = new byte[size];
            var source = (byte*)note;
            for (var i = 0; i < size; i++) data[i] = source[i];
            return data;
        }
        catch (Exception ex)
        {
            rawStatus = $"Native read failed: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private void CaptureRawBefore()
    {
        rawBefore = ReadRawNative();
        rawDifferences.Clear();
        if (rawBefore != null)
            rawStatus = $"Captured {rawBefore.Length} native bytes at {DateTime.UtcNow:HH:mm:ss} UTC. Close Challenge Log, kill one eligible enemy, then capture AFTER.";
    }

    private void CaptureRawAfter()
    {
        if (rawBefore == null) { rawStatus = "Capture raw BEFORE first."; return; }
        var after = ReadRawNative();
        if (after == null) return;
        rawDifferences.Clear();
        var changed = 0;
        var count = Math.Min(rawBefore.Length, after.Length);
        for (var i = 0; i < count; i++)
        {
            if (rawBefore[i] == after[i]) continue;
            changed++;
            if (rawDifferences.Count < MaxRawDiffLines)
                rawDifferences.Add($"offset 0x{i:X3}: 0x{rawBefore[i]:X2} -> 0x{after[i]:X2}");
        }
        rawStatus = $"Changed {changed}/{count} bytes. " +
            (changed > MaxRawDiffLines ? $"Showing first {MaxRawDiffLines}. " : "") +
            "These are unclassified bytes, NOT confirmed kill counters.";
        if (changed == 0) rawDifferences.Add("No bytes changed inside the native ContentsNote object.");
    }
    private string comparisonStatus = "Capture BEFORE and AFTER with Challenge Log closed.";

    private sealed record NativeSample(DateTime AtUtc, string State, string Tab, int DisplayCount, string[] Ids, string[] Statuses, bool[] Completed);

    private unsafe NativeSample? ReadNativeSample()
    {
        if (!client.IsLoggedIn || client.TerritoryType is not (732 or 763 or 795 or 827))
        {
            nativeStatus = "Enter Eureka before sampling native state.";
            return null;
        }
        try
        {
            var note = NativeContentsNote.Instance();
            if (note == null) { nativeStatus = "Native ContentsNote.Instance() returned null."; return null; }
            var ids = new string[19];
            var statuses = new string[19];
            for (var i = 0; i < 19; i++)
            {
                ids[i] = note->DisplayIds[i].ToString();
                statuses[i] = note->DisplayStatuses[i].ToString();
            }
            // Documented completion bitset. This indicates completed challenges,
            // NOT per-kill progress. Row IDs are not mapped to Eureka here.
            var completed = new bool[104];
            for (var rowId = 1; rowId <= completed.Length; rowId++)
                completed[rowId - 1] = note->IsContentNoteComplete(rowId);
            return new NativeSample(DateTime.UtcNow, note->State.ToString(), note->SelectedTab.ToString(),
                (int)note->DisplayCount, ids, statuses, completed);
        }
        catch (Exception ex)
        {
            nativeStatus = $"Native probe unavailable: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private void CaptureNativeState()
    {
        nativeSnapshot.Clear();
        var sample = ReadNativeSample();
        if (sample == null) return;
        nativeSampleUtc = sample.AtUtc;
        nativeStatus = $"Sampled at {sample.AtUtc:HH:mm:ss} UTC | state={sample.State} | tab={sample.Tab} | displayCount={sample.DisplayCount}";
        for (var i = 0; i < 19; i++)
            nativeSnapshot.Add($"Slot {i:00}: displayId={sample.Ids[i]}, status={sample.Statuses[i]}");
        var completedRows = new List<string>();
        for (var i = 0; i < sample.Completed.Length; i++)
            if (sample.Completed[i]) completedRows.Add((i + 1).ToString());
        nativeSnapshot.Add("Completed challenge row IDs (not kill counts): " +
            (completedRows.Count == 0 ? "none" : string.Join(", ", completedRows)));
    }

    private void CaptureBefore()
    {
        beforeSample = ReadNativeSample();
        afterSample = null;
        nativeDifferences.Clear();
        comparisonStatus = beforeSample == null ? nativeStatus :
            $"BEFORE captured at {beforeSample.AtUtc:HH:mm:ss} UTC. Kill an eligible enemy with Challenge Log closed, then capture AFTER.";
    }

    private void CaptureAfter()
    {
        if (beforeSample == null) { comparisonStatus = "Capture BEFORE first."; return; }
        afterSample = ReadNativeSample();
        if (afterSample == null) { comparisonStatus = nativeStatus; return; }
        nativeDifferences.Clear();
        void Compare(string name, string first, string second)
        {
            if (first != second) nativeDifferences.Add($"{name}: {first} -> {second}");
        }
        Compare("Load state", beforeSample.State, afterSample.State);
        Compare("Selected tab", beforeSample.Tab, afterSample.Tab);
        Compare("Display count", beforeSample.DisplayCount.ToString(), afterSample.DisplayCount.ToString());
        for (var i = 0; i < 19; i++)
        {
            Compare($"Slot {i:00} ID", beforeSample.Ids[i], afterSample.Ids[i]);
            Compare($"Slot {i:00} status", beforeSample.Statuses[i], afterSample.Statuses[i]);
        }
        for (var i = 0; i < beforeSample.Completed.Length; i++)
            Compare($"Challenge row {i + 1} completed", beforeSample.Completed[i].ToString(), afterSample.Completed[i].ToString());
        comparisonStatus = $"BEFORE {beforeSample.AtUtc:HH:mm:ss} / AFTER {afterSample.AtUtc:HH:mm:ss} UTC | " +
            $"{nativeDifferences.Count} changed fields among 145 compared (41 native fields + 104 completion flags). " +
            "Completion flags only indicate finished objectives, NOT partial kill counts.";
        if (nativeDifferences.Count == 0) nativeDifferences.Add("No changes in documented sampled fields. This does not rule out counters elsewhere.");
    }
    private bool onlyNew = true;
    private string filter = "";
    private int totalEvents;

    public DiagnosticWindow(IClientState client, IGameGui gameGui, UiProgressSource progress)
        : base("EurekaLog - Counter Source Audit###EurekaLogDiagnostics")
    {
        this.client = client;
        this.gameGui = gameGui;
        this.progress = progress;
        Size = new Vector2(700, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public void OnAddonEvent(AddonEvent eventType, AddonArgs args)
    {
        // Callback runs on the game thread. Store names only, not UI data.
        var name = args.AddonName;
        if (string.IsNullOrWhiteSpace(name) || name == "NullAddon") return;
        counts.TryGetValue(name, out var oldCount);
        counts[name] = oldCount + 1;
        totalEvents++;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("v0.9 Native Byte Watch: compare the complete native ContentsNote object, including previously unexamined bytes. Read-only; no verified live kill source yet.");
        ImGui.Separator();
        ImGui.TextUnformatted("BACKGROUND SOURCE PROBE (read-only)");
        ImGui.TextWrapped(progress.ProbeStatus);
        ImGui.TextUnformatted(progress.ProbeTransitions);
        ImGui.TextDisabled("Hidden UI is not proof that the underlying Challenge Log counters are readable.");
        if (ImGui.BeginChild("probe-history", new Vector2(0, 92), true))
        {
            foreach (var line in progress.ProbeHistory) ImGui.TextUnformatted(line);
        }
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.TextUnformatted("NATIVE CONTENTSNOTE STATE (experimental, read-only)");
        ImGui.TextWrapped("Take one sample with the Challenge Log open, then close it and sample again. Compare IDs and statuses. These are not verified kill counters.");
        if (ImGui.Button("Sample native state")) CaptureNativeState();
        ImGui.TextWrapped(nativeStatus);
        if (ImGui.BeginChild("native-state-rows", new Vector2(0, 145), true))
        {
            foreach (var line in nativeSnapshot) ImGui.TextUnformatted(line);
            if (nativeSnapshot.Count == 0) ImGui.TextDisabled("No native sample captured.");
        }
        ImGui.EndChild();
        ImGui.TextUnformatted("NATIVE COUNTER SOURCE AUDIT (41 fields + 104 completion flags)");
        ImGui.Separator();
        ImGui.TextWrapped("NEW: RAW NATIVE BYTE WATCH (experimental, read-only)");
        ImGui.TextWrapped("Compare previously unexamined bytes of the native ContentsNote object. This does not scan arbitrary memory or identify counters automatically.");
        if (ImGui.Button("Capture raw BEFORE")) CaptureRawBefore();
        ImGui.SameLine();
        if (ImGui.Button("Capture raw AFTER")) CaptureRawAfter();
        ImGui.TextWrapped(rawStatus);
        foreach (var line in rawDifferences) ImGui.TextUnformatted(line);
        ImGui.Separator();
        if (ImGui.Button("Capture BEFORE kill")) CaptureBefore();
        ImGui.SameLine();
        if (ImGui.Button("Capture AFTER kill")) CaptureAfter();
        ImGui.TextWrapped(comparisonStatus);
        if (ImGui.BeginChild("native-differences", new Vector2(0, 105), true))
        {
            foreach (var line in nativeDifferences) ImGui.TextWrapped(line);
        }
        ImGui.EndChild();
        ImGui.TextDisabled("Do not assume a changed ID or status is an actual kill count.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Territory: {client.TerritoryType} | Logged in: {client.IsLoggedIn}");
        ImGui.TextUnformatted($"Unique observed addons: {counts.Count} | Lifecycle events: {totalEvents}");
        if (ImGui.Button("Capture baseline"))
        {
            baseline.Clear();
            foreach (var name in counts.Keys) baseline.Add(name);
            hasBaseline = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear observations"))
        {
            counts.Clear();
            baseline.Clear();
            totalEvents = 0;
            hasBaseline = false;
        }
        ImGui.Checkbox("Show only new names", ref onlyNew);
        ImGui.InputText("Name filter", ref filter, 80);
        if (!hasBaseline) ImGui.TextDisabled("No baseline captured yet.");
        ImGui.Separator();
        if (ImGui.Button("Scan ContentsNote tree")) CaptureContentsNote();
        ImGui.SameLine();
        if (ImGui.Button("Clear text snapshot")) { capturedText.Clear(); captureStatus = "Cleared."; }
        ImGui.TextWrapped(captureStatus);
        ImGui.Checkbox("Only show likely progress lines", ref onlyMatches);
        ImGui.InputText("Text contains", ref textFilter, 100);
        if (ImGui.BeginChild("text-snapshot", new Vector2(0, 290), true))
        {
            foreach (var line in capturedText)
            {
                if (onlyMatches && !LooksRelevant(line)) continue;
                if (textFilter.Length > 0 && line.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ImGui.TextWrapped(line);
            }
            if (capturedText.Count == 0) ImGui.TextDisabled("No text captured. Open the Challenge Log to its Eureka page and click Read ContentsNote text.");
        }
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.TextDisabled("Addon lifecycle events (below):");
        var names = new List<string>(counts.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        int shown = 0;
        if (ImGui.BeginChild("addon-results", new Vector2(0, 0), true))
        {
            foreach (var name in names)
            {
                bool isNew = hasBaseline && !baseline.Contains(name);
                if (onlyNew && !isNew) continue;
                if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ImGui.TextUnformatted($"{(isNew ? "NEW" : "   ")}  {name}  ({counts[name]} events)");
                shown++;
            }
            if (shown == 0) ImGui.TextDisabled("No matching events observed. Try opening/closing the Challenge Log after capturing the baseline.");
        }
        ImGui.EndChild();
    }
    private static bool LooksRelevant(string line)
    {
        return line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || line.Contains("/30", StringComparison.Ordinal)
            || line.Contains("/60", StringComparison.Ordinal)
            || line.Contains("/20", StringComparison.Ordinal)
            || line.Contains("/40", StringComparison.Ordinal)
            || line.Contains("/10", StringComparison.Ordinal)
            || line.Contains("Eureka", StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureContentsNote()
    {
        capturedText.Clear();
        inspectedNodes = 0;
        inspectedComponents = 0;
        var address = gameGui.GetAddonByName("ContentsNote");
        if (address.Address == nint.Zero)
        {
            captureStatus = "ContentsNote not found. Open Challenge Log first.";
            return;
        }
        var addon = (AtkUnitBase*)address.Address;
        var visited = new HashSet<nint>();
        ScanUld(&addon->UldManager, "ContentsNote", 0, visited);
        captureStatus = $"Scanned {inspectedNodes} nodes across {inspectedComponents} nested components; "
            + $"{capturedText.Count} non-empty text values. Snapshot only, not background counters.";
    }

    // Component node lists are separate from the top-level addon node list.
    // Cap recursion, node count, and output size to keep the diagnostic bounded.
    private void ScanUld(AtkUldManager* uld, string path, int depth, HashSet<nint> visited)
    {
        if (uld == null || depth > 8 || inspectedNodes >= 12000 || capturedText.Count >= 600) return;
        if (uld->NodeList == null) return;
        var count = Math.Min((int)uld->NodeListCount, 4096);
        for (var i = 0; i < count && inspectedNodes < 12000 && capturedText.Count < 600; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !visited.Add((nint)node)) continue;
            inspectedNodes++;
            var nodePath = $"{path}/{i}[id={node->NodeId},type={(int)node->Type}]";
            if ((int)node->Type == 3) // AtkNodeType.Text
            {
                var value = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // Collapse newlines for a compact, copyable diagnostic.
                    value = value.Replace('\r', ' ').Replace('\n', ' ');
                    if (value.Length > 400) value = value[..400] + "...";
                    capturedText.Add($"{nodePath}: {value}");
                }
            }
            else if ((int)node->Type >= 1000) // AtkNodeType component range
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component == null) continue;
                inspectedComponents++;
                ScanUld(&componentNode->Component->UldManager, nodePath, depth + 1, visited);
            }
        }
    }
}
