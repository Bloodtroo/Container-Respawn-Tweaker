using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using ModSettings;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameContainer = Il2Cpp.Container;

[assembly: MelonInfo(typeof(ContainerRespawnTweaker.MainMod), "Container Respawn Tweaker", "1.0.1", "Bloodtroo")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace ContainerRespawnTweaker
{
    public class MainMod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Settings.instance.AddToModSettings("Container Respawn Tweaker");
            MelonLogger.Msg("Container Respawn Tweaker Loaded");
        }
    }

    internal enum LootDensity
    {
        Low,
        Medium,
        High
    }

    internal enum EmptyChance
    {
        None,
        Low,
        Medium
    }

    internal class Settings : JsonModSettings
    {
        internal static Settings instance = new Settings();

        [Section("Respawn Settings")]
        [Name("Enable Container Respawn")]
        public bool EnableRespawn = true;

        [Name("Respawn Interval (Days)")]
        [Slider(1, 200)]
        public int RespawnDays = 30;

        [Name("Protect Stored Items")]
        [Description("If enabled, containers with items inside will not be refreshed.")]
        public bool ProtectStoredItems = true;

        [Section("Loot Settings")]
        [Name("Loot Density")]
        [Choice("Low", "Medium", "High")]
        public LootDensity Density = LootDensity.Medium;

        [Name("Empty Container Chance")]
        [Choice("None", "Low", "Medium")]
        public EmptyChance EmptyContainerChance = EmptyChance.Low;

        [Section("Debug")]
        [Name("Enable Debug Logging")]
        public bool DebugLog = false;
    }

    internal static class ContainerMemory
    {
        private static readonly Dictionary<string, float> LastRefreshDay =
            new Dictionary<string, float>();

        private static readonly Dictionary<string, int> LastCheckDay =
            new Dictionary<string, int>();

        internal static string GetKey(GameContainer c)
        {
            if (c == null)
                return null;

            string scene = "UnknownScene";

            try
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
            catch { }

            Vector3 pos = c.transform.position;

            return $"{scene}|{c.name}|{pos.x:F1}|{pos.y:F1}|{pos.z:F1}";
        }

        internal static bool ShouldCheckToday(GameContainer c, float currentDay)
        {
            string key = GetKey(c);

            if (string.IsNullOrEmpty(key))
                return false;

            int day = Mathf.FloorToInt(currentDay);

            if (LastCheckDay.TryGetValue(key, out int lastDay))
            {
                if (lastDay == day)
                    return false;
            }

            LastCheckDay[key] = day;
            return true;
        }

        internal static bool ShouldRefresh(GameContainer c, float currentDay, int days)
        {
            string key = GetKey(c);

            if (string.IsNullOrEmpty(key))
                return false;

            if (!LastRefreshDay.ContainsKey(key))
            {
                return currentDay >= days;
            }

            return currentDay - LastRefreshDay[key] >= days;
        }

        internal static void MarkRefreshed(GameContainer c, float currentDay)
        {
            string key = GetKey(c);

            if (!string.IsNullOrEmpty(key))
                LastRefreshDay[key] = currentDay;
        }
    }

    internal static class GameTimeHelper
    {
        internal static float GetCurrentDay()
        {
            try
            {
                return GameManager.GetTimeOfDayComponent().GetDayNumber();
            }
            catch
            {
                return Time.time / 86400f;
            }
        }
    }

    internal static class ReflectionHelper
    {
        internal static object GetMember(object obj, string name)
        {
            if (obj == null)
                return null;

            try
            {
                Type type = obj.GetType();

                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (field != null)
                    return field.GetValue(obj);

                PropertyInfo property = type.GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (property != null && property.CanRead)
                    return property.GetValue(obj, null);
            }
            catch { }

            return null;
        }

        internal static void SetMember(object obj, string name, object value)
        {
            if (obj == null)
                return;

            try
            {
                Type type = obj.GetType();

                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (field != null)
                {
                    field.SetValue(obj, value);
                    return;
                }

                PropertyInfo property = type.GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                if (property != null && property.CanWrite)
                    property.SetValue(obj, value, null);
            }
            catch { }
        }

        internal static void CallMethod(object obj, string methodName)
        {
            if (obj == null)
                return;

            try
            {
                MethodInfo method = obj.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                method?.Invoke(obj, null);
            }
            catch (Exception ex)
            {
                if (Settings.instance.DebugLog)
                    MelonLogger.Warning($"{methodName} failed: {ex.Message}");
            }
        }

        internal static int GetIntMember(object obj, string name, int fallback)
        {
            object value = GetMember(obj, name);

            if (value == null)
                return fallback;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }
    }

    internal static class ContainerRefresher
    {
        internal static void TryRefresh(GameContainer c, bool dailyCheckRequired)
        {
            if (c == null)
                return;

            if (!Settings.instance.EnableRespawn)
                return;

            try
            {
                float currentDay = GameTimeHelper.GetCurrentDay();

                if (dailyCheckRequired)
                {
                    if (!ContainerMemory.ShouldCheckToday(c, currentDay))
                        return;
                }

                if (!ContainerMemory.ShouldRefresh(c, currentDay, Settings.instance.RespawnDays))
                    return;

                if (Settings.instance.ProtectStoredItems && GetItemCount(c) > 0)
                {
                    if (Settings.instance.DebugLog)
                        MelonLogger.Msg($"Skipped refresh because container has items: {c.name}");

                    return;
                }

                bool empty = RollEmpty();

                RefreshContainer(c, empty);

                ContainerMemory.MarkRefreshed(c, currentDay);

                if (Settings.instance.DebugLog)
                {
                    MelonLogger.Msg(
                        $"Container refreshed: {c.name} | Empty: {empty} | Density: {Settings.instance.Density}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Container refresh error: {ex}");
            }
        }

        private static int GetItemCount(GameContainer c)
        {
            try
            {
                object items = ReflectionHelper.GetMember(c, "m_Items");

                if (items == null)
                    return 0;

                PropertyInfo countProperty = items.GetType().GetProperty("Count");

                if (countProperty != null)
                {
                    object countValue = countProperty.GetValue(items, null);
                    return Convert.ToInt32(countValue);
                }

                if (items is ICollection collection)
                    return collection.Count;
            }
            catch { }

            return 0;
        }

        private static void RefreshContainer(GameContainer c, bool empty)
        {
            int originalMin = ReflectionHelper.GetIntMember(c, "m_MinRandomItems", 0);
            int originalMax = ReflectionHelper.GetIntMember(c, "m_MaxRandomItems", 2);

            int newMin = originalMin;
            int newMax = originalMax;

            switch (Settings.instance.Density)
            {
                case LootDensity.Low:
                    newMin = 0;
                    newMax = Math.Max(1, originalMax / 2);
                    break;

                case LootDensity.Medium:
                    newMin = originalMin;
                    newMax = originalMax;
                    break;

                case LootDensity.High:
                    newMin = Math.Max(1, originalMin + 1);
                    newMax = Math.Max(originalMax + 2, originalMax * 2);
                    break;
            }

            ReflectionHelper.CallMethod(c, "DestroyAllGear");

            SetContainerAsUnsearched(c);

            if (empty)
            {
                ReflectionHelper.SetMember(c, "m_MinRandomItems", 0);
                ReflectionHelper.SetMember(c, "m_MaxRandomItems", 0);
                ReflectionHelper.SetMember(c, "m_NotPopulated", false);
                return;
            }

            ReflectionHelper.SetMember(c, "m_MinRandomItems", newMin);
            ReflectionHelper.SetMember(c, "m_MaxRandomItems", newMax);
            ReflectionHelper.SetMember(c, "m_NotPopulated", true);

            ReflectionHelper.CallMethod(c, "PopulateContents");

            ReflectionHelper.SetMember(c, "m_MinRandomItems", originalMin);
            ReflectionHelper.SetMember(c, "m_MaxRandomItems", originalMax);
            ReflectionHelper.SetMember(c, "m_NotPopulated", false);

            SetContainerAsUnsearched(c);
        }

        private static void SetContainerAsUnsearched(GameContainer c)
        {
            ReflectionHelper.SetMember(c, "m_Inspected", false);
            ReflectionHelper.SetMember(c, "m_BeenInspected", false);
            ReflectionHelper.SetMember(c, "m_StartInspected", false);
            ReflectionHelper.SetMember(c, "m_ItemLooted", false);
            ReflectionHelper.SetMember(c, "m_Opened", false);
            ReflectionHelper.SetMember(c, "m_StartOpened", false);
            ReflectionHelper.SetMember(c, "m_GearIsOpened", false);
            ReflectionHelper.SetMember(c, "m_HasBeenOpened", false);
            ReflectionHelper.SetMember(c, "m_Searched", false);
            ReflectionHelper.SetMember(c, "m_Looted", false);
            ReflectionHelper.SetMember(c, "m_ContainerLooted", false);
        }

        private static bool RollEmpty()
        {
            int chance = 0;

            switch (Settings.instance.EmptyContainerChance)
            {
                case EmptyChance.None:
                    chance = 0;
                    break;

                case EmptyChance.Low:
                    chance = 15;
                    break;

                case EmptyChance.Medium:
                    chance = 35;
                    break;
            }

            return UnityEngine.Random.Range(0, 100) < chance;
        }
    }

    [HarmonyPatch(typeof(GameContainer), "UpdateContainer")]
    internal class UpdateContainerPatch
    {
        private static void Prefix(GameContainer __instance)
        {
            ContainerRefresher.TryRefresh(__instance, true);
        }
    }

    [HarmonyPatch(typeof(GameContainer), "BeginContainerOpen")]
    internal class BeginContainerOpenPatch
    {
        private static void Prefix(GameContainer __instance)
        {
            ContainerRefresher.TryRefresh(__instance, false);
        }
    }
}
