using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    // 1.2.208 (Fire: "ebm cannot rely on any other mod ... it needs its own shader and apis so the other mods can work along side it"):
    // EBM never looks Core up any more. Everything Core used to give EBM is now PUSHED in through EbmApi (Core registers when it loads);
    // nobody registering = EBM's own defaults (no line folding, no extra bake exclusions, gravity up, no FrameTimer, no Core ground snow,
    // log boxes as one Debug line, no banner art). What EBM offers Core (its status line, config, help) Core PULLS from EbmApi.
    // The callers keep these names, so nothing else in EBM changed.
    internal static class CoreLink
    {
        internal readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        // pushed in by EbmApi.Register* (main thread, at load)
        internal static Func<string, int, string> LineFit;
        internal static Func<int> ConsoleColumnsSource;
        internal static Func<string, int> PrefixColumnsSource;
        internal static Func<GameObject, bool> BakeExclusion;
        internal static Func<Vector3, Vector3> GravityUp;
        internal static Action<string, long> FrameTimer;
        internal static Func<bool> FrameTimerOn;
        internal static Func<Vector3, float> SnowBase;
        private static readonly List<Action> s_exclusionListeners = new List<Action>();

        internal static bool Present => BakeExclusion != null || LineFit != null || FrameTimer != null;

        internal static void SayIfAbsent() { }

        internal static int ConsoleColumns { get { try { return ConsoleColumnsSource != null ? ConsoleColumnsSource() : 0; } catch { return 0; } } }

        internal static string Fit(string text, int prefixColumns)
        {
            if (LineFit == null) return text;
            try { return LineFit(text, prefixColumns); } catch { return text; }
        }

        internal static int PrefixColumnsFor(string source) { try { return PrefixColumnsSource != null ? PrefixColumnsSource(source) : 0; } catch { return 0; } }

        internal static bool IsExcludedFromBaking(GameObject go)
        {
            if (BakeExclusion == null || EasyBake.ForeignCall.Off("EbmApi", "bake exclusion")) return false;
            try { return BakeExclusion(go); } catch (Exception ex) { EasyBake.ForeignCall.Failed("EbmApi", "bake exclusion", ex); return false; }
        }

        internal static void OnExclusionsChanged(Action handler) { if (handler != null) s_exclusionListeners.Add(handler); }
        internal static void RaiseExclusionsChanged() { foreach (var h in s_exclusionListeners) { try { h(); } catch (Exception ex) { EasyBakeLog.Debug("[EbmApi] exclusions listener threw: " + ex.Message); } } }

        internal static float SnowBaseAt(Vector3 pos) { if (SnowBase == null) return 0f; try { return SnowBase(pos); } catch { return 0f; } }

        internal static bool HasUp => GravityUp != null;
        internal static Vector3 Up(Vector3 pos) { try { return GravityUp != null ? GravityUp(pos) : Vector3.up; } catch { return Vector3.up; } }

        internal static bool TimerEnabled { get { try { return FrameTimer != null && FrameTimerOn != null && FrameTimerOn(); } catch { return false; } } }

        internal static void Record(string label, long ticks)
        {
            if (FrameTimer == null || EasyBake.ForeignCall.Off("EbmApi", "frame timer")) return;
            try { FrameTimer(label, ticks); } catch (Exception ex) { EasyBake.ForeignCall.Failed("EbmApi", "frame timer", ex); }
        }

        internal static void MiniBox(string title, string[] lines) => EasyBakeLog.Debug(title + ": " + string.Join(", ", lines.Select(l => l.Trim())));

        internal static void PrintBanner(Segment[][] lines) { }

        // what EBM offers others to pull (EbmApi.StatusLine / EbmApi.Config)
        internal static Func<string> StatusLine;
        internal static ConfigFile Config;
        internal static void RegisterStatus(string source, Func<string> line) => StatusLine = line;
        internal static void WatchConfig(ConfigFile config) => Config = config;
        internal static void MarkAdvanced(ConfigFile config, Func<ConfigDefinition, bool> isAdvanced) { Config = config; AdvancedKey = isAdvanced; }
        internal static Func<ConfigDefinition, bool> AdvancedKey;

        // help: EBM keeps its sections; a help system pulls them through EbmApi.HelpSections
        internal sealed class HelpWriter
        {
            private readonly System.Text.StringBuilder _sb;
            internal HelpWriter(System.Text.StringBuilder sb) { _sb = sb; }
            public void Header(string text) => _sb.Append("# ").AppendLine(text);
            public void SubHeader(string text) => _sb.Append("## ").AppendLine(text);
            public void AdminHeader(string text) => _sb.Append("## (admin) ").AppendLine(text);
            public void Paragraph(string text) => _sb.AppendLine(text);
            public void Bullet(string text) => _sb.Append("- ").AppendLine(text);
            public void Divider() => _sb.AppendLine("---");
        }

        internal static readonly List<(string group, string title, int order, bool adminOnly, Action<HelpWriter> build)> HelpSections = new List<(string, string, int, bool, Action<HelpWriter>)>();
        internal static void RegisterHelp(string modId, string group, string title, int order, Action<HelpWriter> build, bool adminOnly = false) => HelpSections.Add((group, title, order, adminOnly, build));
    }
}
