using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using static PlayerSaveData;

namespace ImBlindedByTheLights.HarmonyPatches {
	[HarmonyPatch(typeof(BeatmapDataTransformHelper), nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData))]
	static class BasegameStaticLights {
		public static bool enabled { get; private set; } = false;

		public static bool forcedByPlugin => Config.Instance.enablePlugin && Config.Instance.staticInHeadset && Config.Instance.staticOnDesktop;

		static void Prefix(ref EnvironmentEffectsFilterPreset environmentEffectsFilterPreset) {
			if(forcedByPlugin)
				environmentEffectsFilterPreset = EnvironmentEffectsFilterPreset.NoEffects;

			enabled = environmentEffectsFilterPreset == EnvironmentEffectsFilterPreset.NoEffects;
		}
	}

	// By the time the above is called the lightshow to use has already been decided on, thats done when loading the map.
	// Using a copy of the settings as they are also compared for the cached map data, which needs to be reloaded when this changes
	[HarmonyPatch]
	static class ForceBasegameStaticLightsOnLoad {
		[HarmonyTargetMethods]
		static IEnumerable<MethodBase> TargetMethods() {
			yield return AccessTools.Method(typeof(BeatmapDataLoader), nameof(BeatmapDataLoader.LoadBeatmapDataAsync));
			yield return AccessTools.Method(typeof(BeatmapDataLoader), nameof(BeatmapDataLoader.LoadBeatmapData));
		}

		static void Prefix(ref PlayerSpecificSettings playerSpecificSettings) {
			if(playerSpecificSettings == null || !BasegameStaticLights.forcedByPlugin)
				return;

			playerSpecificSettings = playerSpecificSettings.CopyWith(
				environmentEffectsFilterDefaultPreset: EnvironmentEffectsFilterPreset.NoEffects,
				environmentEffectsFilterExpertPlusPreset: EnvironmentEffectsFilterPreset.NoEffects
			);
		}
	}
}
