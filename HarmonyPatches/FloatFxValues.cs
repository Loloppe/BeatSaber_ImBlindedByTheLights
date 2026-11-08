using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Object = UnityEngine.Object;

namespace ImBlindedByTheLights.HarmonyPatches {
	// There is no way of asking the targets what their current value is, which we need to be able to put it back
	static class FloatFxValues {
		static readonly Dictionary<(FloatFxGroupEffectTarget target, int groupId, int elementId), float> values =
			new Dictionary<(FloatFxGroupEffectTarget target, int groupId, int elementId), float>();

		public static void Set(FloatFxGroupEffectTarget target, int groupId, int elementId, float value) => values[(target, groupId, elementId)] = value;

		public static void Set((FloatFxGroupEffectTarget target, int groupId, int elementId) key, float value) => values[key] = value;

		// Everything starts out at 0, see the constructor of FloatFxGroupEffect
		public static float Get((FloatFxGroupEffectTarget target, int groupId, int elementId) key) => values.TryGetValue(key, out var value) ? value : 0f;

		public static (FloatFxGroupEffectTarget target, int groupId, int elementId)[] FindAll() {
			return Object.FindObjectsOfType<FloatFxGroupEffectManager>(true)
				.SelectMany(x => x._floatFxGroupEffects).Select(x => (x._initData.target, x._initData.groupId, x._initData.elementId))
				.Concat(Object.FindObjectsOfType<TriggerFloatFxGroupEffectManager>(true)
					.SelectMany(x => x._floatFxGroupEffects).Select(x => (x._initData.target, x._initData.groupId, x._initData.elementId)))
				.Where(x => x.target != null).Distinct().ToArray();
		}

		public static void Clear() => values.Clear();
	}

	[HarmonyPatch(typeof(FloatFxGroupEffect), nameof(FloatFxGroupEffect.SetValue))]
	static class TrackFloatFxValues {
		static void Prefix(FloatFxGroupEffect.InitData ____initData, float value) =>
			FloatFxValues.Set(____initData.target, ____initData.groupId, ____initData.elementId, value);
	}

	// SetValue is small enough that it might get inlined into this
	[HarmonyPatch(typeof(FloatFxGroupEffect), nameof(FloatFxGroupEffect.HandleFloatFxBeatmapEventCallback))]
	static class TrackFloatFxEventValues {
		static void Prefix(FloatFxGroupEffect.InitData ____initData, FloatFxBeatmapEventData currentEvent) =>
			FloatFxValues.Set(____initData.target, ____initData.groupId, ____initData.elementId, currentEvent.value);
	}

	[HarmonyPatch(typeof(TriggerFloatFxGroupEffect), nameof(TriggerFloatFxGroupEffect.HandleFloatFxBeatmapEventCallback))]
	static class TrackTriggerFloatFxValues {
		static void Prefix(TriggerFloatFxGroupEffect.InitData ____initData, FloatFxBeatmapEventData currentEvent) =>
			FloatFxValues.Set(____initData.target, ____initData.groupId, ____initData.elementId, currentEvent.value);
	}
}
