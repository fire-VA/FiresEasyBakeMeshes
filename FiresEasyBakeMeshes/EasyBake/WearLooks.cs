// 1.2.165 (Fire 11-05): EBM's own copy, so EBM needs no Core. Core carries the same method for its own users; keep the two in step.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// Which of a building piece's own wear models - the objects its prefab's WearNTear.m_new, m_worn and m_broken point
    /// at - a placed copy shows. <see cref="WearLooks"/> works it out the way the game does, field edits included.
    /// </summary>
    internal readonly struct WearLook : IEquatable<WearLook>
    {
        private const byte NewModelBit = 1;
        private const byte WornModelBit = 2;
        private const byte BrokenModelBit = 4;

        public readonly byte Bits;

        public WearLook(byte bits)
        {
            Bits = bits;
        }

        public WearLook(bool showsNewModel, bool showsWornModel, bool showsBrokenModel)
        {
            Bits = (byte)((showsNewModel ? NewModelBit : 0) | (showsWornModel ? WornModelBit : 0) | (showsBrokenModel ? BrokenModelBit : 0));
        }

        public bool ShowsNewModel => (Bits & NewModelBit) != 0;
        public bool ShowsWornModel => (Bits & WornModelBit) != 0;
        public bool ShowsBrokenModel => (Bits & BrokenModelBit) != 0;
        public bool ShowsNoModel => Bits == 0;

        public bool Equals(WearLook other) => Bits == other.Bits;
        public override bool Equals(object obj) => obj is WearLook other && Equals(other);
        public override int GetHashCode() => Bits;
        public static bool operator ==(WearLook left, WearLook right) => left.Bits == right.Bits;
        public static bool operator !=(WearLook left, WearLook right) => left.Bits != right.Bits;

        public override string ToString()
        {
            if (ShowsNoModel) return "no model";
            var models = new List<string>(3);
            if (ShowsNewModel) models.Add("new");
            if (ShowsWornModel) models.Add("worn");
            if (ShowsBrokenModel) models.Add("broken");
            return string.Join(" + ", models.ToArray());
        }
    }

    /// <summary>
    /// ZNetView.LoadFields' field edits (max health, swapped wear models), WearNTear.Awake's world-level scaling, then
    /// SetHealthVisual's switch: new above 75% of max, worn above 25%, broken otherwise. A swapped model reference reaches
    /// the other prefab and leaves the piece's own model as shipped - health -1, max -4 and m_broken swapped shows nothing.
    /// </summary>
    internal static class WearLooks
    {
        public const float NewModelAbove = 0.75f;
        public const float WornModelAbove = 0.25f;

        public static readonly int FieldsKey = ZNetView.CustomFieldsStr.GetStableHashCode();
        public static readonly int WearNTearFieldsKey = (ZNetView.CustomFieldsStr + nameof(WearNTear)).GetStableHashCode();
        public static readonly int MaxHealthKey = FieldKey(nameof(WearNTear.m_health));
        public static readonly int NewModelKey = FieldKey(nameof(WearNTear.m_new));
        public static readonly int WornModelKey = FieldKey(nameof(WearNTear.m_worn));
        public static readonly int BrokenModelKey = FieldKey(nameof(WearNTear.m_broken));

        public static WearLook Resolve(GameObject prefab, ZDO zdo)
        {
            var wear = WearOf(prefab);
            if (wear == null || zdo == null) return AsShipped(wear);

            float maxHealth = wear.m_health;
            string newModel = null, wornModel = null, brokenModel = null;
            if (zdo.GetBool(FieldsKey) && zdo.GetBool(WearNTearFieldsKey))
            {
                if (zdo.GetFloat(MaxHealthKey, out float editedMaxHealth)) maxHealth = editedMaxHealth;
                newModel = ReadString(zdo, NewModelKey);
                wornModel = ReadString(zdo, WornModelKey);
                brokenModel = ReadString(zdo, BrokenModelKey);
            }
            return Simulate(wear, zdo.GetFloat(ZDOVars.s_health, maxHealth), maxHealth, newModel, wornModel, brokenModel);
        }

        /// <summary>
        /// The same answer for saved piece data that is not on a ZDO yet, such as a blueprint's: ZDO keys to values, with
        /// bools stored as ints the way the game stores them.
        /// </summary>
        public static WearLook Resolve(GameObject prefab, IDictionary<int, float> floats, IDictionary<int, int> ints, IDictionary<int, string> strings)
        {
            var wear = WearOf(prefab);
            if (wear == null) return AsShipped(wear);

            float maxHealth = wear.m_health;
            string newModel = null, wornModel = null, brokenModel = null;
            if (IsSet(ints, FieldsKey) && IsSet(ints, WearNTearFieldsKey))
            {
                if (floats != null && floats.TryGetValue(MaxHealthKey, out float editedMaxHealth)) maxHealth = editedMaxHealth;
                newModel = ReadString(strings, NewModelKey);
                wornModel = ReadString(strings, WornModelKey);
                brokenModel = ReadString(strings, BrokenModelKey);
            }
            float health = floats != null && floats.TryGetValue(ZDOVars.s_health, out float storedHealth) ? storedHealth : maxHealth;
            return Simulate(wear, health, maxHealth, newModel, wornModel, brokenModel);
        }

        public static WearLook Healthy(GameObject prefab)
        {
            var wear = WearOf(prefab);
            if (wear == null) return AsShipped(wear);
            return Simulate(wear, wear.m_health, wear.m_health, null, null, null);
        }

        /// <summary>
        /// Whether a node of the prefab is switched on in a copy showing this look: the wear models follow the look, the
        /// 1.0 snow caps are always off (see <see cref="IsSnowCap"/>), and every other object stays the way the prefab
        /// ships it.
        /// </summary>
        public static bool IsShown(GameObject prefab, WearLook look, Transform node)
        {
            var wear = WearOf(prefab);
            var root = prefab.transform;
            for (var current = node; current != null; current = current.parent)
            {
                if (!IsSwitchedOn(current.gameObject, wear, look)) return false;
                if (current == root) return true;
            }
            return true;
        }

        private static int FieldKey(string field) => (nameof(WearNTear) + "." + field).GetStableHashCode();

        private static WearNTear WearOf(GameObject prefab) => prefab != null ? prefab.GetComponent<WearNTear>() : null;

        private static WearLook AsShipped(WearNTear wear)
        {
            if (wear == null) return default(WearLook);
            return ModelStates.Of(wear).ToLook();
        }

        private static WearLook Simulate(WearNTear wear, float health, float maxHealth, string newModel, string wornModel, string brokenModel)
        {
            var states = ModelStates.Of(wear);
            GameObject newReference = Swapped(wear.m_new, newModel);
            GameObject wornReference = Swapped(wear.m_worn, wornModel);
            GameObject brokenReference = Swapped(wear.m_broken, brokenModel);
            if (newReference == null && wornReference == null && brokenReference == null) return states.ToLook();

            if (Game.m_worldLevel > 0 && Game.instance != null)
                maxHealth += Game.m_worldLevel * Game.instance.m_worldLevelPieceHPMultiplier * maxHealth;
            float healthFraction = Mathf.Clamp01(health / maxHealth);

            if (healthFraction > NewModelAbove) states.Switch(newReference, wornReference, brokenReference);
            else if (healthFraction > WornModelAbove) states.Switch(wornReference, newReference, brokenReference);
            else states.Switch(brokenReference, newReference, wornReference);
            return states.ToLook();
        }

        private static GameObject Swapped(GameObject ownModel, string prefabName)
        {
            if (prefabName == null) return ownModel;
            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabName) : null;
            return prefab != null ? prefab : ownModel;
        }

        private static bool IsSwitchedOn(GameObject node, WearNTear wear, WearLook look)
        {
            if (wear != null)
            {
                if (node == wear.m_new) return look.ShowsNewModel;
                if (node == wear.m_worn) return look.ShowsWornModel;
                if (node == wear.m_broken) return look.ShowsBrokenModel;
                if (IsSnowCap(node, wear)) return false;
            }
            return node.activeSelf;
        }

        // Valheim 1.0's snow caps are never on in a fresh copy, whatever the prefab ships them as. WearNTear.Awake
        // switches m_snow, m_snowWorn and m_snowBroken off unconditionally before anything else, and only
        // UpdateSnowVisual turns one back on, above a 0.25 snow buildup that lives on the ZDO. A copy being
        // reproduced from a look alone - a blueprint, a baked mesh - carries no buildup, so the faithful answer is
        // off. Reading activeSelf here would bake a permanent snow cap onto every copy of a prefab that happens to
        // ship them enabled, and it would never melt, because a combined mesh has no renderer to switch.
        private static bool IsSnowCap(GameObject node, WearNTear wear)
        {
            return (wear.m_snow != null && node == wear.m_snow.gameObject)
                || (wear.m_snowWorn != null && node == wear.m_snowWorn.gameObject)
                || (wear.m_snowBroken != null && node == wear.m_snowBroken.gameObject);
        }

        private static string ReadString(ZDO zdo, int key) => zdo.GetString(key, out string value) ? value : null;

        private static string ReadString(IDictionary<int, string> strings, int key)
            => strings != null && strings.TryGetValue(key, out string value) ? value : null;

        private static bool IsSet(IDictionary<int, int> ints, int key) => ints != null && ints.TryGetValue(key, out int value) && value != 0;

        /// <summary>
        /// The piece's own three models and whether each is on. A swapped reference changes nothing here; a missing one
        /// throws in the game, which ends the switch where it stands.
        /// </summary>
        private struct ModelStates
        {
            private GameObject _newModel, _wornModel, _brokenModel;
            private bool _newShown, _wornShown, _brokenShown;

            public static ModelStates Of(WearNTear wear) => new ModelStates
            {
                _newModel = wear.m_new,
                _wornModel = wear.m_worn,
                _brokenModel = wear.m_broken,
                _newShown = wear.m_new != null && wear.m_new.activeSelf,
                _wornShown = wear.m_worn != null && wear.m_worn.activeSelf,
                _brokenShown = wear.m_broken != null && wear.m_broken.activeSelf,
            };

            // A missing worn or broken model is normal - plenty of pieces ship only one - and it just means there is
            // nothing to switch off. It used to return here instead, which abandoned the method before the model that
            // should be ON was switched on, so those pieces resolved to a look with nothing showing at all.
            public void Switch(GameObject shown, GameObject firstHidden, GameObject secondHidden)
            {
                if (firstHidden != null && firstHidden != shown) Set(firstHidden, false);
                if (secondHidden != null && secondHidden != shown) Set(secondHidden, false);
                if (shown != null) Set(shown, true);
            }

            private void Set(GameObject model, bool shown)
            {
                if (_newModel != null && _newModel == model) _newShown = shown;
                if (_wornModel != null && _wornModel == model) _wornShown = shown;
                if (_brokenModel != null && _brokenModel == model) _brokenShown = shown;
            }

            public WearLook ToLook() => new WearLook(_newShown, _wornShown, _brokenShown);
        }
    }
}
